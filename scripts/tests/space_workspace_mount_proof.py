#!/usr/bin/env python3
"""Opt-in Linux host-bind proof using a cached immutable image; no OpenSandbox server changes.

This verifies Docker filesystem behavior only. It does not attest Core's SDK/gateway contract.
"""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import uuid


def docker(*args, check=True):
    result = subprocess.run(["docker", *args], capture_output=True, text=True, timeout=60)
    if check and result.returncode:
        raise RuntimeError("Docker proof operation failed; inspect the disposable fixture locally.")
    return result


def run(image):
    identity = json.loads(docker("image", "inspect", image, "--format", "{{json .Id}}").stdout)
    if not identity.startswith("sha256:"):
        raise RuntimeError("A cached immutable image is required.")
    uid, gid = os.getuid(), os.getgid()
    if uid == 0:
        raise RuntimeError("Run the proof as a non-root local operator.")
    with tempfile.TemporaryDirectory(prefix="agent-core-space-mount-") as temporary:
        root = Path(temporary)
        tenant, first, second, private, agent, session = [uuid.uuid4().hex for _ in range(6)]
        homes = root / f"tenant-{tenant}" / "spaces"
        active_a, active_b, own = [homes / f"space-{value}" / "home" for value in (first, second, private)]
        working = root / f"agent-{agent}" / "sessions" / f"session-{session}" / "working"
        for directory, marker in ((active_a, "A"), (active_b, "B"), (own, "PRIVATE"), (working, "WORKING")):
            directory.mkdir(parents=True)
            (directory / "owner.txt").write_text(marker)
        base = ["--pull=never", "--rm", "--network=none", "--user", f"{uid}:{gid}",
                "--cpus=1", "--memory=256m", "--pids-limit=64", "--cap-drop=ALL",
                "--security-opt=no-new-privileges", "--workdir=/working"]

        def generation(home, readonly, expected, previous_private):
            name = "agent-core-space-proof-" + uuid.uuid4().hex
            mounts = [(home, "/home", readonly), (own, "/private", False), (working, "/working", True)]
            args = base.copy()
            for source, target, ro in mounts:
                args += ["--mount", f"type=bind,source={source},target={target}" + (",readonly" if ro else "")]
            code = r'''
import os
from pathlib import Path
assert os.getuid() > 0
assert Path('/home/owner.txt').read_text() == EXPECTED
assert Path('/private/owner.txt').read_text() == 'PRIVATE'
assert Path('/working/owner.txt').read_text() == 'WORKING'
assert not Path('/var/run/docker.sock').exists()
def reject_write(path):
    try: Path(path).write_text('forbidden')
    except OSError: return
    raise AssertionError('read-only root accepted a write')
reject_write('/working/forbidden.txt')
if READONLY: reject_write('/home/forbidden.txt')
else:
    Path('/home/committed.txt').write_text('first-generation')
    Path('/home/local-link').symlink_to('committed.txt')
    assert Path('/home/local-link').read_text() == 'first-generation'
if PREVIOUS:
    assert Path('/private/persistent.txt').read_text() == 'private-first'
    assert not Path('/home/committed.txt').exists()
else: Path('/private/persistent.txt').write_text('private-first')
'''
            code = f"EXPECTED={expected!r};READONLY={readonly!r};PREVIOUS={previous_private!r}\n" + code
            try:
                docker("run", "--name", name, *args, identity, "python", "-I", "-c", code)
                # --rm completion is checked, not inferred from cancellation or a local process token.
                if docker("container", "inspect", name, check=False).returncode == 0:
                    raise RuntimeError("Disposable generation retirement is unconfirmed.")
            finally:
                # Exact proof-owned identity only; no existing server/workload is a cleanup target.
                docker("rm", "-f", name, check=False)

        generation(active_a, False, "A", False)
        assert (active_a / "committed.txt").read_text() == "first-generation"
        generation(active_b, True, "B", True)
        assert (active_a / "committed.txt").read_text() == "first-generation"
        assert not (active_b / "committed.txt").exists()
        assert not (active_b / "forbidden.txt").exists()
        assert not (working / "forbidden.txt").exists()
        assert (own / "persistent.txt").read_text() == "private-first"
    print(json.dumps({"passed": True, "generations": 2, "activeSpaceRoots": "distinct",
                      "homeModes": ["ReadWrite", "ReadOnly"], "privateRetained": True,
                      "workingReadOnly": True, "retirementConfirmed": True,
                      "sdkContractAttested": False}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True, help="An already cached Linux image with Python and a compatible UID/GID.")
    run(parser.parse_args().image)
