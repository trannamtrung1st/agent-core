#!/usr/bin/env python3
"""Opt-in actual OCI auth checks against disposable Synthetic Core and an operator-prepared registry fixture.

Start only owned fixtures, approve their host/repository policy, and supply two distinct immutable images.
AGENTCORE_OCI_FIXTURE_PASSWORD supplies the non-user fixture value; it is never printed or saved here.
After this check, publish a disposable Definition explicitly authorizing private-workbench/private-alternate
and set AGENTCORE_OCI_DEFINITION_VERSION for web/opensandbox-e2e/oci-catalog.spec.ts.
"""
import argparse
import json
import os
import re
import secrets
import urllib.error
import urllib.parse
import urllib.request


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--api-url", required=True)
    parser.add_argument("--registry", required=True)
    parser.add_argument("--username", required=True)
    parser.add_argument("--workbench-image", required=True)
    parser.add_argument("--alternate-image", required=True)
    parser.add_argument("--profile-prefix",default="private",help="Fresh disposable profile ID prefix; existing entries are never overwritten.")
    parser.add_argument("--disposable", action="store_true")
    args = parser.parse_args()
    origin = urllib.parse.urlparse(args.api_url)
    password = os.environ.get("AGENTCORE_OCI_FIXTURE_PASSWORD")
    if not args.disposable or not password or origin.scheme != "http" or origin.hostname not in ("localhost", "127.0.0.1") or origin.username or origin.path not in ("", "/") or origin.query or origin.fragment:
        parser.error("Use --disposable with a loopback Synthetic API and a non-user fixture password in the documented environment variable.")
    if not re.fullmatch(r"[a-z][a-z0-9-]{0,39}",args.profile_prefix): parser.error("Use a bounded lowercase disposable profile prefix.")
    images = [args.workbench_image, args.alternate_image]
    if any(not image.startswith(args.registry + "/") or "@sha256:" not in image for image in images) or images[0].split("@")[-1] == images[1].split("@")[-1]:
        parser.error("Supply two different approved immutable images from the selected fixture registry.")
    token = None

    def request(path, body=None, method=None):
        headers = {"Content-Type": "application/json"}
        if token:
            headers["X-AgentCore-Owner-Capability"] = token
        message = urllib.request.Request(args.api_url.rstrip("/") + path, headers=headers,
            data=None if body is None else json.dumps(body).encode(), method=method or ("GET" if body is None else "POST"))
        with urllib.request.urlopen(message, timeout=120) as response:
            return json.load(response)

    if request("/health")["profile"] != "Synthetic":
        parser.error("Actual OCI checks require disposable Synthetic Core; provider calls are excluded.")
    token = request("/api/v1/local/owner-capability", {})["token"]
    credential = request("/api/v2/admin/container-registry-credentials", {"displayName": "Disposable OCI registry", "kind": "Password",
        "profile": {"registry": args.registry, "username": args.username, "repositoryPrefixes": []}, "protectedValue": password})
    ids = [args.profile_prefix+"-workbench",args.profile_prefix+"-alternate"]
    for profile_id, image in zip(ids, images):
        saved = request("/api/v2/admin/sandbox-profiles/" + profile_id, {"expectedRevision": 0, "name": "Private " + profile_id.removeprefix("private-"),
            "description": "Disposable private OCI acceptance fixture", "enabled": True, "imageReference": image, "registryCredentialId": credential["credentialId"]}, "PUT")
        assert saved["availability"] == "NeedsVerification"
        observed = request("/api/v2/admin/sandbox-profiles/" + profile_id + "/verify", {"expectedRevision": 1})
        assert observed["passed"] and observed["profile"]["availability"] == "Eligible"
        assert password not in json.dumps(observed)
        print(profile_id + ": actual disposable verification passed", flush=True)

    # Cached private bytes never turn a remote reference into anonymous authority.
    anonymous_id=args.profile_prefix+"-public-denied"
    request("/api/v2/admin/sandbox-profiles/"+anonymous_id,{"expectedRevision":0,"name":"Anonymous private source denied","description":"Disposable cached-image auth boundary","enabled":True,"imageReference":images[0]},"PUT")
    assert not request("/api/v2/admin/sandbox-profiles/"+anonymous_id+"/verify",{"expectedRevision":1})["passed"]
    print("Cached private image without a credential rejected",flush=True)
    rotated = request("/api/v2/admin/credentials/" + credential["credentialId"] + "/value", {"expectedRevision": credential["revision"], "protectedValue": secrets.token_hex(32)}, "PUT")
    try:
        for profile_id in ids:
            current = request("/api/v2/admin/sandbox-profiles/" + profile_id)
            assert current["availability"] == "NeedsVerification"
            observed = request("/api/v2/admin/sandbox-profiles/" + profile_id + "/verify", {"expectedRevision": 1})
            assert not observed["passed"]
            print(profile_id + ": cached-image wrong authorization rejected", flush=True)
    finally:
        request("/api/v2/admin/credentials/" + credential["credentialId"] + "/value", {"expectedRevision": rotated["revision"], "protectedValue": password}, "PUT")
    for profile_id in ids:
        assert request("/api/v2/admin/sandbox-profiles/" + profile_id + "/verify", {"expectedRevision": 1})["passed"]
    assert password not in json.dumps(request("/api/v2/admin/credentials"))
    print("Restored protected authorization and safe readback passed. Disposable evidence retained.")


if __name__ == "__main__":
    try:
        main()
    except (AssertionError, urllib.error.URLError, ValueError, KeyError):
        raise SystemExit("OCI acceptance failed; inspect the disposable profile evidence. Protected values and upstream bodies withheld.")
