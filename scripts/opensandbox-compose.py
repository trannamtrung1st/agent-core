#!/usr/bin/env python3
"""Project-scoped Compose deployment and private native handoff. Never prints API keys."""

import argparse, hashlib, json, os, re, secrets, subprocess, sys, urllib.error, urllib.request
from opensandbox_config import ROOT, PINS, server_config


def run(*args, capture=True):
    result = subprocess.run(
        args,
        cwd=ROOT,
        text=True,
        stdout=subprocess.PIPE if capture else None,
        stderr=subprocess.PIPE if capture else None,
    )
    if result.returncode:
        raise RuntimeError(
            "Docker operation failed; inspect the selected project locally."
        )
    return result.stdout or ""


def inspect(name):
    result = subprocess.run(["docker", "inspect", name], text=True, capture_output=True)
    return json.loads(result.stdout)[0] if result.returncode == 0 else None


def private_write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_symlink():
        raise RuntimeError("Private configuration cannot be a symbolic link.")
    if path.is_dir():
        raise RuntimeError(f"Expected a configuration file at {path}; directory retained. Move it aside before retrying.")
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    os.fchmod(fd, 0o600)
    with os.fdopen(fd, "w") as output:
        output.write(text)


def write_server_config(path, text):
    # Compose short bind syntax can leave an empty directory at a missing
    # file source. Preserve it before repairing this known startup failure.
    if path.is_symlink():
        raise RuntimeError("Server configuration cannot be a symbolic link; link retained.")
    if path.is_dir():
        if any(path.iterdir()):
            raise RuntimeError(f"Expected a configuration file at {path}; nonempty directory retained. Move it aside before retrying.")
        backup = path.with_name(path.name + ".directory-backup-" + secrets.token_hex(8))
        path.rename(backup)
        print(f"OpenSandbox: preserved empty configuration directory at {backup}; generating server.toml.")
    private_write(path, text)


def state_path(args):
    return ROOT / "local/opensandbox-compose" / args.project


def read_json(path):
    return json.loads(path.read_text()) if path.exists() else None


def prepare(args):
    state = state_path(args)
    state.mkdir(parents=True, exist_ok=True)
    key_path = state / "api-key"
    if not key_path.exists():
        private_write(key_path, secrets.token_hex(32) + "\n")
    if key_path.is_symlink() or key_path.stat().st_mode & 0o077:
        raise RuntimeError("Private key file must have mode 0600 and cannot be a link.")
    desktop = "Docker Desktop" in run(
        "docker", "info", "--format", "{{.OperatingSystem}}"
    )
    gateway = json.loads(run("docker", "network", "inspect", "bridge"))[0]["IPAM"][
        "Config"
    ][0]["Gateway"]
    workspace_root = state / "workspace-mounts"
    workspace_root.mkdir(exist_ok=True)
    workspace_root.chmod(0o1777)  # sticky shared staging; private generation parents, never canonical data
    write_server_config(state / "server.toml", server_config(desktop, gateway, compose=True, workspace_root=workspace_root))
    image = None
    if args.build_workbench:
        inputs = [
            "scripts/opensandbox-proof/images.json",
            "deploy/sandbox/Dockerfile.workbench",
            "deploy/sandbox/requirements.txt",
            "deploy/sandbox/inventory.py",
        ]
        fingerprint = hashlib.sha256(
            b"\0".join((ROOT / name).read_bytes() for name in inputs)
        ).hexdigest()
        # Retain every input-addressed manifest: containerd may discard an old
        # multi-platform image ID when its only mutable tag is overwritten.
        tag = "agent-core-workbench:inputs-" + fingerprint[:24]
        previous = read_json(state / "image.json")
        if (
            previous
            and previous.get("inputsSha256") == fingerprint
            and inspect(previous["imageId"])
        ):
            image = previous["imageId"]
            run("docker", "tag", image, tag)
            print("Workbench: reused the immutable image for unchanged pinned inputs.")
        else:
            run(
                "docker",
                "build",
                "--provenance=false",
                "--label",
                "agent-core.workbench.inputs=" + fingerprint,
                "-t",
                tag,
                "-f",
                "deploy/sandbox/Dockerfile.workbench",
                "deploy/sandbox",
                capture=False,
            )
            image = run("docker", "image", "inspect", tag, "--format", "{{.Id}}").strip()
    for name in ["server", "execd", "egress"]:
        if not inspect(PINS[name]):
            run("docker", "pull", PINS[name], capture=False)
    values = {
        "AGENTCORE_SANDBOX_SERVER_IMAGE": PINS["server"],
        "AGENTCORE_SANDBOX_KEY_FILE": str(key_path),
        "AGENTCORE_SANDBOX_CONFIG_FILE": str(state / "server.toml"),
        "AGENTCORE_SANDBOX_PORT": str(args.port),
        "AGENTCORE_SANDBOX_WORKSPACE_ROOT": str(workspace_root),
    }
    private_write(
        state / "compose.env", "".join(k + "=" + v + "\n" for k, v in values.items())
    )
    if image:
        (state / "image.json").write_text(
            json.dumps(
                {"imageId": image, "pins": PINS, "inputsSha256": fingerprint}, indent=2
            )
        )
    return state, image


