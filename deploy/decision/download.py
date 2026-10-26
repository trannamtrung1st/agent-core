"""Explicit operator-only download, including the checkpoint's exact frozen base."""
import os
if os.environ.get('HF_HUB_OFFLINE') != '0':
    raise SystemExit('Explicit download requires HF_HUB_OFFLINE=0 for this one command.')
from huggingface_hub import snapshot_download
from kev.checkpoint import Checkpoint
checkpoint = Checkpoint('jaredpalmer/kev-4b@6cfce5c2fa4b4bd64026336ab649c5ca78857d52')
if not checkpoint.meta.base_revision:
    raise SystemExit('Checkpoint omitted its immutable base revision; refusing download.')
snapshot_download(checkpoint.meta.base, revision=checkpoint.meta.base_revision,
                  allow_patterns=['*.json', '*.safetensors', '*.txt', '*.jinja'])
print('Pinned decision checkpoint and base cached. No inference was performed.')
