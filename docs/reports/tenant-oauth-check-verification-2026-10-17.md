# Tenant Google connection-check verification — 2026-10-17

Baseline: `22e56d3822f8b00348fee0ad72dabcc43377e611`. This checkpoint adds canonical operator verification of a connected tenant Google grant. The full proposal remains active; trusted tenant execution admission, runtime credential consumption and effect checks remain required. Existing Real hosts and user data were preserved. All tests use disposable fixtures and offline/Synthetic providers.

## Delivered boundary

`POST /api/v2/tenants/{tenantId}/credentials/{credentialId}/oauth/check` uses cookie/CSRF authentication, the actual human Account/session, actual resource ownership and independent current `credentials.read`, `credentials.manage` and `credentials.use`. It compares security and logical witnesses before provider work, serializes overlapping checks per owned Credential, and rechecks live identity, permissions, provider descriptor and logical/secret generations afterward. Permission revocation is evaluated before exposing a stale-witness error.

The Infrastructure verifier accepts only the shared tenant OAuth client stamp and matching Synthetic mode. Real-mode refresh uses the existing fixed Google endpoints, bounded 64 KiB/depth-16 responses, no redirects/loggers/retries and a 20-second HTTP timeout. The Application check has a 30-second provider deadline and rejects late uncooperative completion. It verifies token type/expiry, any returned purpose scopes and the saved profile email. Access/refresh tokens, provider DTOs and response bodies stay inside Infrastructure; only an optional protected replacement grant or reconnect-required outcome crosses the port. Synthetic makes zero provider requests.

Both stores commit a derived check transition under current human/session and security/logical/secret witnesses, with audit. Routine refresh rotation increments only SecretRevision, preserving logical revision/AuthEpoch and the bound account. Explicit permanent grant rejection, missing required scopes or mismatched profile identity clears local protected authority and marks ReconnectRequired, advances logical/secret/AuthEpoch and supersedes older consent work. Transient, malformed or unavailable responses retain the saved grant; wrong client/mode fails closed without a legacy fallback. Concurrent disable, logout, use revocation, rotation, cancellation or deadline cannot restore prepared authorization. No table, migration, parallel credential inventory or token cache was added.

## Executed checks

| Check | Result |
| --- | --- |
| API build, `dotnet build src/AgentCore.Api/AgentCore.Api.csproj --no-restore --nologo -v minimal` | Passed; zero warnings/errors. Subsequent test builds include final serialization/deadline changes. |
| Infrastructure filter `FullyQualifiedName~OAuth_check\|FullyQualifiedName~OAuth_orchestration` | 42 passed before adding the two late-deadline cases. Includes the 18 existing orchestration regressions affected by the extracted shared human/session authority helper. |
| Final Infrastructure filter `FullyQualifiedName~OAuth_check` | 26 passed. Both stores cover Gmail/Drive rotation, temporary failure retention, permanent rejection and SQLite reopen; exact read/manage without use; overlapping checks; use revocation, logout, disable, competing rotation, cancellation, changed host and deterministic late completion. Offline provider tests reject oversized/malformed/token-type/expiry responses, missing scopes, wrong identity/client and Synthetic-mode mismatch, with safe failures. |
| API filter `FullyQualifiedName~TenantCredentialAccessTests` | 10 passed after serialization/deadline changes. Cookie/CSRF Synthetic Gmail/Drive checks return safe no-store metadata and preserve logical revision; anonymous, stale and disconnected checks deny. Existing management/binding/OAuth behavior remains covered. |
| Final HTTP filter `FullyQualifiedName~Tenant_OAuth_HTTP_connects` | 2 passed after adding explicit foreign-tenant and missing-CSRF assertions for the new check route, covering both Google purposes. |
| Final provider filter `FullyQualifiedName~OAuth_check_provider_bounds` | 9 passed after normalizing unavailable profile responses to the same safe 503 check failure; includes response bounds, narrow scopes, identity, client and mode. |
| Documentation checks | 10 changed Markdown files, 684 local links/anchors and 20 complete JSON examples passed; fences are balanced and `git diff --check` passed. |

The first refresh test run exposed record equality comparing fresh immutable collection references; assertions were corrected to inspect retained protected payload, security/logical/secret witnesses and provider calls. The implementation then passed the affected integration cases. Test fixtures verify schema/model parity and dispose their own SQLite/InMemory roots; no user database or listening Real host was touched.

## Limits and next obligations

This is an operator connection check, not an execution identity or a general secret-return API. The canonical UI does not yet expose Check access; live Google refresh/consent was not exercised. Runtime contexts still lack trusted tenant admission, so this work does not infer tenant identity from a caller-supplied Instance ID or enable legacy sinks to read owned grants. Tenant-aware Session/Run/Hub/tool/background integration, actual secret consumption and independent typed effects, other resource/store families, Sandbox/decommission, complete access/binding UI, durable approvals, obsolete global authority removal and final schema/reset consolidation remain pending. No CI, deployment, reset or application milestone acceptance occurred.
