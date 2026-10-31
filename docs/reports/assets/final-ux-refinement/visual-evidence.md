# Portable navy UI visual evidence

The [visual evidence archive](visual-evidence.zip) preserves the original captures and records for source commit `d3e9272c090136ff8f0de6aec3849c9d59d5b18b`. It is committed alongside the [delivery ledger](../../final-ux-refinement-2026-10-31.md), so future reviews do not depend on the original `/tmp/agent-core-navy` directory.

The 38.6 MiB package contains all **548 original PNGs**, unchanged byte-for-byte, plus coverage, source epochs, verification results, contrast measurements and independent review records. An offline gallery selects **240 final captures** using the recorded correction precedence; **308 earlier, baseline and supplemental captures** remain separately accessible. Earlier captures may show superseded defects. Selection and preservation add no new visual, functional or production acceptance claim.

## Open and verify

From this directory, verify the [archive checksum](visual-evidence.zip.sha256), extract into a fresh directory, then verify every original capture and supporting record:

```sh
shasum -a 256 -c visual-evidence.zip.sha256
navy_review_dir="$(mktemp -d)"
python3 -m zipfile -e visual-evidence.zip "$navy_review_dir"
python3 "$navy_review_dir/verify.py"
```

Open `index.html` in the extracted directory. The gallery uses relative local paths only; no application server, provider credentials, package installation or network connection is required. Hash verification uses the Python 3.9+ standard library.

`manifest.json` maps each original absolute capture path to its archive-relative `path`, retaining SHA-256, viewport dimensions, source epoch and recorded reviewers. Absolute paths inside the unchanged historical records are provenance, not required file locations. The outer checksum anchors the manifest and the entire package. [Package metadata](portable-evidence.json) records its source identity, size and counts.

The original qualification limits remain: captures are visual evidence rather than proof of every role/state combination, live providers, physical devices, native services, customer deployment or full accessibility conformance. This preservation follow-up changes documentation and artifacts only; it does not reopen UI refinement or start Self-Hosting.
