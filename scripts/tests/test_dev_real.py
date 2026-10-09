"""Portable launcher regressions; no Docker, ports, credentials or providers."""
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import unittest


class DevRealTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name).resolve()
        (self.root / "scripts").mkdir()
        (self.root / "web").mkdir()
        shutil.copyfile(Path(__file__).resolve().parents[1] / "dev-real.sh", self.root / "scripts/dev-real.sh")
        self.bin = self.root / "bin"
        self.bin.mkdir()
        for name, body in {"dotnet": "exec sleep 300", "pnpm": "exec sleep 300", "curl": "exit 0"}.items():
            self.command(name, body)
        self.env = {**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"]}
        self.foreign = None

    def command(self, name, body):
        file = self.bin / name
        file.write_text("#!/bin/sh\n" + body + "\n")
        file.chmod(0o755)

    def run_script(self, *args):
        return subprocess.run(["bash", str(self.root / "scripts/dev-real.sh"), *args],
            env=self.env, capture_output=True, text=True, timeout=80)

    def pids(self):
        return [int((self.root / "local/dev" / name).read_text()) for name in ("api.pid", "web.pid")]

    def alive(self, pid):
        result = subprocess.run(["ps", "-p", str(pid), "-o", "stat="], capture_output=True, text=True)
        return result.returncode == 0 and "Z" not in result.stdout

    def tearDown(self):
        self.run_script("stop")
        if self.foreign and self.foreign.poll() is None:
            os.killpg(self.foreign.pid, signal.SIGTERM)
            self.foreign.wait(timeout=5)
        self.tmp.cleanup()

    def test_restart_survives_parent_exit_and_stop_returns_success(self):
        first = self.run_script("start", "--api-only")
        self.assertEqual(first.returncode, 0, first.stderr)
        old = self.pids()
        self.assertTrue(all(self.alive(pid) for pid in old))
        restarted = self.run_script("restart", "--api-only")
        self.assertEqual(restarted.returncode, 0, restarted.stderr)
        new = self.pids()
        self.assertTrue(all(self.alive(pid) for pid in new))
        self.assertTrue(all(not self.alive(pid) for pid in old))
        stopped = self.run_script("stop")
        self.assertEqual(stopped.returncode, 0, stopped.stderr)
        self.assertTrue(all(not self.alive(pid) for pid in new))
        self.assertFalse((self.root / "local/dev/api.pid").exists())

    def test_stale_pid_does_not_stop_another_workspaces_server(self):
        foreign_dir = self.root / "other-workspace"
        foreign_dir.mkdir()
        vite = foreign_dir / "vite/bin/vite"
        vite.parent.mkdir(parents=True)
        vite.write_text("#!/bin/sh\nsleep 300 &\nwait\n")
        vite.chmod(0o755)
        self.foreign = subprocess.Popen([str(vite)], cwd=foreign_dir, start_new_session=True)
        state = self.root / "local/dev"
        state.mkdir(parents=True)
        (state / "web.pid").write_text(str(self.foreign.pid))
        started = self.run_script("restart", "--api-only")
        self.assertEqual(started.returncode, 0, started.stderr)
        self.assertIsNone(self.foreign.poll())
        self.assertIn("Ignoring stale PID", started.stderr)
        self.assertTrue(all(self.alive(pid) for pid in self.pids()))

    def test_failed_process_is_reported_without_waiting_for_readiness_timeout(self):
        self.command("dotnet", "exit 42")
        self.command("curl", "exit 1")
        result = self.run_script("start", "--api-only")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("API exited before becoming ready", result.stderr)
        self.assertFalse((self.root / "local/dev/web.pid").exists())

    def test_denied_process_inspection_preserves_tracking_and_fails_stop(self):
        started = self.run_script("start", "--api-only")
        self.assertEqual(started.returncode, 0, started.stderr)
        pids = self.pids()
        self.command("ps", "echo 'ps: Operation not permitted' >&2; exit 1")
        try:
            stopped = self.run_script("stop")
            self.assertNotEqual(stopped.returncode, 0)
            self.assertIn("Cannot inspect PID", stopped.stderr)
            self.assertNotIn("Stopped API and web", stopped.stdout)
            self.assertEqual(self.pids(), pids)
        finally:
            (self.bin / "ps").unlink()
        self.assertTrue(all(self.alive(pid) for pid in pids))
        recovered = self.run_script("stop")
        self.assertEqual(recovered.returncode, 0, recovered.stderr)
        self.assertTrue(all(not self.alive(pid) for pid in pids))

    def test_stop_kills_child_that_outlives_launcher_and_ignores_term(self):
        child_pid_file = self.root / "child.pid"
        child = self.root / "child.py"
        child.write_text("import os, pathlib, signal, time\n"
            "signal.signal(signal.SIGTERM, signal.SIG_IGN)\n"
            f"pathlib.Path({str(child_pid_file)!r}).write_text(str(os.getpid()))\n"
            "time.sleep(300)\n")
        launcher = self.root / "launcher.py"
        launcher.write_text("import subprocess, sys\n"
            f"subprocess.Popen([sys.executable, {str(child)!r}]).wait()\n")
        self.command("pnpm", f'exec "{sys.executable}" "{launcher}"')
        # Shorten only the shutdown deadline, without replacing process probes.
        self.command("seq", "echo 1")
        started = self.run_script("start", "--api-only")
        self.assertEqual(started.returncode, 0, started.stderr)
        for _ in range(100):
            if child_pid_file.exists():
                break
            time.sleep(0.02)
        child_pid = int(child_pid_file.read_text())
        pids = self.pids()
        try:
            stopped = self.run_script("stop")
            self.assertEqual(stopped.returncode, 0, stopped.stderr)
            self.assertTrue(all(not self.alive(pid) for pid in pids))
            self.assertFalse(self.alive(child_pid), "TERM-resistant child survived stop")
            self.assertFalse((self.root / "local/dev/web.pid").exists())
        finally:
            if self.alive(child_pid):
                os.kill(child_pid, signal.SIGKILL)


if __name__ == "__main__":
    unittest.main()
