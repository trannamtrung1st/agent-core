#!/usr/bin/env bash
# Every invocation owns a fresh project/volume and removes only that fixture.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
exec python3 "$root/scripts/verify-compose-identity.py"
