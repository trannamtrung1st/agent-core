import contextlib
import io
import os
from pathlib import Path
import runpy
import sys
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2]
POLICY = runpy.run_path(str(ROOT / 'scripts/dev-env.py'))
DECISION = runpy.run_path(str(ROOT / 'scripts/decision-compose.py'))

class DecisionLauncherTests(unittest.TestCase):
    def test_unknown_names_and_unbounded_requests_refuse_without_values(self):
        for key, value in [('DecisionIntelligence__LegacyBackend', 'private-sentinel'), ('DecisionIntelligence__RequestTimeoutMs', '999999'), ('DecisionIntelligence__MaxConcurrentRequests', '0')]:
            with self.subTest(key=key), self.assertRaises(ValueError) as raised:
                POLICY['validate']({key: value})
            self.assertNotIn(value, str(raised.exception))

    def test_disabled_preflight_never_contacts_docker_or_downloads(self):
        stream = io.StringIO()
        with patch.dict(os.environ, {}, clear=True), patch.dict(DECISION['policy'], read_env=lambda _: {}), patch.object(sys, 'argv', ['decision-compose.py', 'preflight']), patch('subprocess.run') as run, contextlib.redirect_stdout(stream):
            self.assertEqual(0, DECISION['main']())
            run.assert_not_called()
        self.assertIn('disabled', stream.getvalue())

    def test_native_service_refuses_non_loopback_before_docker(self):
        with patch.dict(os.environ, {'DecisionIntelligence__Enabled': 'true', 'DecisionIntelligence__Endpoint': 'http://169.254.169.254:8009'}, clear=True), patch.dict(DECISION['policy'], read_env=lambda _: {}), patch.object(sys, 'argv', ['decision-compose.py', 'start']), patch('subprocess.run') as run:
            with self.assertRaisesRegex(ValueError, 'loopback'):
                DECISION['main']()
            run.assert_not_called()

    def test_mismatched_private_credentials_refuse_without_echoing(self):
        with patch.dict(os.environ, {'DecisionIntelligence__Enabled': 'true', 'DecisionIntelligence__ApiKey': 'private-host-sentinel', 'KEV_API_KEY': 'private-service-sentinel'}, clear=True), patch.dict(DECISION['policy'], read_env=lambda _: {}), patch.object(sys, 'argv', ['decision-compose.py', 'start']), patch('subprocess.run') as run:
            with self.assertRaises(ValueError) as raised: DECISION['main']()
            self.assertNotIn('sentinel', str(raised.exception)); run.assert_not_called()

if __name__ == '__main__': unittest.main()
