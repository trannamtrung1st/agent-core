"""Shared pinned local server configuration for Compose and the acceptance runner."""

from pathlib import Path
import json

ROOT = Path(__file__).resolve().parents[1]
PINS = json.loads((ROOT / "scripts/opensandbox-proof/images.json").read_text())


def server_config(desktop: bool, gateway: str, compose: bool = False, workspace_root=None) -> str:
    config = (ROOT / "scripts/opensandbox-proof/server.toml.example").read_text()
    if not desktop:
        import ipaddress

        address = ipaddress.ip_address(gateway)
        if address.version != 4 or not address.is_private or address.is_loopback:
            raise ValueError("Use the inspected private Docker bridge gateway.")
        config = config.replace(
            'host_ip = "host.docker.internal"', f'host_ip = "{gateway}"'
        )
        config = config.replace(
            'publish_host = "127.0.0.1"', f'publish_host = "{gateway}"'
        )
    if compose:
        # Server and workloads inhabit different bridges; use authenticated
        # server-proxy routing through the daemon's private published ports.
        config = config.replace("resolve_internal = true", "resolve_internal = false")
    if workspace_root is not None:
        config = config.replace("allowed_host_paths = []", "allowed_host_paths = " + json.dumps([str(Path(workspace_root).resolve())]))
    return config
