#!/usr/bin/env python3
"""Load native development dotenv as data, including .NET keys with hyphenated aliases."""
import argparse
import json
import os
from pathlib import Path
import re
import shlex
from urllib.parse import urlsplit


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
        if any(normalize(key) == normalize(previous) for previous in values):
            raise ValueError(f'Duplicate configuration key {key}; keep one assignment. Values are redacted.')
        values[key] = value
    return values


TRANSLATIONS = {
    'GOOGLE_OAUTH_CLIENT_ID': 'GoogleOAuth__ClientId',
    'GOOGLE_OAUTH_CLIENT_SECRET': 'GoogleOAuth__ClientSecret',
    'GOOGLE_OAUTH_PUBLIC_APP_URL': 'GoogleOAuth__PublicAppUrl',
    'AGENTCORE_DRIVE_ENABLED': 'GoogleDrive__Enabled',
}
RETIRED = {
    'googleemailoauth': 'GoogleOAuth__*; reconnect unproven grants',
    'googledriveoauth': 'GoogleOAuth__*; reconnect unproven grants',
    'google_email_oauth_': 'GOOGLE_OAUTH_*; reconnect unproven grants',
    'google_drive_oauth_': 'GOOGLE_OAUTH_*; reconnect unproven grants',
    'providers:speechrecognizers': 'Providers__Speech__Recognition__*',
    'providers:speechsynthesizers': 'Providers__Speech__Synthesis__*',
    'filepreview:limits:csvbytes': 'FilePreview__Limits__DelimitedBytes',
}


def normalize(key):
    return key.replace('__', ':').lower()


def validate(values, compose=False):
    """Names only in diagnostics. Called before launch or Compose interpolation."""
    seen = {}
    for key, value in values.items():
        normalized = normalize(key)
        if normalized in seen:
            raise ValueError(f'Duplicate configuration keys {seen[normalized]} and {key}. Values are redacted.')
        seen[normalized] = key
        for prefix, replacement in RETIRED.items():
            if normalized == prefix or normalized.startswith(prefix if prefix.endswith('_') else prefix + ':'):
                raise ValueError(f'Retired configuration key {key}; use {replacement}. Values are redacted.')
        if value and (normalized.endswith(':enabled') or normalized in ('googleDrive:enabled'.lower(),)
                      or key == 'AGENTCORE_DRIVE_ENABLED') and value.lower() not in ('true', 'false'):
            raise ValueError(f'{key} must be true or false. Values are redacted.')
        if value and key in ('AGENTCORE_DEV_API_PORT', 'AGENTCORE_DEV_WEB_PORT', 'AGENTCORE_SANDBOX_PORT', 'AGENTCORE_COMPOSE_PORT'):
            if not value.isascii() or not value.isdecimal() or not 1 <= int(value) <= 65535:
                raise ValueError(f'{key} must be a port in 1..65535. Values are redacted.')
        if value and (normalized.startswith('filepreview:') or normalized.endswith(':timeoutseconds')):
            if not value.isascii() or not value.isdecimal() or int(value) < 1:
                raise ValueError(f'{key} must be a positive integer. Values are redacted.')
        if value and key == 'AGENTCORE_TLS_CERTIFICATE_PATH' and not Path(value).is_file():
            raise ValueError(f'{key} must identify a readable certificate file. Values are redacted.')
    for external, canonical in TRANSLATIONS.items():
        other = seen.get(normalize(canonical))
        if values.get(external) and other and values[other]:
            raise ValueError(f'Ambiguous configuration keys {external} and {other}; set one input. Values are redacted.')
        if compose and other and values[other]:
            raise ValueError(f'{other} is a native input; Compose uses {external}. Values are redacted.')
    origin = values.get('GOOGLE_OAUTH_PUBLIC_APP_URL') or values.get(seen.get('googleoauth:publicappurl', ''))
    if origin:
        try:
            url = urlsplit(origin)
            valid = url.scheme == 'https' or url.scheme == 'http' and url.hostname in ('localhost', '127.0.0.1', '::1')
            valid = valid and url.hostname and not url.username and not url.password and url.path in ('', '/') and not url.query and not url.fragment
            valid = valid and (url.port is None or 1 <= url.port <= 65535)
        except ValueError:
            valid = False
        if not valid:
            raise ValueError('GoogleOAuth PublicAppUrl must be an HTTPS or loopback HTTP origin. Values are redacted.')


def translate(values):
    result = dict(values)
    validate(result)
    for external, canonical in TRANSLATIONS.items():
        value = result.pop(external, None)
        if value:
            result[canonical] = value
    return result


def environment(root, path, inherited):
    profile = root / 'src/AgentCore.Api/Properties/launchSettings.json'
    explicit = translate({**inherited, **read_env(path)})
    defaults = {'ASPNETCORE_ENVIRONMENT': 'Development', 'AgentCore__Profile': 'Real'}
    if profile.exists():
        selected = 'http' if explicit.get('AgentCore__Profile', 'Real').lower() == 'synthetic' else 'http-real'
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
    except ValueError as error:
        parser.exit(2, "Cannot load native .env configuration. " + str(error) + "\n")
    except (OSError, KeyError):
        # Never echo a dotenv line/value or a parser traceback containing secrets.
        parser.exit(2, 'Cannot load native .env configuration. Check assignment syntax and quoting; values were not printed.\n')
    os.execvpe(command[0], command, env)


if __name__ == '__main__':
    main()
