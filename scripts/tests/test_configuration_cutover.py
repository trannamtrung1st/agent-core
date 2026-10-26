"""Configuration boundary behavior; no ports, provider calls or private inputs."""
from pathlib import Path
import runpy
import tempfile
import unittest

policy = runpy.run_path(str(Path(__file__).resolve().parents[1] / 'dev-env.py'))


class ConfigurationCutoverTests(unittest.TestCase):
    def test_duplicate_assignments_include_case_and_net_separators(self):
        for text in ('GoogleOAuth__ClientId=one\ngoogleoauth__clientid=two',
                     'FilePreview__Limits__DelimitedBytes=1\nFilePreview__Limits__DelimitedBytes=1'):
            with self.subTest(text=text), tempfile.TemporaryDirectory() as root:
                path = Path(root) / 'input.env'; path.write_text(text)
                with self.assertRaisesRegex(ValueError, 'Duplicate'):
                    policy['read_env'](path)

    def test_native_translation_removes_orchestration_inputs(self):
        result = policy['translate']({'GOOGLE_OAUTH_CLIENT_ID': 'id', 'GOOGLE_OAUTH_CLIENT_SECRET': 'private',
            'GOOGLE_OAUTH_PUBLIC_APP_URL': 'https://app.example.test', 'AGENTCORE_DRIVE_ENABLED': 'false'})
        self.assertEqual(result, {'GoogleOAuth__ClientId': 'id', 'GoogleOAuth__ClientSecret': 'private',
            'GoogleOAuth__PublicAppUrl': 'https://app.example.test', 'GoogleDrive__Enabled': 'false'})

    def test_dual_inputs_are_ambiguous_even_when_equal(self):
        for external, canonical in policy['TRANSLATIONS'].items():
            with self.subTest(external=external), self.assertRaisesRegex(ValueError, 'Ambiguous') as error:
                policy['translate']({external: 'true' if external == 'AGENTCORE_DRIVE_ENABLED' else 'private-sentinel', canonical: 'true' if external == 'AGENTCORE_DRIVE_ENABLED' else 'private-sentinel'})
            self.assertNotIn('private-sentinel', str(error.exception))

    def test_retired_names_fail_even_when_empty_and_do_not_echo_values(self):
        for key in ('GoogleEmailOAuth__ClientId', 'GoogleDriveOAuth__ClientSecret', 'GOOGLE_EMAIL_OAUTH_CLIENT_ID',
            'GOOGLE_DRIVE_OAUTH_CLIENT_SECRET', 'Providers__SpeechRecognizers__primary-stt__Adapter',
            'Providers__SpeechSynthesizers__primary-tts__Adapter', 'FilePreview__Limits__CsvBytes'):
            for value in ('', 'private-sentinel'):
                with self.subTest(key=key), self.assertRaisesRegex(ValueError, 'Retired') as error:
                    policy['validate']({key: value})
                self.assertNotIn('private-sentinel', str(error.exception))

    def test_malformed_values_fail_early(self):
        for key, value in [('GoogleDrive__Enabled', 'yes'), ('AGENTCORE_DEV_API_PORT', '65536'),
            ('FilePreview__Limits__DelimitedBytes', '-1'), ('GoogleOAuth__PublicAppUrl', 'https://user:private@app.test/path')]:
            with self.subTest(key=key), self.assertRaises(ValueError) as error:
                policy['validate']({key: value})
            self.assertNotIn('private', str(error.exception))

    def test_native_precedence_is_defaults_then_inherited_then_dotenv(self):
        with tempfile.TemporaryDirectory() as root:
            root = Path(root); env = root / '.env'; env.write_text('AgentCore__Profile=Synthetic\nAGENTCORE_DEV_API_PORT=5200\n')
            result = policy['environment'](root, env, {'AgentCore__Profile': 'Real', 'AGENTCORE_DEV_API_PORT': '5100'})
            self.assertEqual(result['AgentCore__Profile'], 'Synthetic'); self.assertEqual(result['AGENTCORE_DEV_API_PORT'], '5200')

    def test_compose_requires_boundary_inputs_for_translated_options(self):
        policy['validate']({'GOOGLE_OAUTH_CLIENT_SECRET': 'private'}, compose=True)
        with self.assertRaisesRegex(ValueError, 'Compose uses'):
            policy['validate']({'GoogleOAuth__ClientSecret': 'private'}, compose=True)
