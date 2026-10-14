"""Run the real launcher against local HTTP and read-only Docker fixtures."""
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import threading
import unittest


@contextmanager
def ready_store(root, env):
    state = {
        "db": "nopcommerce-demo nopcommerce-db running healthy",
        "web": "nopcommerce-demo nopcommerce-web running",
        "plugin": '{"InstalledPlugins":[{"SystemName":"AgentCore.OrderEvents"}]}',
        "plugin_exit": 0,
        "install_status": 302,
        "install_location": "/",
        "store_status": 200,
    }

    class StoreHandler(BaseHTTPRequestHandler):
        def do_GET(self):
            status = state["install_status"] if self.path == "/install" else state["store_status"]
            self.send_response(status)
            if self.path == "/install" and status == 302:
                self.send_header("Location", state["install_location"])
            self.end_headers()
            self.wfile.write(b"demo fixture")

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), StoreHandler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    script = root / "scripts/nopcommerce-demo.sh"
    script.write_text((Path(__file__).resolve().parents[1] / "nopcommerce-demo.sh")
                      .read_text().replace("5088", str(server.server_port)))
    script.chmod(0o755)
    bin_dir = root / "bin"
    bin_dir.mkdir(exist_ok=True)
    docker = bin_dir / "docker"
    docker.write_text(f"#!{sys.executable}\n" + '''
import json, os, pathlib, sys
args = sys.argv[1:]
with pathlib.Path(os.environ["STORE_TRACE"]).open("a") as output:
    output.write(json.dumps(args) + "\\n")
state = json.loads(pathlib.Path(os.environ["STORE_STATE"]).read_text())
if args[0] == "info":
    sys.exit(0)
if args[0] == "inspect":
    value = state["db" if args[-1].endswith("-db") else "web"]
    if value is None:
        sys.exit(1)
    print(value)
    sys.exit(0)
if args[0] == "ps":
    print("nopcommerce-demo-web")
    sys.exit(0)
if args[0] == "exec":
    print(state["plugin"])
    sys.exit(state["plugin_exit"])
if args[:2] == ["volume", "ls"] or "down" in args:
    sys.exit(0)
print("fixture stops before container initialization", file=sys.stderr)
sys.exit(42)
''')
    docker.chmod(0o755)
    env.update(STORE_TRACE=str(root / "store-trace"), STORE_STATE=str(root / "store-state"))
    env["PATH"] = str(bin_dir) + os.pathsep + env["PATH"]
    (root / "store-state").write_text(json.dumps(state))
    try:
        yield state
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


class NopCommerceReuseTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "scripts").mkdir()
        self.env = dict(os.environ)
        context = ready_store(self.root, self.env)
        self.state = context.__enter__()
        self.addCleanup(context.__exit__, None, None, None)
        self.env_file = self.root / ".env"
        self.env_file.write_text("NOPCOMMERCE_DB_PASSWORD=test-only\nNOPCOMMERCE_ADMIN_PASSWORD=test-only\n")

    def launch(self, *args):
        (self.root / "store-state").write_text(json.dumps(self.state))
        return subprocess.run(["bash", str(self.root / "scripts/nopcommerce-demo.sh"), *args],
                              env=self.env, capture_output=True, text=True, timeout=10)

    def trace(self):
        return [json.loads(line) for line in (self.root / "store-trace").read_text().splitlines()]

    def test_healthy_start_is_read_only_and_repeatable_without_env_credentials(self):
        self.env_file.unlink()
        for _ in range(2):
            result = self.launch("start")
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn("Reusing healthy nopCommerce", result.stdout)
        self.assertFalse(self.env_file.exists())
        self.assertTrue(all(args[0] in ("info", "inspect", "exec") for args in self.trace()))
        self.assertTrue(all(args[2:4] == ["sh", "-c"] and "cat /app/App_Data/plugins.json" in args[4]
                            for args in self.trace() if args[0] == "exec"))

    def test_missing_unhealthy_or_foreign_containers_require_initialization(self):
        for field, value in [("db", None), ("db", "nopcommerce-demo nopcommerce-db running unhealthy"),
                             ("db", "other nopcommerce-db running healthy"),
                             ("web", "nopcommerce-demo nopcommerce-web exited"),
                             ("web", "other nopcommerce-web running")]:
            with self.subTest(field=field, value=value):
                previous = self.state[field]
                self.state[field] = value
                result = self.launch("start")
                self.state[field] = previous
                self.assertEqual(result.returncode, 42, result.stderr)
                self.assertEqual(self.trace()[-1][-2:], ["up", "-d"])

    def test_missing_or_invalid_plugin_requires_initialization(self):
        for catalog, exit_code in [(self.state["plugin"], 1), ("{}", 0), ("invalid", 0),
                                   ('{"InstalledPlugins":null}', 0),
                                   ('{"InstalledPlugins":[{"SystemName":"Other"}]}', 0)]:
            with self.subTest(catalog=catalog, exit_code=exit_code):
                self.state.update(plugin=catalog, plugin_exit=exit_code)
                result = self.launch("start")
                self.assertEqual(result.returncode, 42, result.stderr)
                self.assertEqual(self.trace()[-1][-2:], ["up", "-d"])

    def test_installer_off_origin_redirect_and_failed_store_require_initialization(self):
        for status, location, store_status in [(200, "/", 200), (302, "http://localhost:5088/", 200),
                                                (302, "/install", 200), (302, "/", 503)]:
            with self.subTest(status=status, location=location, store_status=store_status):
                self.state.update(install_status=status, install_location=location, store_status=store_status)
                result = self.launch("start")
                self.assertEqual(result.returncode, 42, result.stderr)
                self.assertEqual(self.trace()[-1][-2:], ["up", "-d"])

    def test_explicit_refresh_initializes_even_when_store_is_healthy(self):
        result = self.launch("start", "--refresh")
        self.assertEqual(result.returncode, 42, result.stderr)
        self.assertEqual([args[0] for args in self.trace()], ["info", "compose"])
        self.assertEqual(self.trace()[-1][-2:], ["up", "-d"])

    def test_reset_remains_explicit_and_scoped(self):
        result = self.launch("reset")
        self.assertEqual(result.returncode, 42, result.stderr)
        compose = [args for args in self.trace() if args[0] == "compose"]
        self.assertEqual([args[-2:] for args in compose], [["down", "-v"], ["up", "-d"]])
        self.assertTrue(all(args[args.index("-p") + 1] == "nopcommerce-demo" for args in compose))

    def test_invalid_options_fail_before_docker(self):
        for args in [(), ("start", "--unknown"), ("stop", "--refresh"), ("reset", "--refresh"),
                     ("start", "--refresh", "extra")]:
            with self.subTest(args=args):
                result = self.launch(*args)
                self.assertEqual(result.returncode, 2)
                self.assertFalse((self.root / "store-trace").exists())


if __name__ == "__main__":
    unittest.main()
