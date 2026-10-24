#!/usr/bin/env python3
"""Load native development dotenv as data, including .NET keys with hyphenated aliases."""
import argparse
import json
import os
from pathlib import Path
import re
import shlex


def read_env(path):
    values = {}
    if not path.exists():
        return values
    for number, raw in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith('#'):
            continue
        if line.startswith('export '):
            line = line[7:].lstrip()
        key, separator, value = line.partition('=')
        key = key.strip()
        if not separator or not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_.-]*', key):
            raise ValueError(f'Invalid dotenv assignment at line {number}; use NAME=value.')
        value = value.strip()
        if value.startswith(('"', "'")):
            try:
                tokens = shlex.split(value, comments=True, posix=True)
            except ValueError:
                raise ValueError(f'Invalid dotenv quoting at line {number}.') from None
            if len(tokens) != 1:
                raise ValueError(f'Invalid dotenv value at line {number}.')
            value = tokens[0]
        else:
            value = re.split(r'\s+#', value, maxsplit=1)[0].rstrip()
        if '\0' in value:
            raise ValueError(f'Invalid dotenv value at line {number}.')
        # Deliberately no shell commands, variable substitution or interpolation.
        values[key] = value
    return values


def environment(root, path, inherited):
    profile = root / 'src/AgentCore.Api/Properties/launchSettings.json'
    explicit = {**inherited, **read_env(path)}
    defaults = {'ASPNETCORE_ENVIRONMENT': 'Development', 'AgentCore__Profile': 'Real'}
    if profile.exists():
        selected = 'http' if explicit.get('AgentCore__Profile', 'Real').lower() == 'synthetic' else 'http-openrouter'
        defaults.update(json.loads(profile.read_text())['profiles'][selected]['environmentVariables'])
    # The optional aggregate data root derives the connection unless explicitly
    # overridden; the old launch-profile connection must not shadow that choice.
    defaults.pop('Persistence__ConnectionString', None)
    if defaults.get('Browser__ProfileRoot'):
        defaults['Browser__ProfileRoot'] = str((root / 'src/AgentCore.Api' / defaults['Browser__ProfileRoot']).resolve())
    for key in ('AGENTCORE_DEV_STATE_DIR', 'AGENTCORE_DEV_API_DATA_DIR', 'AgentCore__AgentDirectory',
                'Persistence__WorkspaceRoot', 'Persistence__AttachmentRoot', 'Persistence__ArtifactRoot',
                'Persistence__DefinitionResourceRoot', 'Persistence__CredentialProtectionKeyRoot',
                'Persistence__TemplateRoot', 'Browser__ProfileRoot'):
        if explicit.get(key) and not Path(explicit[key]).is_absolute():
            explicit[key] = str((root / explicit[key]).resolve())
    result = {**defaults, **explicit}
    # Shell exports cannot address hyphenated names. execve passes them unchanged.
    # Defaults are applied before .env; --no-launch-profile prevents a later override.
    result['AGENTCORE_DEV_ENV_LOADED'] = str(root)
    result['AGENTCORE_DEV_ENV_FILE'] = str(path)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, required=True)
    parser.add_argument('--env-file', type=Path, required=True)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ['--'] else args.command
    if not command:
        parser.error('Supply the child command after --.')
    try:
        env = environment(args.root.resolve(), args.env_file.resolve(), os.environ)
    except (ValueError, OSError, KeyError):
        # Never echo a dotenv line/value or a parser traceback containing secrets.
        parser.exit(2, 'Cannot load native .env configuration. Check assignment syntax and quoting; values were not printed.\n')
    os.execvpe(command[0], command, env)


if __name__ == '__main__':
    main()
