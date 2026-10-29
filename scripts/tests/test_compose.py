"""Exercise preflight-to-Compose handoff without Docker, credentials or services."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


class ComposeLauncherTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        base = Path(self.temporary.name).resolve()
        self.root = base / 'project'
        self.caller = base / 'outside'
        self.bin = base / 'bin'
        for directory in (self.root / 'scripts', self.caller, self.bin):
            directory.mkdir(parents=True)
        for name in ('compose.py', 'dev-env.py'):
            shutil.copyfile(Path(__file__).resolve().parents[1] / name, self.root / 'scripts' / name)
        self.capture = base / 'handoff.json'
        docker = self.bin / 'docker'
        docker.write_text(f'#!{sys.executable}\n' + """
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
files = []
for index, arg in enumerate(args):
    if arg == '--env-file': files.append(Path(args[index + 1]))
    elif arg.startswith('--env-file='): files.append(Path(arg.split('=', 1)[1]))
if not files: files = [Path.cwd() / '.env']
Path(os.environ['COMPOSE_TEST_CAPTURE']).write_text(json.dumps({
    'cwd': str(Path.cwd()), 'args': args,
    'files': [str(file.resolve()) for file in files],
    'contents': [file.read_text() for file in files],
}))
""")
        docker.chmod(0o755)
        self.env = {'PATH': str(self.bin) + os.pathsep + os.defpath,
                    'COMPOSE_TEST_CAPTURE': str(self.capture)}

    def launch(self, *args):
        return subprocess.run([sys.executable, str(self.root / 'scripts/compose.py'),
                               *args, '-f', 'docker-compose.yml', 'config'],
                              cwd=self.caller, env=self.env, capture_output=True, text=True, timeout=10)

    def test_outside_relative_env_file_is_the_same_file_validated_and_consumed(self):
        for equals in (False, True):
            with self.subTest(equals=equals):
                relative = Path('input files') / 'selected.env'
                for directory in (self.caller, self.root):
                    (directory / relative.parent).mkdir(exist_ok=True)
                selected = self.caller / relative
                selected.write_text('AGENTCORE_DRIVE_ENABLED=false\n')
                (self.root / relative).write_text('GoogleEmailOAuth__ClientSecret=unvalidated-sentinel\n')
                args = ['--env-file=' + str(relative)] if equals else ['--env-file', str(relative)]
                result = self.launch(*args)
                self.assertEqual(result.returncode, 0, result.stderr)
                handoff = json.loads(self.capture.read_text())
                self.assertEqual(handoff['cwd'], str(self.root))
                self.assertEqual(handoff['files'], [str(selected)])
                self.assertEqual(handoff['contents'], [selected.read_text()])
                self.assertEqual(handoff['args'][-3:], ['-f', 'docker-compose.yml', 'config'])

    def test_mixed_env_file_forms_keep_override_order_for_validation_and_handoff(self):
        (self.caller / 'first.env').write_text('AGENTCORE_COMPOSE_PORT=invalid\n')
        (self.caller / 'second.env').write_text('AGENTCORE_COMPOSE_PORT=5080\n')
        for args in (['--env-file=first.env', '--env-file', 'second.env'],
                     ['--env-file', 'first.env', '--env-file=second.env']):
            with self.subTest(args=args):
                result = self.launch(*args)
                self.assertEqual(result.returncode, 0, result.stderr)
                handoff = json.loads(self.capture.read_text())
                self.assertEqual(handoff['files'], [str(self.caller / name) for name in ('first.env', 'second.env')])
                self.assertEqual(handoff['contents'], ['AGENTCORE_COMPOSE_PORT=invalid\n', 'AGENTCORE_COMPOSE_PORT=5080\n'])

    def test_retired_caller_input_refuses_before_docker_and_redacts_values(self):
        (self.caller / 'selected.env').write_text('GoogleEmailOAuth__ClientSecret=private-sentinel\n')
        (self.root / 'selected.env').write_text('AGENTCORE_DRIVE_ENABLED=true\n')
        result = self.launch('--env-file', 'selected.env')
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Retired configuration key', result.stderr)
        self.assertNotIn('private-sentinel', result.stdout + result.stderr)
        self.assertFalse(self.capture.exists())

    def test_external_decision_and_sidecar_refuse_before_docker(self):
        for flags in (['-f', 'docker-compose.decision.yml', '--file=docker-compose.decision.external.yml'],
                      ['--file', 'docker-compose.decision.native.yml', '-f', 'docker-compose.decision.external.yml']):
            with self.subTest(flags=flags):
                result = self.launch(*flags)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn('external Decision bridge', result.stderr)
                self.assertFalse(self.capture.exists())

    def test_compose_file_environment_cannot_mix_external_and_sidecar(self):
        env = {**self.env, 'COMPOSE_FILE': 'docker-compose.decision.yml' + os.pathsep + 'docker-compose.decision.external.yml'}
        result = subprocess.run([sys.executable, str(self.root / 'scripts/compose.py'), 'config'],
                                cwd=self.caller, env=env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('external Decision bridge', result.stderr)
        self.assertFalse(self.capture.exists())

    def test_default_env_file_stays_at_project_root(self):
        (self.root / '.env').write_text('AGENTCORE_DRIVE_ENABLED=false\n')
        (self.caller / '.env').write_text('GoogleEmailOAuth__ClientId=retired\n')
        result = self.launch()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(json.loads(self.capture.read_text())['files'], [str(self.root / '.env')])

    def test_missing_env_file_argument_refuses_before_docker(self):
        result = subprocess.run([sys.executable, str(self.root / 'scripts/compose.py'), '--env-file'],
                                cwd=self.caller, env=self.env, capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Missing path for --env-file.', result.stderr)
        self.assertFalse(self.capture.exists())
