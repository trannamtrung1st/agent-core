"""Build/run the unprivileged local fallback; always remove its owned container."""
import json
from pathlib import Path
import subprocess
import sys
import time
import uuid


def docker(*args, **kwargs):
    return subprocess.run(["docker", *args], capture_output=True, timeout=kwargs.pop("timeout", 30), **kwargs)


def main():
    if sys.argv[1:] != ["--run"]:
        print("Usage: python3 scripts/opensandbox-proof/run-execd-fallback.py --run (unprivileged, network none, no host mounts)")
        return 0 if sys.argv[1:] == ["--help"] else 2
    here = Path(__file__).resolve().parent
    name = "ac-osb-fallback-" + uuid.uuid4().hex
    tag = "agent-core-osb-execd-fallback:" + uuid.uuid4().hex
    exit_code = 1
    try:
        built = docker("build", "--network", "none", "-f", str(here / "Dockerfile.execd-fallback"), "-t", tag, str(here), timeout=180)
        if built.returncode:
            raise RuntimeError("fallback-build")
        inspected = docker("image", "inspect", tag)
        if inspected.returncode:
            raise RuntimeError("fallback-image")
        image = json.loads(inspected.stdout)[0]["Id"]
        started = docker("run", "-d", "--name", name, "--label", "agent-core.opensandbox-proof=1",
                         "--network", "none", "--read-only", "--cap-drop", "ALL", "--security-opt", "no-new-privileges",
                         "--memory", "256m", "--cpus", "0.5", "--pids-limit", "64",
                         "--tmpfs", "/working:rw,nosuid,nodev,noexec,size=16m,uid=1000,gid=1000",
                         "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=16m", "-e", "AGENTCORE_OSB_FALLBACK=1", image)
        if started.returncode:
            raise RuntimeError("fallback-start")
        inspected = docker("inspect", name)
        if inspected.returncode:
            raise RuntimeError("fallback-inspect")
        actual = json.loads(inspected.stdout)[0]
        host = actual["HostConfig"]
        restricted = (actual["Config"]["User"] == "1000:1000" and host["NetworkMode"] == "none"
                      and host["Privileged"] is False and host["ReadonlyRootfs"] is True
                      and host["CapDrop"] == ["ALL"] and "no-new-privileges" in host["SecurityOpt"]
                      and host["Memory"] == 256 * 1024 * 1024 and host["NanoCpus"] == 500000000
                      and host["PidsLimit"] == 64 and not host.get("Binds") and not host.get("PortBindings")
                      and not actual["Mounts"] and set(host["Tmpfs"]) == {"/working", "/tmp"})
        print(json.dumps({"scenario": "fallback-container-security-config", "passed": restricted}), flush=True)
        if not restricted:
            raise RuntimeError("fallback-security-config")
        for attempt in range(20):
            ready = docker("exec", name, "python", "-c", "import socket; s=socket.create_connection(('127.0.0.1',44772),1); s.close()", timeout=5)
            if ready.returncode == 0:
                break
            time.sleep(0.25)
        else:
            raise RuntimeError("fallback-readiness")
        exercised = docker("exec", "-i", name, "python", input=(here / "execd-fallback.py").read_bytes(), timeout=60)
        if len(exercised.stdout) > 65536:
            raise RuntimeError("fallback-output-limit")
        # The child prints only fixed scenario identifiers/booleans, never raw SDK/HTTP errors.
        records = [json.loads(line) for line in exercised.stdout.splitlines()]
        for record in records:
            print(json.dumps(record), flush=True)
        if not records or records[-1].get("stageAAccepted") is not False:
            raise RuntimeError("fallback-missing-summary")
        exit_code = exercised.returncode
    except (Exception, KeyboardInterrupt):
        print(json.dumps({"scenario": "fallback-runner-error", "passed": False}), flush=True)
    finally:
        try:
            removed = docker("rm", "-f", name)
            # If creation never happened, no owned container should exist either.
            remaining = docker("ps", "-a", "--filter", "name=^/" + name + "$", "--format", "{{.Names}}")
            clean = remaining.returncode == 0 and not remaining.stdout.strip()
            print(json.dumps({"scenario": "fallback-cleanup", "passed": clean}), flush=True)
            if not clean:
                exit_code = 1
        except Exception:
            print(json.dumps({"scenario": "fallback-cleanup", "passed": False}), flush=True)
            exit_code = 1
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
