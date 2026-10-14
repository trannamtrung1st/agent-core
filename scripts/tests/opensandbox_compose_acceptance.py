#!/usr/bin/env python3
"""Owner-authorized real API/Compose checks. Use only with disposable Core data."""

import argparse, json, urllib.error, urllib.parse, urllib.request

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--api-url", required=True)
parser.add_argument("--expect-profile", choices=["Synthetic", "Real"], required=True)
parser.add_argument("--without-sandbox", action="store_true")
args = parser.parse_args()
uri = urllib.parse.urlparse(args.api_url)
if (
    uri.scheme != "http"
    or uri.hostname not in ("127.0.0.1", "localhost")
    or uri.path not in ("", "/")
):
    parser.error("Use the disposable loopback API origin.")
base = args.api_url.rstrip("/")
token = None


def request(path, method="GET", body=None, owner=True):
    headers = {}
    if owner and token:
        headers["X-AgentCore-Owner-Capability"] = token
    if body is not None:
        headers["Content-Type"] = "application/json"
    with urllib.request.urlopen(
        urllib.request.Request(
            base + path,
            method=method,
            headers=headers,
            data=None if body is None else json.dumps(body).encode(),
        ),
        timeout=120,
    ) as response:
        data = response.read()
        return json.loads(data) if data else None


def check(value, name):
    if not value:
        raise RuntimeError(
            name + " failed; inspect retained disposable evidence locally."
        )
    print(name + ": passed")


health = request("/health", owner=False)
check(health["profile"] == args.expect_profile, "requested host profile")
token = request("/api/v1/local/owner-capability", "POST", owner=False)["token"]
platform = request("/api/v2/admin/sandboxes")
if args.without_sandbox:
    check(
        platform["state"] in ("Unavailable", "Disabled"),
        "optional missing server remains usable",
    )
    raise SystemExit(0)
check(platform["state"] == "Ready", "authenticated internal control-plane readiness")
profile = next(p for p in platform["profiles"] if p["id"] == "general-workbench")
# A dedicated profile in disposable Core storage never changes user configuration.
profile = {
    k: v
    for k, v in profile.items()
    if k
    not in ("id", "revision", "lastVerifiedAt", "verificationReason", "verification")
}
profile["name"] = "Disposable Compose acceptance"
profile["description"] = "Private local verification fixture"
script = """python -I -c 'import os; from docx import Document; from openpyxl import Workbook,load_workbook; assert os.getuid()>0; assert not os.path.exists("/var/run/docker.sock"); w=Workbook(); w.active["A1"]=5; w.save("compose.xlsx"); assert load_workbook("compose.xlsx").active["A1"].value==5; d=Document(); d.add_paragraph("Total: 5"); d.save("compose.docx"); assert Document("compose.docx").paragraphs[0].text=="Total: 5"' && node -e 'if(2+3!==5)process.exit(1);console.log("Node passed")' && echo Python-Word-Excel-passed"""
profile["initializationScript"] = script
current = None
try:
    current = request("/api/v2/admin/sandbox-profiles/compose-acceptance")
except urllib.error.HTTPError as error:
    if error.code != 404:
        raise
profile["expectedRevision"] = current["revision"] if current else 0
saved = request("/api/v2/admin/sandbox-profiles/compose-acceptance", "PUT", profile)
revision = saved["revision"]
result = request(
    "/api/v2/admin/sandbox-profiles/compose-acceptance/verify",
    "POST",
    {"expectedRevision": revision},
)
check(
    result["passed"]
    and result["exitCode"] == 0
    and "Python-Word-Excel-passed" in result["stdout"]
    and "Node passed" in result["stdout"],
    "actual Python Node Word Excel disposable setup",
)
check(
    result["profile"]["revision"] == revision,
    "verification preserves configuration revision",
)
profile.update(
    expectedRevision=revision, initializationScript="printf bounded-failure >&2; exit 7"
)
saved = request("/api/v2/admin/sandbox-profiles/compose-acceptance", "PUT", profile)
revision = saved["revision"]
image = request(
    "/api/v2/admin/sandbox-profiles/compose-acceptance/check-image",
    "POST",
    {"expectedRevision": revision},
)
check(
    image["passed"] and not image["stderr"],
    "image readiness is separate from initialization",
)
failed = request(
    "/api/v2/admin/sandbox-profiles/compose-acceptance/verify",
    "POST",
    {"expectedRevision": revision},
)
check(
    not failed["passed"]
    and failed["exitCode"] == 7
    and "bounded-failure" in failed["stderr"]
    and "bounded-failure" not in failed["message"],
    "broken setup has safe failure and separate bounded output",
)
observed = request("/api/v2/admin/sandbox-profiles/compose-acceptance")
check(
    observed["revision"] == revision and observed["verification"]["exitCode"] == 7,
    "persisted exact revision observation",
)
try:
    request(
        "/api/v2/admin/sandbox-profiles/compose-acceptance/verify",
        "POST",
        {"expectedRevision": revision - 1},
    )
except urllib.error.HTTPError as error:
    check(error.code == 409, "stale verification CAS rejected")
else:
    raise RuntimeError("Stale revision was accepted")
try:
    request("/api/v2/admin/sandboxes", owner=False)
except urllib.error.HTTPError as error:
    check(error.code == 401, "owner authorization required")
else:
    raise RuntimeError("Anonymous owner route was accepted")
