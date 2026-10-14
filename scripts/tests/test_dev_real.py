"""Portable launcher regressions; no Docker, ports, credentials or providers."""
import os
from pathlib import Path
import shutil
import signal
import socket
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
        for name, body in {"dotnet": "exec sleep 300", "pnpm": "exec sleep 300", "curl": "printf '{\"profile\":\"Real\"}\\n'"}.items():
            self.command(name, body)
        self.env = {**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"]}
        for name in ('API','WEB'):
            with socket.socket() as probe:
                probe.bind(('127.0.0.1',0));self.env['AGENTCORE_DEV_'+name+'_PORT']=str(probe.getsockname()[1])
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

    def sandbox_stubs(self):
        self.command('docker','exit 0')
        (self.root/'scripts/nopcommerce-demo.sh').write_text('#!/bin/sh\necho "store:$1" >> "'+str(self.root/'trace')+'"\n')
        (self.root/'scripts/nopcommerce-demo.sh').chmod(0o755)
        (self.root/'scripts/opensandbox-compose.py').write_text("""import os,pathlib,sys
root=pathlib.Path(__file__).resolve().parents[1]
with (root/'trace').open('a') as output: output.write('sandbox:'+sys.argv[1]+'\\n')
if sys.argv[1]=='up':
 p=root/'local/opensandbox-compose/agent-core-sandbox/native.env';p.parent.mkdir(parents=True,exist_ok=True)
 p.write_text('OpenSandbox__Enabled=true\\nOpenSandbox__Endpoint=http://127.0.0.1:18080\\nOpenSandbox__ApiKey=fixture-private-key\\nOpenSandbox__WorkbenchImage=sha256:fixture\\n');p.chmod(0o600)
if sys.argv[1]=='stop' and os.getenv('FIXTURE_CLEANUP_PENDING')=='1': raise SystemExit(1)
print('OpenSandbox: authenticated fixture readiness')
""")
        self.command('dotnet','case "$*" in *--AgentCore:Profile=Real*) ;; *) exit 40;; esac\ntest "$OpenSandbox__Enabled" = true || exit 41\necho "$OpenSandbox__Endpoint $OpenSandbox__WorkbenchImage" >> "'+str(self.root/'handoff')+'"\nexec sleep 300')

    def test_full_start_restart_status_stop_selects_all_components_and_hands_off_private_settings(self):
        self.sandbox_stubs()
        started=self.run_script('start','--full');self.assertEqual(started.returncode,0,started.stderr)
        old=self.pids()
        for _ in range(100):
            if (self.root/'handoff').exists(): break
            time.sleep(.01)
        self.assertIn('http://127.0.0.1:18080 sha256:fixture',(self.root/'handoff').read_text())
        self.assertNotIn('fixture-private-key',started.stdout)
        self.assertEqual(self.run_script('status').returncode,0)
        restarted=self.run_script('restart','--full');self.assertEqual(restarted.returncode,0,restarted.stderr)
        self.assertTrue(all(not self.alive(pid) for pid in old))
        trace=(self.root/'trace').read_text()
        self.assertNotIn('sandbox:stop',trace)
        self.assertNotIn('store:stop',trace)
        stopped=self.run_script('stop','--full');self.assertEqual(stopped.returncode,0,stopped.stderr)
        trace=(self.root/'trace').read_text();self.assertIn('store:start',trace);self.assertIn('store:stop',trace);self.assertIn('sandbox:stop',trace)

    def test_api_only_with_sandbox_does_not_start_or_stop_store(self):
        self.sandbox_stubs()
        (self.root/'.env').write_text('OpenSandbox__Enabled=false\n')
        result=self.run_script('start','--api-only','--with-sandbox');self.assertEqual(result.returncode,0,result.stderr)
        self.assertNotIn('store:',(self.root/'trace').read_text())
        self.assertEqual(self.run_script('stop','--with-sandbox').returncode,0)
        self.assertNotIn('store:',(self.root/'trace').read_text())

    def test_pending_selected_cleanup_returns_failure_and_stops_native_processes_without_claiming_success(self):
        self.sandbox_stubs();self.assertEqual(self.run_script('start','--api-only','--with-sandbox').returncode,0)
        pids=self.pids();self.env['FIXTURE_CLEANUP_PENDING']='1'
        result=self.run_script('stop','--with-sandbox');self.assertNotEqual(result.returncode,0)
        self.assertIn('cleanup remains pending',result.stderr);self.assertTrue(all(not self.alive(pid) for pid in pids))

    def test_default_starts_only_sandbox_infrastructure_and_native_real_app(self):
        self.sandbox_stubs()
        result=self.run_script('start');self.assertEqual(result.returncode,0,result.stderr)
        trace=(self.root/'trace').read_text();self.assertIn('sandbox:up',trace);self.assertNotIn('store:',trace)
        import json
        state=json.loads((self.root/'local/dev/stack.json').read_text())
        self.assertTrue(state['sandboxSelected']);self.assertFalse(state['storeSelected'])
        self.assertEqual(self.run_script('status').returncode,0)
        self.assertEqual(self.run_script('restart').returncode,0)
        # Ordinary restart reuses infra; it must not stop the server or optional store.
        trace=(self.root/'trace').read_text();self.assertNotIn('sandbox:stop',trace);self.assertNotIn('store:',trace)
        self.assertEqual(self.run_script('stop').returncode,0)
        self.assertNotIn('sandbox:stop',(self.root/'trace').read_text())

    def test_with_store_explicitly_adds_optional_demo(self):
        self.sandbox_stubs()
        result=self.run_script('start','--with-store');self.assertEqual(result.returncode,0,result.stderr)
        trace=(self.root/'trace').read_text();self.assertIn('sandbox:up',trace);self.assertIn('store:start',trace)

    def test_full_restart_executes_real_store_launcher_without_reinitializing_healthy_demo(self):
        from scripts.tests.test_nopcommerce_reuse import ready_store
        self.sandbox_stubs()
        with ready_store(self.root, self.env):
            # Native readiness is stubbed; store-origin checks use real local HTTP.
            (self.bin / 'curl').unlink()
            real_curl = shutil.which('curl')
            # The store fixture selects an isolated HTTP port in the copied scripts.
            store_script = (self.root / 'scripts/nopcommerce-demo.sh').read_text()
            import re
            store_port = re.search(r'origin="http://127.0.0.1:(\d+)"', store_script).group(1)
            self.command('curl', f'case "$*" in *127.0.0.1:{store_port}*) exec "{real_curl}" "$@";; esac\n'
                         'printf \'{"profile":"Real"}\\n\'')
            launcher = self.root / 'scripts/dev-real.sh'
            launcher.write_text(launcher.read_text().replace('5088', store_port))
            for action in ('start', 'restart'):
                result = self.run_script(action, '--full')
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertIn('Reusing healthy nopCommerce', result.stdout)
            import json
            calls = [json.loads(line) for line in (self.root / 'store-trace').read_text().splitlines()]
            self.assertFalse(any(args[0] in ('compose', 'restart', 'cp') for args in calls))
            self.assertNotIn('sandbox:stop', (self.root / 'trace').read_text())

    def test_api_only_status_does_not_require_sandbox_or_docker(self):
        self.sandbox_stubs()
        self.command('dotnet','exec sleep 300')
        self.assertEqual(self.run_script('start','--api-only').returncode,0)
        self.assertEqual(self.run_script('status').returncode,0)
        self.assertFalse((self.root/'trace').exists())
        self.assertIn('not selected',self.run_script('status').stdout)

    def test_api_only_disables_sandbox_despite_inherited_and_env_file_settings(self):
        self.sandbox_stubs()
        self.env['OpenSandbox__Enabled']='true'
        (self.root/'.env').write_text('OpenSandbox__Enabled=true\nOpenSandbox__ApiKey=fixture-private-key\n')
        self.command('dotnet','echo "$OpenSandbox__Enabled" >> "'+str(self.root/'enabled')+'"\nexec sleep 300')
        for count,action in enumerate(['start','restart'],start=1):
            result=self.run_script(action,'--api-only')
            self.assertEqual(result.returncode,0,result.stderr)
            for _ in range(100):
                if (self.root/'enabled').exists() and len((self.root/'enabled').read_text().splitlines())==count: break
                time.sleep(.01)
            self.assertEqual((self.root/'enabled').read_text().splitlines(),['false']*count)
        self.assertEqual((self.root/'enabled').read_text().splitlines(),['false','false'])
        self.assertFalse((self.root/'trace').exists())

    def test_explicit_sandbox_restart_reuses_server(self):
        self.sandbox_stubs()
        self.assertEqual(self.run_script('start','--api-only','--with-sandbox').returncode,0)
        result=self.run_script('restart','--api-only','--with-sandbox')
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertNotIn('sandbox:stop',(self.root/'trace').read_text())

    def test_explicit_recreation_warns_before_cleanup_and_restarts_native_processes(self):
        self.sandbox_stubs()
        self.assertEqual(self.run_script('start').returncode,0)
        old=self.pids()
        result=self.run_script('restart','--recreate-sandbox')
        self.assertEqual(result.returncode,0,result.stderr)
        self.assertIn('container-local packages, processes and unsynchronized files',result.stderr)
        self.assertIn('Canonical files are preserved',result.stderr)
        self.assertTrue(all(not self.alive(pid) for pid in old))
        self.assertTrue(all(self.alive(pid) for pid in self.pids()))
        self.assertEqual((self.root/'trace').read_text().splitlines(),
            ['sandbox:up','sandbox:status','sandbox:stop','sandbox:up','sandbox:status'])

    def test_recreation_pending_cleanup_does_not_start_replacement_stack(self):
        self.sandbox_stubs()
        self.assertEqual(self.run_script('start','--full').returncode,0)
        old=self.pids();self.env['FIXTURE_CLEANUP_PENDING']='1'
        result=self.run_script('restart','--full','--recreate-sandbox')
        self.assertNotEqual(result.returncode,0)
        self.assertIn('cleanup remains pending',result.stderr)
        self.assertTrue(all(not self.alive(pid) for pid in old))
        trace=(self.root/'trace').read_text().splitlines()
        self.assertEqual(trace.count('sandbox:up'),1)
        self.assertNotIn('store:stop',trace)

    def test_recreation_requires_restart_and_selected_sandbox_before_mutation(self):
        self.sandbox_stubs()
        for action,flags in [('start',('--recreate-sandbox',)),
            ('restart',('--api-only','--recreate-sandbox')),
            ('restart',('--recreate-sandbox','--api-only'))]:
            result=self.run_script(action,*flags)
            self.assertEqual(result.returncode,2,result.stderr)
            self.assertFalse((self.root/'trace').exists())

    def test_selected_store_down_fails_status_without_stopping_services(self):
        self.sandbox_stubs()
        self.assertEqual(self.run_script('start','--with-store').returncode,0)
        self.command('curl', 'case "$*" in *5088*) exit 7;; esac\nprintf \'{"profile":"Real"}\\n\'')
        result=self.run_script('status');self.assertNotEqual(result.returncode,0)
        self.assertIn('selected demo is down',result.stdout)
        self.assertTrue(all(self.alive(pid) for pid in self.pids()))

    def test_synthetic_health_is_rejected_and_native_processes_are_cleaned_up(self):
        self.command('curl', "printf '{\"profile\":\"Synthetic\"}\\n'")
        result=self.run_script('start','--api-only')
        self.assertNotEqual(result.returncode,0);self.assertIn('did not confirm the Real profile',result.stderr)
        self.assertFalse((self.root/'local/dev/api.pid').exists());self.assertFalse((self.root/'local/dev/web.pid').exists())

    def test_incompatible_store_api_only_flags_fail_before_mutations_in_either_order(self):
        self.sandbox_stubs()
        for action in ['start','restart']:
            for flags in [('--with-store','--api-only'),('--api-only','--with-store')]:
                result=self.run_script(action,*flags)
                self.assertEqual(result.returncode,2,result.stderr);self.assertFalse((self.root/'trace').exists())

    def test_incompatible_full_api_only_flags_fail_before_starting_components(self):
        self.sandbox_stubs();result=self.run_script('start','--full','--api-only')
        self.assertEqual(result.returncode,2);self.assertFalse((self.root/'trace').exists())
        result=self.run_script('restart','--full','--api-only');self.assertEqual(result.returncode,2);self.assertFalse((self.root/'trace').exists())


if __name__ == "__main__":
    unittest.main()
