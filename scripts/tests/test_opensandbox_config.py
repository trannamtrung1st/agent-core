"""Portable topology/configuration and project ownership regressions."""

import argparse, importlib.util, sys, tempfile, unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from opensandbox_config import server_config

spec = importlib.util.spec_from_file_location(
    "compose_helper", Path(__file__).resolve().parents[1] / "opensandbox-compose.py"
)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class SandboxComposeTests(unittest.TestCase):
    def test_desktop_and_linux_share_pins_but_use_inspected_reachable_private_proxy_routes(
        self,
    ):
        desktop = server_config(True, "172.17.0.1", True)
        linux = server_config(False, "172.17.0.1", True)
        self.assertIn('host_ip = "host.docker.internal"', desktop)
        self.assertIn('publish_host = "127.0.0.1"', desktop)
        self.assertIn('host_ip = "172.17.0.1"', linux)
        self.assertIn('publish_host = "172.17.0.1"', linux)
        for config in (desktop, linux):
            self.assertIn("resolve_internal = false", config)
            self.assertIn('network_mode = "bridge"', config)
            self.assertIn("sha256:", config)
        for address in ("8.8.8.8", "127.0.0.1"):
            with self.assertRaises(ValueError):
                server_config(False, address, True)

    def test_native_helper_loads_only_the_standalone_infrastructure_compose_file(self):
        args=argparse.Namespace(project='agent-core-sandbox')
        with patch.object(helper,'run',return_value='') as run:
            helper.compose(args,Path('/tmp/private-fixture'),'up','-d','opensandbox')
        command=run.call_args.args
        self.assertEqual(command.count('-f'),1)
        self.assertIn('docker-compose.sandbox.yml',command)
        self.assertNotIn('docker-compose.yml',command)
        self.assertNotIn('docker-compose.app-sandbox.yml',command)
        self.assertNotIn('--remove-orphans',command)

    def test_private_projection_is_atomic_and_read_only_for_the_backend_owner(self):
        import os

        spec = importlib.util.spec_from_file_location(
            "bootstrap",
            Path(__file__).resolve().parents[2] / "deploy/sandbox/server-bootstrap.py",
        )
        bootstrap = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(bootstrap)
        with tempfile.TemporaryDirectory() as temp:
            directory = Path(temp) / "auth"
            try:
                bootstrap.project_key(
                    "private-fixture-key", directory, os.getuid(), os.getgid()
                )
                file = directory / "opensandbox_api_key"
                self.assertEqual(file.read_text(), "private-fixture-key\n")
                self.assertEqual(file.stat().st_mode & 0o777, 0o400)
                self.assertEqual(directory.stat().st_mode & 0o777, 0o500)
                self.assertFalse((directory / "opensandbox_api_key.pending").exists())
                bootstrap.project_key(
                    "replacement-private-key", directory, os.getuid(), os.getgid()
                )
                self.assertEqual(file.read_text(), "replacement-private-key\n")
            finally:
                directory.chmod(0o700)

    def test_private_native_handoff_is_restricted_and_not_emitted(self):
        with tempfile.TemporaryDirectory() as temp:
            state = Path(temp)
            helper.native_handoff(
                state, "http://127.0.0.1:18080", "private-key", "sha256:fixture"
            )
            self.assertEqual((state / "native.env").stat().st_mode & 0o777, 0o600)
            with self.assertRaises(RuntimeError):
                (state / "link").symlink_to(state / "native.env")
                helper.private_write(state / "link", "changed")

    def test_foreign_local_server_cannot_be_adopted(self):
        with patch.object(
            helper,
            "inspect",
            return_value={"Config": {"Labels": {}}, "NetworkSettings": {}},
        ):
            with self.assertRaisesRegex(RuntimeError, "Another server"):
                helper.compatible_local("foreign-opensandbox")

    def test_pending_workload_preserves_server_and_never_runs_compose_stop(self):
        owned = {
            "Config": {
                "Labels": {
                    "com.docker.compose.project": "agent-core-sandbox",
                    "com.docker.compose.service": "opensandbox",
                }
            },
            "State": {"Running": True},
        }
        args = argparse.Namespace(project="agent-core-sandbox", api_url=None)
        with (
            patch.object(
                helper,
                "state_config",
                return_value=(
                    Path("/tmp/fixture"),
                    {"kind": "compose"},
                    ("http://127.0.0.1:18080", "private"),
                ),
            ),
            patch.object(helper, "inspect", return_value=owned),
            patch.object(
                helper, "readiness", return_value={"items": [{"id": "foreign"}]}
            ),
            patch.object(helper, "compose") as compose,
        ):
            with self.assertRaisesRegex(RuntimeError, "pending"):
                helper.stop(args)
            compose.assert_not_called()

    def test_changed_compose_ownership_cannot_be_stopped(self):
        args = argparse.Namespace(project="agent-core-sandbox", api_url=None)
        with (
            patch.object(
                helper,
                "state_config",
                return_value=(
                    Path("/tmp/fixture"),
                    {"kind": "compose"},
                    ("http://127.0.0.1:18080", "private"),
                ),
            ),
            patch.object(
                helper,
                "inspect",
                return_value={
                    "Config": {"Labels": {"com.docker.compose.project": "foreign"}}
                },
            ),
            patch.object(helper, "compose") as compose,
        ):
            with self.assertRaisesRegex(RuntimeError, "ownership"):
                helper.stop(args)
            compose.assert_not_called()


if __name__ == "__main__":
    unittest.main()
