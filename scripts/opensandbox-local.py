#!/usr/bin/env python3
"""Project-owned trusted local Docker runner. Never starts privileged Docker-in-Docker."""
import argparse
import json
import os
from pathlib import Path
import re
import secrets
import signal
import shutil
import socket
import subprocess
import time
import urllib.request
from opensandbox_config import server_config

ROOT = Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / "scripts/opensandbox-proof/images.json").read_text())
LABEL = "agent-core.disposable"


def run(*args, capture=False, env=None):
    return subprocess.run(args, cwd=ROOT, env=env, check=True,
                          stdout=subprocess.PIPE if capture else None, text=True).stdout


def inspect(name):
    result = subprocess.run(["docker", "container", "inspect", name], capture_output=True, text=True)
    if result.returncode:
        return None
    return json.loads(result.stdout)[0]


def owned(name):
    container = inspect(name)
    if container and container["Config"]["Labels"].get(LABEL) != name:
        raise RuntimeError("Name belongs to an unrelated container; choose another --name.")
    return container


def prepare(args):
    state = ROOT / "local/opensandbox-runners" / args.name
    state.mkdir(parents=True, exist_ok=True)
    secret = state / "server.env"
    if not secret.exists():
        fd = os.open(secret, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
        with os.fdopen(fd, "w") as out:
            out.write("OPENSANDBOX_SERVER_API_KEY=" + secrets.token_hex(32) + "\n")
    if secret.stat().st_mode & 0o077:
        raise RuntimeError("The private server.env must have mode 0600.")
    platform = run("docker", "info", "--format", "{{.OperatingSystem}}", capture=True)
    gateway = ""
    if "Docker Desktop" not in platform:
        gateway = json.loads(run("docker", "network", "inspect", "bridge", capture=True))[0]["IPAM"]["Config"][0]["Gateway"]
    workspace_root = state / "canonical-workspaces"
    workspace_root.mkdir(exist_ok=True)
    workspace_root.chmod(0o700)
    config = server_config("Docker Desktop" in platform, gateway, workspace_root=workspace_root)
    (state / "server.toml").write_text(config)
    return state


def up(args):
    # Upstream sidecar reconciliation is daemon-wide. Concurrent servers can
    # remove each other's provisioning sidecars even with separate databases.
    servers = run("docker", "ps", "--filter", "ancestor=" + PINS["server"], "--format", "{{.Names}}", capture=True).splitlines()
    if any(name != args.name for name in servers):
        raise RuntimeError("One OpenSandbox server per Docker daemon is required. Reuse the existing server or stop its owned runner first.")
    current = owned(args.name)
    if current is None and any(name.startswith("sandbox-") for name in run("docker", "ps", "-a", "--format", "{{.Names}}", capture=True).splitlines()):
        raise RuntimeError("Existing sandbox workloads must be reconciled by their owning server before starting a new runner; unrelated resources are left unchanged.")
    state = prepare(args)
    tag = args.name + "-workbench:local"
    if args.build_workbench:
        run("docker", "build", "--build-arg", f"AGENTCORE_RUNTIME_UID={os.getuid()}", "--build-arg", f"AGENTCORE_RUNTIME_GID={os.getgid()}", "--label", LABEL + "=" + args.name, "-t", tag,
            "-f", "deploy/sandbox/Dockerfile.workbench", "deploy/sandbox")
    for component in ("server", "execd", "egress"):
        if subprocess.run(["docker", "image", "inspect", PINS[component]],capture_output=True).returncode:
            run("docker", "pull", PINS[component])
    current = owned(args.name)
    if current is not None:
        mounts = {m.get("Destination") for m in current.get("Mounts", [])}
        if str(state / "canonical-workspaces") not in mounts or "/opt/agent-core/retirement.py" not in mounts:
            raise RuntimeError("Existing owned runner lacks the canonical workspace/tenant retirement adapter. Explicitly reconcile its workloads and recreate that runner with retained state; it was not started or replaced.")
    if current is None:
        run("docker", "run", "-d", "--name", args.name, "--label", LABEL + "=" + args.name,
            "--network", "bridge", "-p", f"127.0.0.1:{args.port}:8080",
            "--env-file", str(state / "server.env"),
            "--mount", "type=bind,src=/var/run/docker.sock,dst=/var/run/docker.sock",
            "--mount", f"type=bind,src={state}/server.toml,dst=/etc/opensandbox/config.toml,readonly",
            "--mount", f"type=volume,src={args.name}-state,dst=/var/lib/opensandbox",
            "--mount", f"type=bind,src={state}/canonical-workspaces,dst={state}/canonical-workspaces",
            "--env", f"AGENTCORE_SANDBOX_SERVER_IMAGE={PINS['server']}",
            "--env", f"AGENTCORE_SANDBOX_UID={os.getuid()}", "--env", f"AGENTCORE_SANDBOX_GID={os.getgid()}",
            "--env", "AGENTCORE_SANDBOX_RUNNER=1",
            "--mount", f"type=bind,src={ROOT}/deploy/sandbox/server-bootstrap.py,dst=/opt/agent-core/server-bootstrap.py,readonly",
            "--mount", f"type=bind,src={ROOT}/deploy/sandbox/compatibility.py,dst=/opt/agent-core/compatibility.py,readonly",
            "--mount", f"type=bind,src={ROOT}/deploy/sandbox/retirement.py,dst=/opt/agent-core/retirement.py,readonly",
            "--entrypoint", "python", PINS["server"], "/opt/agent-core/server-bootstrap.py")
    elif not current["State"]["Running"]:
        run("docker", "start", args.name)
    if not any(m.get("Destination") == str(state / "canonical-workspaces") for m in owned(args.name).get("Mounts", [])):
        raise RuntimeError("Existing runner needs the mounted-workspace contract. Stop its owned workloads/server, then recreate it with the updated runner; retain the state volume.")
    if not any(m.get("Destination") == "/opt/agent-core/retirement.py" for m in owned(args.name).get("Mounts", [])):
        raise RuntimeError("Existing owned runner lacks the tenant retirement-fence adapter. Explicitly reconcile its workloads and recreate that runner with retained state before tenant provisioning; no existing workload was changed.")
    env = environment(args, state, tag)
    for attempt in range(90):
        try:
            request = urllib.request.Request(env["AGENTCORE_OSB_PROOF_URL"] + "/sandboxes?pageSize=1",
                headers={"OPEN-SANDBOX-API-KEY": env["AGENTCORE_OSB_PROOF_KEY"]})
            with urllib.request.urlopen(request, timeout=2) as response:
                if response.status == 200:
                    break
        except (OSError, TimeoutError):
            if attempt == 89:
                raise RuntimeError("Private authenticated OpenSandbox readiness failed; inspect owned server logs locally.")
            time.sleep(1)
    if env.get("AGENTCORE_OSB_PROOF_IMAGE"):
        (state / "image.json").write_text(json.dumps({"imageId": env["AGENTCORE_OSB_PROOF_IMAGE"], "pins": PINS}, indent=2))
    if args.build_workbench:
        export_inventory(args, state, env["AGENTCORE_OSB_PROOF_IMAGE"])
    print(f"Trusted local server ready on 127.0.0.1:{args.port}; private configuration in {state}.")
    return state, env


def export_inventory(args, state, image):
    command = ["docker", "run", "--rm", "--label", LABEL + "=" + args.name, "--network", "none", "--read-only",
               "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--pids-limit", "64", image]
    inventory = run(*command, "python", "-I", "/usr/share/agent-core/inventory.py", capture=True)
    json.loads(inventory)
    (state / "workbench.spdx.json").write_text(inventory)
    # Retain actual Debian, Python, Node and package notices; never mount Core/host files.
    with (state / "workbench-licenses.tar.gz").open("wb") as output:
        subprocess.run(command + ["python", "-I", "/usr/share/agent-core/inventory.py", "--licenses"],
                       stdout=output, check=True)


def environment(args, state, tag):
    key = (state / "server.env").read_text().strip().split("=", 1)[1]
    env = os.environ.copy()
    env.update(AGENTCORE_OPENSANDBOX_TESTS="1", AGENTCORE_OSB_PROOF_DISPOSABLE="1", AGENTCORE_OSB_PROOF_KEY=key,
        AGENTCORE_OSB_PROOF_URL=f"http://127.0.0.1:{args.port}",
        AGENTCORE_OSB_PROOF_MOUNT_ROOT=str(state / "canonical-workspaces"),
        AGENTCORE_OSB_PROOF_IMAGE=args.image or (run("docker", "image", "inspect", tag, "--format", "{{.Id}}", capture=True).strip() if args.build_workbench else ""),
        AGENTCORE_OSB_PROOF_PRIVATE_TARGET=owned(args.name)["NetworkSettings"]["Networks"]["bridge"]["IPAddress"],
        AGENTCORE_OSB_PROOF_GATEWAY=owned(args.name)["NetworkSettings"]["Networks"]["bridge"]["Gateway"],
        DOTNET_CLI_USE_MSBUILD_SERVER="0", MSBUILDDISABLENODEREUSE="1")
    for field, key in (("ordinary_image", "AGENTCORE_OSB_ORDINARY_IMAGE"), ("alternate_image", "AGENTCORE_OSB_ALTERNATE_IMAGE")):
        if getattr(args, field, None): env[key] = getattr(args, field)
    return env


def down(args):
    if owned(args.name):
        state = ROOT / "local/opensandbox-runners" / args.name
        key = (state / "server.env").read_text().strip().split("=", 1)[1]
        origin = f"http://127.0.0.1:{args.port}"
        headers = {"OPEN-SANDBOX-API-KEY": key}
        # Query this server's durable registry and kill only Core/proof metadata.
        # If cleanup cannot be confirmed, retain the server/volume for TTL recovery.
        request = urllib.request.Request(origin + "/sandboxes?pageSize=100", headers=headers)
        with urllib.request.urlopen(request, timeout=5) as response:
            items = json.load(response)["items"]
        deadline = time.monotonic() + 30
        for item in items:
            metadata = item.get("metadata") or {}
            if not ({"agent-core-generation", "agent-core-proof"} & metadata.keys()):
                continue
            if time.monotonic() >= deadline:
                raise RuntimeError("Owned cleanup remains pending; server retained for reconciliation.")
            request = urllib.request.Request(origin + "/sandboxes/" + item["id"], headers=headers, method="DELETE")
            with urllib.request.urlopen(request, timeout=5):
                pass
        run("docker", "rm", "-f", args.name)
    # Workloads have their own durable TTL/SDK cleanup. Do not remove arbitrary
    # containers, images, bridge networks or volumes from this shared daemon.
    print("Owned server stopped; its state volume and Workbench image are retained.")


def accept(args):
    try:
        state, env = up(args)
        run("dotnet", "run", "--project", "scripts/opensandbox-proof/OpenSandboxProof.csproj", "--", env=env)
        run("dotnet", "test", "tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj",
            "--filter", "FullyQualifiedName~OpenSandboxIntegrationTests|FullyQualifiedName~SandboxStoreTests|FullyQualifiedName~WorkspaceStructureTests",
            "--nologo", "-m:1", "--logger", "trx;LogFileName=opensandbox-acceptance.trx", env=env)
        if args.ui:
            ui_acceptance(args, state, env)
        print("Real SDK, durable lifecycle, Workbench and canonical filesystem acceptance passed.")
    finally:
        down(args)


def ui(args):
    try:
        state, env = up(args)
        ui_acceptance(args,state,env)
    finally:
        down(args)


def stop_process(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=20)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait()


def ui_workspace_root(mount_root):
    # Share the same private qualification namespace accepted by the trusted
    # mount adapter; log/artifact directory labels are not mount authority.
    return Path(mount_root) / secrets.token_hex(16)


def ui_acceptance(args, state, env):
    for port in (args.api_port, args.web_port):
        with socket.socket() as probe:
            probe.settimeout(1)
            if probe.connect_ex(("127.0.0.1", port)) == 0:
                raise RuntimeError("Acceptance port is occupied; choose another --api-port/--web-port. Existing services are left unchanged.")
    run("dotnet", "build", "src/AgentCore.Api/AgentCore.Api.csproj", "--nologo", "-m:1", env=env)
    data = state / ("ui-" + secrets.token_hex(4))
    data.mkdir()
    workspaces = ui_workspace_root(env["AGENTCORE_OSB_PROOF_MOUNT_ROOT"])
    api_url = f"http://127.0.0.1:{args.api_port}"
    web_url = f"http://127.0.0.1:{args.web_port}"
    env = env.copy()
    node = shutil.which("node")
    if not node or not run(node, "--version", capture=True).startswith("v22."):
        raise RuntimeError("Browser acceptance requires the project's supported Node 22 runtime on PATH.")
    env.update(AgentCore__Profile="Synthetic", Persistence__Provider="Sqlite",
        Persistence__ConnectionString=f"Data Source={data}/core.db",
        Persistence__WorkspaceRoot=str(workspaces), Persistence__ArtifactRoot=str(data / "artifacts"),
        Persistence__AttachmentRoot=str(data / "attachments"), Persistence__CredentialProtectionKeyRoot=str(data / "keys"),
        OpenSandbox__Enabled="true", OpenSandbox__Endpoint=env["AGENTCORE_OSB_PROOF_URL"],
        OpenSandbox__ApiKey=env["AGENTCORE_OSB_PROOF_KEY"],
        OpenSandbox__WorkspaceDockerHostRoot=str(workspaces),
        OpenSandbox__AllowLocalImageIds="true", Browser__Enabled="false",
        VITE_DEV_PORT=str(args.web_port), VITE_API_PROXY_TARGET=api_url,
        AGENTCORE_OSB_UI_URL=web_url, AGENTCORE_OSB_UI_STATE=str(data / "browser-state.json"))
    processes = []
    with (data / "core.log").open("a") as core_log, (data / "vite.log").open("a") as vite_log:
        def start_core():
            process = subprocess.Popen(["dotnet", "run", "--project", "src/AgentCore.Api", "--no-build", "--no-restore",
                "--no-launch-profile", "--urls", api_url], cwd=ROOT, env=env, stdout=core_log, stderr=core_log, start_new_session=True)
            processes.append(process)
            wait_url(api_url + "/health", process)
            return process
        try:
            core = start_core()
            vite = subprocess.Popen([node, str(ROOT / "web/node_modules/vite/bin/vite.js"), "--host", "127.0.0.1", "--strictPort"], cwd=ROOT / "web",
                env=env, stdout=vite_log, stderr=vite_log, start_new_session=True)
            processes.append(vite); wait_url(web_url, vite)
            for phase in ("before", "after"):
                env["AGENTCORE_OSB_UI_PHASE"] = phase
                subprocess.run([node, str(ROOT / "web/node_modules/@playwright/test/cli.js"), "test", "--config", "playwright.opensandbox.config.ts"],
                    cwd=ROOT / "web", env=env, check=True)
                if phase == "before":
                    stop_process(core)
                    run("docker", "restart", args.name)
                    # Preserve the server's SQLite volume and workload containers.
                    core = start_core()
        finally:
            for process in reversed(processes):
                stop_process(process)


def wait_url(url, process):
    for attempt in range(90):
        if process.poll() is not None:
            raise RuntimeError("Owned application exited; inspect its local log.")
        try:
            with urllib.request.urlopen(url, timeout=2) as response:
                if response.status == 200:
                    return
        except (OSError, TimeoutError):
            time.sleep(1)
    raise RuntimeError("Owned application readiness timed out.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["up", "down", "accept", "ui"])
    parser.add_argument("--name", default="agent-core-opensandbox-local")
    parser.add_argument("--port", type=int, default=18080)
    parser.add_argument("--image", help="Exact installed local sha256 ID for opt-in tests, never a runtime default.")
    parser.add_argument("--ordinary-image", help="Explicit installed identity-compatible SDK image digest for compatibility cases.")
    parser.add_argument("--alternate-image", help="Explicit installed third compatible Linux image digest for profile switching.")
    parser.add_argument("--build-workbench", action="store_true", help="Explicit optional maintainer fixture build.")
    parser.add_argument("--ui", action="store_true", help="Include browser journeys and actual Core/server restart.")
    parser.add_argument("--api-port", type=int, default=6280)
    parser.add_argument("--web-port", type=int, default=6273)
    args = parser.parse_args()
    if not re.fullmatch(r"agent-core-opensandbox-[a-z0-9-]{1,64}", args.name) or not 1024 <= args.port <= 65535:
        parser.error("Use a project-owned name and an unprivileged local port.")
    if args.command in ("accept","ui") and not (args.build_workbench or args.image):
        parser.error("Acceptance requires --image <exact immutable ID> or explicit --build-workbench fixture preparation.")
    {"up": up, "down": down, "accept": accept, "ui":ui}[args.command](args)


if __name__ == "__main__":
    main()
