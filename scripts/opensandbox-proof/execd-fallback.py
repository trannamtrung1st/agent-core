"""Run INSIDE the network-none, non-root disposable execd container only.

This probes a real execd binary, not the lifecycle service or SDK integration.
No target outside the container is contacted. A loopback isolation failure is
an intentional nonzero result, never an accepted security configuration.
"""
import json
import os
import sys
import urllib.request

BASE = "http://127.0.0.1:44772"
failures = []


def check(scenario, passed):
    print(json.dumps({"scenario": scenario, "passed": bool(passed)}), flush=True)
    if not passed:
        failures.append(scenario)


def read_json(response):
    content = response.read(65537)
    if len(content) > 65536:
        raise RuntimeError("Bounded proof response exceeded its limit")
    return json.loads(content)


def run(command, expected_exit=0):
    request = urllib.request.Request(
        BASE + "/command",
        data=json.dumps({"command": command, "cwd": "/working", "timeout": 5000}).encode(),
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(request, timeout=10) as response:
        content = response.read(65537)
    if len(content) > 65536:
        raise RuntimeError("Bounded proof command produced excess output")
    events = [json.loads(line) for line in content.splitlines() if line.strip()]
    execution_id = next((event.get("text") for event in events if event.get("type") == "init"), None)
    if not isinstance(execution_id, str) or not execution_id.isalnum():
        raise RuntimeError("Missing or invalid execution receipt")
    with urllib.request.urlopen(BASE + "/command/status/" + execution_id, timeout=5) as response:
        status = read_json(response)
    if status.get("running") is not False or status.get("exit_code") != expected_exit:
        raise RuntimeError("Command completion was not confirmed with the expected exit code")
    return events


def text(events, stream):
    return "".join(event.get("text", "") for event in events if event["type"] == stream)


def main():
    try:
        inside_execd = os.readlink("/proc/1/exe") == "/usr/local/bin/execd"
    except OSError:
        inside_execd = False
    if os.environ.get("AGENTCORE_OSB_FALLBACK") != "1" or os.getuid() != 1000 or not inside_execd:
        print("Run only through run-execd-fallback.py in its disposable execd container.", file=sys.stderr)
        return 2
    try:
        exercise()
    except Exception:
        check("fallback-exception-details-suppressed", False)
    print(json.dumps({"failures": failures, "stageAAccepted": False}), flush=True)
    return 1 if failures else 0


def exercise():
    check("fallback-non-root-client", os.getuid() == 1000)
    identity = run("id -u")
    check("fallback-non-root-command", text(identity, "stdout").strip() == "1000")
    events = run("printf stdout-marker; printf stderr-marker >&2; exit 7", expected_exit=7)
    check("fallback-separated-streams", text(events, "stdout") == "stdout-marker"
          and text(events, "stderr") == "stderr-marker")
    check("fallback-exit-code-seven", True)  # run() confirmed the durable command status.

    created = run("python -c \"from pathlib import Path; Path('/working/exact.bin').write_bytes(bytes(range(256)))\"")
    check("fallback-command-created-file", not any(e["type"] == "error" for e in created))
    retained = run("python -c \"from pathlib import Path; print(Path('/working/exact.bin').read_bytes() == bytes(range(256)))\"")
    check("fallback-second-command-retains-exact-bytes", text(retained, "stdout").strip() == "True")

    # This request is issued BY an execd command, not just by the test client.
    # Raw execd needs no lifecycle API key in this fallback. Reaching it fails
    # the required workload/control-plane separation, even with network=none.
    probe = run("python -c \"import socket; s=socket.socket(); s.settimeout(2); print(s.connect_ex(('127.0.0.1',44772))); s.close()\"")
    # Empty/malformed output or a failed command must never count as isolation.
    result = int(text(probe, "stdout").strip())
    check("fallback-workload-cannot-connect-to-execd-loopback", result > 0)


if __name__ == "__main__":
    sys.exit(main())