def compose(args, state, *tail):
    return run(
        "docker",
        "compose",
        "--project-name",
        args.project,
        "--env-file",
        str(state / "compose.env"),
        "-f",
        "docker-compose.sandbox.yml",
        *tail,
    )


def request(url, key, method="GET", data=None):
    req = urllib.request.Request(
        url, headers={"OPEN-SANDBOX-API-KEY": key}, method=method, data=data
    )
    with urllib.request.urlopen(req, timeout=10) as response:
        body = response.read(1024 * 1024)
        return json.loads(body) if body else None


def readiness(endpoint, key):
    result = request(endpoint + "/sandboxes?pageSize=100", key)
    return result


def native_handoff(state, endpoint, key, image=None, workspace_root=None):
    workspace_root = workspace_root or state / "workspace-mounts"
    private_write(
        state / "native.env",
        f"OpenSandbox__Enabled=true\nOpenSandbox__Endpoint={endpoint}\nOpenSandbox__ApiKey={key}\nOpenSandbox__WorkspaceMountRoot={workspace_root}\n",
    )


def compatible_local(name):
    container = inspect(name)
    if not container:
        raise RuntimeError("Existing server changed; retry status.")
    label = container["Config"].get("Labels") or {}
    if label.get("agent-core.disposable") != name or not re.fullmatch(
        "agent-core-opensandbox-[a-z0-9-]+", name
    ):
        raise RuntimeError(
            "Another server uses this daemon. Reuse its private configuration explicitly; no competing server was started."
        )
    path = ROOT / "local/opensandbox-runners" / name / "server.env"
    if not path.exists() or path.stat().st_mode & 0o077:
        raise RuntimeError(
            "Compatible local runner private configuration is unavailable."
        )
    ports = container["NetworkSettings"]["Ports"].get("8080/tcp") or []
    if len(ports) != 1 or ports[0]["HostIp"] != "127.0.0.1":
        raise RuntimeError(
            "Existing runner does not use the expected private loopback origin."
        )
    endpoint = "http://127.0.0.1:" + ports[0]["HostPort"]
    key = path.read_text().strip().split("=", 1)[1]
    return endpoint, key


def up(args):
    state, image = prepare(args)
    names = run(
        "docker",
        "ps",
        "--filter",
        "ancestor=" + PINS["server"],
        "--format",
        "{{.Names}}",
    ).splitlines()
    foreign = []
    for name in names:
        item = inspect(name)
        labels = (item["Config"].get("Labels") or {}) if item else {}
        if (
            labels.get("com.docker.compose.project") != args.project
            or labels.get("com.docker.compose.service") != "opensandbox"
        ):
            foreign.append(name)
    if foreign:
        if len(foreign) != 1:
            raise RuntimeError(
                "One server per daemon is required; multiple existing servers were left untouched."
            )
        endpoint, key = compatible_local(foreign[0])
        remote = readiness(endpoint, key)
        if args.migrate_local:
            if remote.get("items"):
                raise RuntimeError(
                    "Existing sandbox work must be reconciled before migration. Server and workloads retained."
                )
            private_write(
                state / "api-key", key + "\n"
            )  # Preserve existing native .env/user-secret authentication.
            run(
                "docker", "stop", foreign[0]
            )  # Retain its container, volume, image and private files.
        else:
            workspace_root = ROOT / "local/opensandbox-runners" / foreign[0] / "workspace-mounts"
            container = inspect(foreign[0])
            if not any(m.get("Destination") == str(workspace_root) for m in container.get("Mounts", [])):
                raise RuntimeError("Existing local runner needs the mounted-workspace contract. Reconcile its workloads, then use --migrate-local.")
            native_handoff(state, endpoint, key, image, workspace_root)
            (state / "deployment.json").write_text(
                json.dumps(
                    {"kind": "reused-local", "endpoint": endpoint, "name": foreign[0]}
                )
            )
            print(
                "OpenSandbox: compatible existing local runner reused at "
                + endpoint
                + "; private native handoff prepared."
            )
            return
    compose(args, state, "up", "-d", "--wait", "--wait-timeout", "120", "opensandbox")
    endpoint = f"http://127.0.0.1:{args.port}"
    key = (state / "api-key").read_text().strip()
    readiness(endpoint, key)
    native_handoff(state, endpoint, key, image)
    (state / "deployment.json").write_text(
        json.dumps({"kind": "compose", "endpoint": endpoint, "project": args.project})
    )
    print(
        "OpenSandbox: authenticated Compose server ready at "
        + endpoint
        + ". Persistent metadata retained; register and verify a prebuilt image in Admin."
    )


