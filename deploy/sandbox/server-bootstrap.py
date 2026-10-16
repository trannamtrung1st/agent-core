#!/usr/bin/env python3
"""Trusted server bootstrap: private auth, app-readable projection and one server per daemon."""

import os
import sys
import urllib.request
from pathlib import Path


def project_key(key, directory=Path("/run/agent-core-auth"), uid=1654, gid=1654):
    """Only the trusted server writes this project volume; Core mounts it read-only."""
    directory.mkdir(parents=True, exist_ok=True)
    os.chmod(directory, 0o700)
    temp = directory / "opensandbox_api_key.pending"
    fd = os.open(temp, os.O_WRONLY | os.O_CREAT | os.O_TRUNC | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "w") as output:
        output.write(key + "\n")
        output.flush()
        os.fsync(output.fileno())
        os.fchown(output.fileno(), uid, gid)
        os.fchmod(output.fileno(), 0o400)
    os.replace(temp, directory / "opensandbox_api_key")
    os.chown(directory, uid, gid)
    os.chmod(directory, 0o500)


def main():
    key = os.environ.get("OPENSANDBOX_SERVER_API_KEY") or Path("/run/secrets/opensandbox_api_key").read_text().strip()
    if len(key) < 32:
        raise SystemExit("A private generated OpenSandbox API key is required.")
    if sys.argv[1:] == ["health"]:
        try:
            request = urllib.request.Request(
                "http://127.0.0.1:8080/sandboxes?pageSize=1",
                headers={"OPEN-SANDBOX-API-KEY": key},
            )
            with urllib.request.urlopen(request, timeout=3) as response:
                return 0 if response.status == 200 else 1
        except Exception:
            return 1
    import docker

    client = docker.from_env()
    self_id = os.environ.get("HOSTNAME", "")
    for container in client.containers.list(
        filters={"ancestor": os.environ["AGENTCORE_SANDBOX_SERVER_IMAGE"]}
    ):
        if not container.id.startswith(self_id):
            raise SystemExit(
                "Another pinned OpenSandbox server is running. Reuse or safely stop its owned deployment first."
            )
    if not os.environ.get("AGENTCORE_SANDBOX_RUNNER"):
        project_key(key, uid=int(os.environ.get("AGENTCORE_SANDBOX_UID", "1654")), gid=int(os.environ.get("AGENTCORE_SANDBOX_GID", "1654")))
    os.environ["OPENSANDBOX_SERVER_API_KEY"] = key
    os.environ["SANDBOX_CONFIG_PATH"] = "/etc/opensandbox/config.toml"
    from opensandbox_server.main import app, app_config
    from compatibility import install
    import uvicorn

    install(app, app_config)
    server = app_config.server
    uvicorn.run(app, host=server.host, port=server.port, date_header=False,
                timeout_keep_alive=server.timeout_keep_alive,
                limit_concurrency=server.limit_concurrency, backlog=server.backlog,
                loop=server.loop, http=server.http,
                timeout_graceful_shutdown=server.timeout_graceful_shutdown)


if __name__ == "__main__":
    sys.exit(main())
