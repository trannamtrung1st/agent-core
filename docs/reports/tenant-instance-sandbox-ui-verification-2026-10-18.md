# Tenant Instance Sandbox UI verification — 2026-10-18

Status: focused Phase 7 implementation over `f90c3b2d2bd50fa765644b5faf3eb379cc07d937`. The full multi-tenant IAM proposal remains active. This supplies Instance Sandbox settings and saved setup verification; Session profile/reset/process UI and other proposal families remain open.

## Implemented scope

The canonical `/t/{tenantId}/admin/agents/{instanceId}/sandbox` page uses the existing original-human fenced setup, sparse settings mutation and disposable verification owners. The setup projection adds Instance name and adopted Definition version from its same bound snapshot; the API explicitly maps lifecycle to its named wire value. No schema, migration, new backend route, executor or authority is added. The [interaction contract](../19-multi-tenant-iam-ux.md#instance-sandbox-interaction-contract) preceded implementation; [Frontend](../13-frontend-implementation-spec.md#tenant-instance-sandbox-configuration-and-verification) owns final behavior.

The page reuses `TenantAgentHeading`, `InstanceSettingsSection`, its controller, inheritance/ceiling fields, native confirmation and scoped draft guard. Explicit tenant profile choices and summary bypass global inventory, unrelated model reads and owner summary calls. Generic Settings links to the dedicated destination. The organization catalog and Instance page share `SandboxVerificationDetails` and bounded escaped streams. No CSS, tokens, assets or second UI kit were added.

Configuration `canManage` and verification `canVerify` remain independent. Null protected sections/partial catalogs do not hide independently authorized verification. Exact saved Instance/profile/security witnesses, clean setup, pending cleanup and busy guards bound each explicit attempt. Current observations, reusable profile eligibility and retained exact/latest attempts remain separate. Native confirmation names agent, organization, revisions and ordered disposable Profile/Instance initialization. Refresh never replays initialization.

Stale saves retain deliberate drafts but block submission/reset until a current read. Successful settings acknowledgements now require a matching Instance and valid revisions at the shared tenant transport boundary: semantically malformed success becomes `IdentityCompletionError`, retiring that submitted draft before read recovery. Exact read loss clears protected setup/evidence. Verification refresh restores focus to the labeled configuration container.

## Focused execution

| Check | Result |
| --- | --- |
| New Instance Sandbox interactions | 8 passed |
| Existing shared settings controller/fields | 11 passed |
| Existing tenant generic configuration | 3 passed |
| Organization Sandbox catalog/shared evidence | 8 passed |
| Scoped routes | 7 passed |
| Owned Instance Sandbox setup/verification HTTP | 3 passed |
| Independent verify authority, InMemory/SQLite | 2 passed |
| Frontend TypeScript/Vite build | Passed |

These selections total 42 focused tests. Commands:

```sh
pnpm --dir web exec vitest run src/features/access/TenantAgentSandbox.test.tsx src/features/admin/InstanceSettingsSection.test.tsx src/features/access/TenantAgentConfiguration.test.tsx src/features/access/TenantSandboxes.test.tsx src/features/access/accessRoute.test.ts
pnpm --dir web exec vitest run src/features/access/TenantAgentSandbox.test.tsx
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Owned_instance_sandbox_HTTP|FullyQualifiedName~Owned_instance_sandbox_verification_HTTP' -v minimal
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --filter 'FullyQualifiedName~Tenant_instance_sandbox_verification_uses_agent_manage_and_profile_read_with_independent_credential_authority' -v minimal
pnpm --dir web run build
```

The first combined frontend selection passed 36/37: the new stale-draft case expected `0.40` while native InputNumber renders `0.4`. Correcting the fixture and rerunning the new file passed all 8. The first build identified missing nullable constraint fields in typed test fixtures; those were supplied and the final build passed. The final HTTP run followed explicit named lifecycle mapping and assertions for same-snapshot heading metadata. No failed run was counted as acceptance. Existing JSDOM/act and Vite SignalR annotation/chunk notices remain; no runtime application exception was observed.

Frontend cases cover sparse exact-byte/witness saves without global calls, stale draft recovery with untouched fields following saved state, semantic malformed acknowledgement without replay, exact read revocation, verify-only permission/pending cleanup, incoherent witnesses, partial catalog and archived inspection. The two-store cases prove verification may remain authorized without configuration update permission and independently enforce Credential authority.

## Actual Synthetic browser journey

Playwright MCP used an isolated Synthetic InMemory API on 5098 and Vite on 5188. Health confirmed Synthetic before sign-in. Disposable state and captures are under `/private/tmp/agent-core-instance-sandbox-ui-UnkPm6`; existing user data/application and Real hosts were preserved. OpenSandbox and Browser execution were disabled.

- Created an organization profile using an immutable public digest and a saved Profile initialization script, published a scoped Definition and created the long-named Instance. No tag resolution or live registry/provider execution was required.
- Saved CPU 0.5 and `echo saved-instance-only` in the actual UI, producing Instance revision 2. Expected sparse overrides were read back.
- Retained a deliberate script draft while an independent real patch changed untouched memory to 512 MiB. The stale UI PATCH returned409, blocked Save and preserved the draft. Explicit Reload adopted memory512 and fresh witnesses; explicit save committed revision4.
- Confirmed section Reset with exact agent/organization context. The actual backend cleared overrides at revision5; CPU1, memory1024 and null Instance script inherited again. Save/reset did not execute scripts.
- Confirmed Verify saved Instance setup. The actual verification owner safely retained Failed evidence, `OpenSandbox is disabled in host configuration.` Latest and exact attempt reads were exercised. Reusable profile eligibility remained Needs verification; no successful SDK setup is claimed.
- Wrapped one actual successful PATCH with a controlled200 `{}` acknowledgement. The backend committed `echo acknowledged-without-response` at revision6, the UI retired the submitted draft and explicit Retry settings read recovered saved state without a second PATCH.
- A second actual disabled-host verification saved Failed evidence for revision6. Read refresh returned focus to `Instance Sandbox configuration`. Existing generic Settings Conversation remained usable through the shared owner.

The first malformed-response browser assertion matched duplicate inline/toast copy; selecting the inline result completed the assertion without a second write. The stale409 and controlled exact-read403 were expected errors. Initial StrictMode aborted reads had successful replacements. Observed tenant/shared Settings requests contained no `/api/v2/admin` or `/api/v1/local` route calls.

Controlled GET-only read-only, partial catalog, archived and exact403 projections establish presentation and recovery only. The real browser actor remained an Owner; these fixtures are not backend authorization proof. Independent verification authority is established by the two-store tests above. No initialization POST was made under a permission presentation fixture.

## Design evidence and finish handoff

The incumbent dark Ant Design v6 Operate system remains intact. Admin shell owns page inset; the shared 48rem form column, Settings Collapse/grid, native controls and 16px section/8px action gaps retain their owners. No generic control wrapper or replacement spacing system was introduced.

Settings top/lower and verification were inspected at 1440,1024,768,390 and320px; middle CPU rows, expanded evidence and existing shared Settings were additionally captured at wide/narrow widths. Document width matched viewport at all five widths. Main client/scroll widths matched 768,744,736,366 and296px respectively, with16px section gaps. Long heading/image content and action rows wrapped without horizontal overflow.

Valid local captures: `settings-final-*`, `settings-lower-*`, `settings-middle-{1440,320}`, `verification-*`, `verification-tail-{1440,320}`, `shared-settings-{1440,320}`, `read-only-320`, `partial-320`, `archived-320`, `denied-320`. Superseded initial `settings-{width}` captures used the wrong scroll owner and are excluded.

A fresh Impeccable finish reviewer returned **ship** for the inspected scope with no required code fixes. The reviewer identified an evidence gap: initial controlled read-only/partial captures omitted painted heading text despite supplied metadata. Both were recaptured after waiting for the exact heading and settled rendering; the full heading/lifecycle/version is now visible (heading x12,y257.4,width296,height56,opacity1 at320px). The original missing paint cause was not established. Denied presentation intentionally omits protected heading metadata. This bounded recapture required no source change.

Visual review does not independently establish authorization, measured contrast, loading/hover states, historical revision rendering or successful live initialization. Runtime interactions and tests above retain their own scope. The focused documenter compares this extension to current tooling context; historical sidecar fingerprints are preserved rather than regenerated.

## Remaining scope and limits

Session Sandbox profile/reset/process UI, other resource Access/effect families, Governance/platform, voice/rich Chat, live input arbitration, autonomous origins, final obsolete-authority/schema removal and the earlier SQLite preference reliability follow-up remain open. This successor supersedes Instance-Sandbox-UI-pending notes only; no full proposal or milestone acceptance is claimed.

No hosted CI/full local CI, live provider or Docker qualification, deployment, user-data migration/reset or Real-host restart was performed. Successful SDK initialization and live authenticated registry behavior through this UI remain unverified. Existing deterministic backend evidence retains its report scope.

Changed Markdown passed added local link/fragment and fence-balance checks; staged whitespace validation passed. The disposable API/Vite sessions were stopped and the Playwright tab closed. Captures and focused logs remain in temporary paths.