def state_config(args):
    state = state_path(args)
    record = read_json(state / "deployment.json")
    if not record:
        return state, None, None
    if record["kind"] == "reused-local":
        endpoint, key = compatible_local(record["name"])
    else:
        endpoint = record["endpoint"]
        key = (state / "api-key").read_text().strip()
    return state, record, (endpoint, key)


def status(args):
    state, record, connection = state_config(args)
    if not record:
        print("OpenSandbox: not configured through the Compose helper")
        return
    readiness(*connection)
    print("OpenSandbox: Ready (" + record["kind"] + ") " + connection[0])
    print(
        "Private handoff: "
        + str(state / "native.env")
        + "; metadata/image persist across stop."
    )


def cleanup_core(api_url):
    # The dev launcher supplies its verified, project-owned native API origin.
    uri = urllib.parse.urlparse(api_url)
    if (
        uri.scheme != "http"
        or uri.hostname not in ["127.0.0.1", "localhost"]
        or uri.path not in ["", "/"]
    ):
        raise RuntimeError("Cleanup requires the owned loopback API origin.")
    with urllib.request.urlopen(
        urllib.request.Request(
            api_url + "/api/v1/local/owner-capability", method="POST"
        ),
        timeout=5,
    ) as response:
        token = json.load(response)["token"]
    headers = {"X-AgentCore-Owner-Capability": token}

    def core(path, method="GET", data=None):
        h = {**headers, **({"Content-Type": "application/json"} if data else {})}
        with urllib.request.urlopen(
            urllib.request.Request(api_url + path, method=method, headers=h, data=data),
            timeout=35,
        ) as response:
            b = response.read()
            return json.loads(b) if b else None

    cursor = None
    while True:
        catalog = core(
            "/api/v2/sessions?includeArchived=true&limit=100"
            + ("&cursor=" + urllib.parse.quote(cursor) if cursor else "")
        )
        for item in catalog["items"]:
            env = core("/api/v2/sessions/" + item["sessionId"] + "/sandbox")
            if env and env["state"] != "Terminated":
                core(
                    "/api/v2/sessions/" + item["sessionId"] + "/sandbox/reset",
                    "POST",
                    json.dumps({"expectedGeneration": env["generation"], "expectedExecutionId": env.get("activeExecutionId") or env.get("lastExecutionId")}).encode(),
                )
        if not catalog.get("hasMore"):
            break
        cursor = catalog["nextCursor"]


def stop(args):
    state, record, connection = state_config(args)
    if not record:
        print("OpenSandbox: no owned deployment to stop")
        return
    if record["kind"] == "reused-local":
        print("OpenSandbox: reused external runner retained; no ownership was claimed.")
        return
    endpoint, key = connection
    owned = inspect(args.project + "-opensandbox-1")
    if not owned:
        raise RuntimeError(
            "Owned Compose container is unavailable; state retained for inspection."
        )
    labels = owned["Config"].get("Labels") or {}
    if (
        labels.get("com.docker.compose.project") != args.project
        or labels.get("com.docker.compose.service") != "opensandbox"
    ):
        raise RuntimeError("Compose ownership changed; no container was stopped.")
    if not owned["State"]["Running"]:
        print(
            "OpenSandbox: owned Compose server is already stopped; durable cleanup resumes on next start."
        )
        return
    if args.api_url:
        cleanup_core(args.api_url)
    if readiness(endpoint, key).get("items"):
        raise RuntimeError(
            "Sandbox cleanup is pending or external workloads remain. Compose server retained for reconciliation."
        )
    compose(args, state, "stop", "opensandbox")
    print(
        "OpenSandbox: owned Compose server stopped; persistent metadata, canonical files, images and volumes retained."
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["prepare", "up", "status", "stop"])
    parser.add_argument("--project", default="agent-core-sandbox")
    parser.add_argument("--port", type=int, default=18080)
    parser.add_argument("--build-workbench", action="store_true", help="Optional maintainer fixture; normal startup never builds workload images.")
    parser.add_argument("--migrate-local", action="store_true")
    parser.add_argument("--api-url")
    args = parser.parse_args()
    if (
        not re.fullmatch(r"agent-core-[a-z0-9-]{1,64}", args.project)
        or not 1024 <= args.port <= 65535
    ):
        parser.error("Use a project-owned name and unprivileged port.")
    try:
        if args.command == "prepare":
            state, _ = prepare(args)
            print(
                "Prepared pinned images and private Compose inputs: "
                + str(state / "compose.env")
            )
        else:
            {"up": up, "status": status, "stop": stop}[args.command](args)
    except Exception as error:
        print(
            str(error)
            if isinstance(error, RuntimeError)
            else "OpenSandbox operation failed; inspect project status and retained evidence. Private values suppressed.",
            file=sys.stderr,
        )
        sys.exit(1)


if __name__ == "__main__":
    main()
