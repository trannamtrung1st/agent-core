"""Exercise Compose progress selection without Docker or provider credentials."""
import os
from pathlib import Path
import pty
import shutil
import subprocess
import tempfile
import unittest


class NopCommerceProgressTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        (self.root / "scripts").mkdir()
        shutil.copyfile(Path(__file__).resolve().parents[1] / "nopcommerce-demo.sh",
                        self.root / "scripts/nopcommerce-demo.sh")
        (self.root / ".env").write_text(
            "NOPCOMMERCE_DB_PASSWORD=test-only\nNOPCOMMERCE_ADMIN_PASSWORD=test-only\n")
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        docker = bin_dir / "docker"
        docker.write_text('''#!/bin/sh
if [ "$1" = info ]; then exit 0; fi
printf '%s\\n' "$@" > "$COMPOSE_ARGS"
echo 'container startup failed' >&2
exit 42
''')
        docker.chmod(0o755)
        self.args_file = self.root / "compose-args"
        self.env = {**os.environ, "PATH": str(bin_dir) + os.pathsep + os.environ["PATH"],
                    "COMPOSE_ARGS": str(self.args_file), "TERM": "xterm-256color"}

    def launch(self, terminal=False):
        master = slave = None
        if terminal:
            master, slave = pty.openpty()
        try:
            result = subprocess.run(
                ["bash", str(self.root / "scripts/nopcommerce-demo.sh"), "start"],
                env=self.env, stdout=subprocess.PIPE,
                stderr=slave if terminal else subprocess.PIPE, text=True, timeout=10)
            if terminal:
                error = os.read(master, 4096).decode()
            else:
                error = result.stderr
            self.assertEqual(result.returncode, 42)
            self.assertIn("container startup failed", error)
            self.assertEqual(result.stdout, "Starting nopCommerce demo containers...\n")
            return self.args_file.read_text().splitlines()
        finally:
            if slave is not None:
                os.close(slave)
            if master is not None:
                os.close(master)

    def test_captured_output_uses_quiet_progress_and_preserves_failure(self):
        args = self.launch()
        self.assertEqual(args[:3], ["compose", "--progress", "quiet"])
        self.assertEqual(args[-2:], ["up", "-d"])

    def test_terminal_updates_progress_in_place(self):
        self.assertEqual(self.launch(terminal=True)[:3], ["compose", "--progress", "tty"])

    def test_dumb_terminal_avoids_control_sequences(self):
        self.env["TERM"] = "dumb"
        self.assertEqual(self.launch(terminal=True)[:3], ["compose", "--progress", "quiet"])


if __name__ == "__main__":
    unittest.main()
