"""Receipt regression checks; these do not stand in for real SDK/server acceptance."""
import io
import json
import os
import unittest
from unittest.mock import patch

import importlib.util
from pathlib import Path

spec = importlib.util.spec_from_file_location("execd_fallback", Path(__file__).with_name("execd-fallback.py"))
fallback = importlib.util.module_from_spec(spec)
spec.loader.exec_module(fallback)


class CommandReceiptTests(unittest.TestCase):
    def responses(self, events, status):
        return [io.BytesIO(events), io.BytesIO(json.dumps(status).encode())]

    def test_nonzero_exit_receipt_retains_separated_output(self):
        events = b'{"type":"init","text":"abc123"}\n{"type":"stdout","text":"ok"}\n{"type":"stderr","text":"failed"}\n'
        with patch.object(fallback.urllib.request, "urlopen", side_effect=self.responses(events, {"running": False, "exit_code": 7})):
            result = fallback.run("ignored-test-fixture", expected_exit=7)
        self.assertEqual("ok", fallback.text(result, "stdout"))
        self.assertEqual("failed", fallback.text(result, "stderr"))

    def test_missing_receipt_is_failure(self):
        with patch.object(fallback.urllib.request, "urlopen", return_value=io.BytesIO(b"")):
            with self.assertRaises(RuntimeError):
                fallback.run("ignored-test-fixture")

    def test_running_or_failed_or_unknown_command_cannot_pass(self):
        for status in ({"running": True, "exit_code": 0}, {"running": False, "exit_code": 1}, {"running": False}):
            with self.subTest(status=status), patch.object(fallback.urllib.request, "urlopen", side_effect=self.responses(b'{"type":"init","text":"abc123"}\n', status)):
                with self.assertRaises(RuntimeError):
                    fallback.run("ignored-test-fixture")

    def test_excess_response_is_failure(self):
        with patch.object(fallback.urllib.request, "urlopen", return_value=io.BytesIO(b"x" * 65537)):
            with self.assertRaises(RuntimeError):
                fallback.run("ignored-test-fixture")

    def test_failed_isolation_command_cannot_pass(self):
        # The former script treated missing stdout as "not reachable". Now a
        # failed command is rejected before interpreting its empty stdout.
        events = b'{"type":"init","text":"abc123"}\n'
        with patch.object(fallback.urllib.request, "urlopen", side_effect=self.responses(events, {"running": False, "exit_code": 127})):
            with self.assertRaises(RuntimeError):
                fallback.run("ignored-test-fixture")

    def test_no_opt_in_never_contacts_loopback(self):
        with patch.dict(os.environ, {}, clear=True), patch.object(fallback.os, "readlink", return_value="/usr/bin/python"), patch.object(fallback.urllib.request, "urlopen") as request, patch("sys.stderr", new_callable=io.StringIO):
            self.assertEqual(2, fallback.main())
            request.assert_not_called()


if __name__ == "__main__":
    unittest.main()
