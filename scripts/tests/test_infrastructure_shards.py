"""Coverage and CLI receipts for the complete Infrastructure CI partition."""
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / 'infrastructure-shards.py'
spec = importlib.util.spec_from_file_location('infrastructure_shards', SCRIPT)
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
SOURCE = 'a' * 40
CATALOG = '''The following Tests are available:
    AgentCore.Infrastructure.Tests.A.One(sqlite: True)
    AgentCore.Infrastructure.Tests.A.One(sqlite: False)
    AgentCore.Infrastructure.Tests.B.Two
    AgentCore.Infrastructure.Tests.C.Three
'''


class InfrastructureShardTests(unittest.TestCase):
    def setUp(self):
        self.source = patch.dict(os.environ, {'GITHUB_SHA': SOURCE})
        self.source.start()
        self.addCleanup(self.source.stop)

    def test_parameter_sets_are_accounted_for_and_partitioned_exactly_once(self):
        methods = helper.parse_discovery(CATALOG)
        plan = helper.make_plan(methods, 2, SOURCE)
        self.assertEqual(sum(methods.values()), 4)
        self.assertEqual(sorted(m for shard in plan['shards'] for m in shard), sorted(methods))
        self.assertEqual([sum(methods[m] for m in shard) for shard in plan['shards']], [2, 2])
        self.assertEqual(plan, helper.make_plan(dict(reversed(list(methods.items()))), 2, SOURCE))

    def test_unknown_or_empty_discovery_cannot_silently_drop_a_test(self):
        for output in ('', 'The following Tests are available:\n', CATALOG + '    Custom test display name\n'):
            with self.subTest(output=output), self.assertRaises(ValueError):
                helper.parse_discovery(output)

    def test_duplicate_missing_or_foreign_source_assignments_are_rejected(self):
        for kind in ('duplicate', 'missing', 'source', 'digest'):
            with self.subTest(kind=kind):
                plan = helper.make_plan(helper.parse_discovery(CATALOG), 2, SOURCE)
                if kind == 'duplicate': plan['shards'][1].append(plan['shards'][0][0])
                if kind == 'missing': plan['shards'][0].pop()
                if kind == 'source': plan['revision'] = 'b' * 40
                if kind != 'digest': plan['sha256'] = helper.digest(plan)
                else: plan['sha256'] = 'invalid'
                with self.assertRaises(ValueError): helper.validate_plan(plan)

    def test_empty_or_out_of_range_shards_are_rejected(self):
        methods = helper.parse_discovery(CATALOG)
        for count in (0, 4):
            with self.assertRaises(ValueError): helper.make_plan(methods, count, SOURCE)

    def cli_fixture(self, root):
        executable = root / 'bin/dotnet'; executable.parent.mkdir()
        executable.write_text(f'#!{sys.executable}\n' + f'''
import json, os, pathlib, sys
args = sys.argv[1:]
if '--list-tests' in args:
    print({CATALOG!r})
    sys.exit(0)
pathlib.Path(os.environ['SHARD_CALL']).write_text(json.dumps(args))
code = int(os.environ.get('SHARD_EXIT', '0'))
if code: sys.exit(code)
counts = {{'AgentCore.Infrastructure.Tests.A.One': 2, 'AgentCore.Infrastructure.Tests.B.Two': 1, 'AgentCore.Infrastructure.Tests.C.Three': 1}}
selected = args[args.index('--filter') + 1].split('|')
count = sum(counts[item.removeprefix('FullyQualifiedName=')] for item in selected) + int(os.environ.get('SHARD_COUNT_DELTA', '0'))
results = pathlib.Path(args[args.index('--results-directory') + 1])
(results / 'results.trx').write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><ResultSummary><Counters total="' + str(count) + '" /></ResultSummary></TestRun>')
''')
        executable.chmod(0o755)
        return {**os.environ, 'PATH': str(executable.parent) + os.pathsep + os.environ['PATH'], 'SHARD_CALL': str(root / 'call.json')}

    def cli(self, env, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *map(str, args)], env=env, capture_output=True, text=True)

    def test_cli_runs_exact_filters_and_requires_complete_trx_receipts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory); env = self.cli_fixture(root); plan = root / 'plan.json'; results = root / 'results'
            prepared = self.cli(env, 'plan', '--shards', 2, '--output', plan)
            self.assertEqual(prepared.returncode, 0, prepared.stderr)
            for number in (1, 2):
                run = self.cli(env, 'run', '--plan', plan, '--shard', number, '--results-root', results)
                self.assertEqual(run.returncode, 0, run.stderr)
            receipts = [json.loads(p.read_text()) for p in results.glob('*/receipt.json')]
            self.assertEqual(sum(r['actual_cases'] for r in receipts), 4)
            self.assertEqual({r['source_sha'] for r in receipts}, {SOURCE})
            args = json.loads((root / 'call.json').read_text())
            self.assertIn('--no-build', args); self.assertIn('--no-restore', args)
            self.assertEqual(args[args.index('--blame-hang-dump-type') + 1], 'none')
            for delta in ('-1', '1'):
                mismatch = self.cli({**env, 'SHARD_COUNT_DELTA': delta}, 'run', '--plan', plan, '--shard', 1, '--results-root', results)
                self.assertEqual(mismatch.returncode, 2)
                self.assertIn('TRX accounted for', mismatch.stderr)
            failed = self.cli({**env, 'SHARD_EXIT': '7'}, 'run', '--plan', plan, '--shard', 1, '--results-root', results)
            self.assertEqual(failed.returncode, 7)
            invalid = self.cli(env, 'run', '--plan', plan, '--shard', 3, '--results-root', results)
            self.assertEqual(invalid.returncode, 2)


if __name__ == '__main__':
    unittest.main()
