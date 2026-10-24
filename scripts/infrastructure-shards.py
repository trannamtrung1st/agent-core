#!/usr/bin/env python3
"""Partition the complete Infrastructure discovery once; verify every shard's TRX count."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
PROJECT = 'tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj'
METHOD = re.compile(r'AgentCore\.Infrastructure\.Tests\.[A-Za-z_]\w*(?:[.+][A-Za-z_]\w*)+\Z')


def parse_discovery(output):
    marker = 'The following Tests are available:'
    if marker not in output:
        raise ValueError('Infrastructure discovery did not produce a test catalog.')
    methods = Counter()
    for line in output.split(marker, 1)[1].splitlines():
        if not line.strip():
            continue
        method = line.strip().split('(', 1)[0]
        if not line.startswith('    ') or not METHOD.fullmatch(method):
            raise ValueError(f'Unrecognized discovered test: {line[:200]}')
        methods[method] += 1
    if not methods:
        raise ValueError('Infrastructure discovery was empty.')
    return dict(methods)


def digest(plan):
    payload = {key: value for key, value in plan.items() if key != 'sha256'}
    return hashlib.sha256(json.dumps(payload, sort_keys=True, separators=(',', ':')).encode()).hexdigest()


def make_plan(methods, count, revision=None):
    if count < 1 or count > len(methods):
        raise ValueError('Shard count must be between one and the discovered method count.')
    shards, loads = [[] for _ in range(count)], [0] * count
    # Keep every parameter set together; distribute larger methods first.
    for method in sorted(methods, key=lambda name: (-methods[name], name)):
        index = min(range(count), key=lambda i: (loads[i], i))
        shards[index].append(method)
        loads[index] += methods[method]
    plan = {'version': 1, 'revision': revision, 'method_cases': methods,
            'shards': [sorted(shard) for shard in shards]}
    plan['sha256'] = digest(plan)
    validate_plan(plan)
    return plan


def validate_plan(plan):
    if not isinstance(plan, dict) or plan.get('version') != 1 or plan.get('sha256') != digest(plan):
        raise ValueError('Infrastructure plan version or digest is invalid.')
    revision = os.environ.get('GITHUB_SHA')
    if revision and plan.get('revision') != revision:
        raise ValueError('Infrastructure plan belongs to a different source commit.')
    methods, shards = plan.get('method_cases'), plan.get('shards')
    if not isinstance(methods, dict) or not methods or any(
        not isinstance(name, str) or not METHOD.fullmatch(name) or type(count) is not int or count < 1
        for name, count in methods.items()
    ):
        raise ValueError('Infrastructure plan has invalid methods or case counts.')
    if not isinstance(shards, list) or not shards or any(not isinstance(shard, list) or not shard for shard in shards):
        raise ValueError('Infrastructure plan has an empty or invalid shard.')
    assigned = [method for shard in shards for method in shard]
    if any(not isinstance(method, str) for method in assigned) or Counter(assigned) != Counter({method: 1 for method in methods}):
        raise ValueError('Infrastructure plan must cover every discovered method exactly once.')


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, sort_keys=True, indent=2) + '\n')


def execute_shard(plan, number, results_root):
    validate_plan(plan)
    if not 1 <= number <= len(plan['shards']):
        raise ValueError('Shard number is outside the plan.')
    selected = plan['shards'][number - 1]
    expected = sum(plan['method_cases'][method] for method in selected)
    results_root.mkdir(parents=True, exist_ok=True)
    results = Path(tempfile.mkdtemp(prefix=f'shard-{number:02d}-', dir=results_root)).resolve()
    receipt = {'source_sha': plan['revision'], 'plan_sha256': plan['sha256'], 'shard': number,
               'shard_count': len(plan['shards']), 'expected_cases': expected, 'actual_cases': None,
               'selected_methods': selected}
    write_json(results / 'receipt.json', receipt)
    print(f"Infrastructure shard {number}/{len(plan['shards'])}: {len(selected)} methods, {expected} cases; plan {plan['sha256']}", flush=True)
    result = subprocess.run(['dotnet', 'test', PROJECT, '--no-build', '--no-restore', '--nologo',
        '--filter', '|'.join('FullyQualifiedName=' + method for method in selected),
        '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'none',
        '--results-directory', str(results), '--logger', 'trx;LogFileName=results.trx'], cwd=ROOT)
    receipt['exit_code'] = result.returncode
    if result.returncode == 0:
        counters = ET.parse(results / 'results.trx').getroot().find('.//{*}Counters')
        if counters is None:
            raise ValueError('Infrastructure TRX has no result counters.')
        receipt['actual_cases'] = int(counters.attrib['total'])
    write_json(results / 'receipt.json', receipt)
    if result.returncode:
        return result.returncode
    if receipt['actual_cases'] != expected:
        raise ValueError(f"Infrastructure shard expected {expected} cases but TRX accounted for {receipt['actual_cases']}.")
    print(f'Verified {expected} cases accounted for in TRX (including explicit skips).', flush=True)
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    modes = parser.add_subparsers(dest='mode', required=True)
    planning = modes.add_parser('plan')
    planning.add_argument('--shards', type=int, required=True)
    planning.add_argument('--output', type=Path, required=True)
    running = modes.add_parser('run')
    running.add_argument('--plan', type=Path, required=True)
    running.add_argument('--shard', type=int, required=True)
    running.add_argument('--results-root', type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        if args.mode == 'plan':
            discovered = subprocess.run(['dotnet', 'test', PROJECT, '--no-build', '--no-restore', '--nologo', '--list-tests'],
                cwd=ROOT, env={**os.environ, 'DOTNET_CLI_UI_LANGUAGE': 'en'}, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            if discovered.returncode:
                print(discovered.stdout[-3000:], file=sys.stderr)
                return discovered.returncode
            plan = make_plan(parse_discovery(discovered.stdout), args.shards, os.environ.get('GITHUB_SHA'))
            write_json(args.output, plan)
            print(f"Planned {sum(plan['method_cases'].values())} cases across {len(plan['shards'])} shards; digest {plan['sha256']}")
            return 0
        return execute_shard(json.loads(args.plan.read_text()), args.shard, args.results_root)
    except (ValueError, KeyError, TypeError, OSError, ET.ParseError) as error:
        print(f'Infrastructure shards: {error}', file=sys.stderr)
        return 2


if __name__ == '__main__':
    sys.exit(main())
