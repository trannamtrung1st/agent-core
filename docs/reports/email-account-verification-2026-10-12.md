# Account-scoped Email verification — 12 October 2026

Implementation started on `9d664ca6` (`refactor`). The three intervening main commits through `10b156bb` were reviewed and are included in the final candidate; this is follow-on evidence, not a new milestone freeze. The user explicitly deferred Real setup/execution. Existing historical reports and P10/P11 remain unchanged. SMTP defaults disabled. No user database, key ring or browser profile was reset.

## Implemented behavior

One protected Password Credential plus typed EmailAccount purpose/profile; existing Instance bindings; per-account IMAP/SMTP/Synthetic factory; Browser sink separation; required TLS/public DNS-pinned egress; bounded read-only IMAP PEEK/UIDVALIDITY; durable Core drafts; full exact sender/account/To/Cc/Bcc/subject/body/revision approval; immutable-draft at-most-once dispatch and inspectable receipts; owner-only account chooser with CAS binding and one safe fresh same-Session continuation. General-assistant v25 and secretary v10 add account discovery/request. Gmail REST remains contract-tested, testing-only; no global fallback.

## Executed user journeys

- Playwright MCP against isolated Synthetic API :5290 / frontend :5390: create a Gmail fixture account using masked input; inspect safe account details; test incoming/outgoing independently and observe `Synthetic — no server connection`. Inspect desktop email editor and mobile details; request an account, explicitly select and allow it, observe one completed continuation and one original user entry, then cancel another request. Verify focused cancellation receipt and 390×900 document bounds equal the viewport. Form lifecycle/default/ID and receipt-banner overflow issues found during inspection were corrected as a batch.
- Synthetic Playwright at 1440/768/390px: create/edit/rotate/test, request account access, confirm no automatic selection, reload current durable request, explicitly allow account and alias, observe one fresh continuation and exactly one original user entry, then cancel another request and observe the safe receipt. Approved send displays sender, account alias, Bcc and complete expected content. Existing credential binding/CRUD/mobile navigation and SelfManagement approval/continuation regressions pass.
- Backend integration: protected aggregate/API CAS, owner capability, immutable Purpose, Browser rejection, account/Session isolation, two aliases/accounts, SQLite migration and key-ring/reopen, stale rotations/unbinds, long exact preview, concurrent distinct approvals, unfinished-send restart, fake TLS transport PEEK/UIDVALIDITY, envelope-only Bcc, drop-after-DATA uncertainty and invalid certificates.
- Isolated Compose project `email-account-verify-20261012` on :5318: SQLite volume and credential protection key ring survived container recreation; script passed and removed its container/network while preserving its disposable volume.

## Checks

Commands ran with repository Node 22 / pnpm 9 and .NET 10, without hosted-provider keys:

| Check | Command / result |
| --- | --- |
| Backend broad suites | `dotnet test AgentCore.sln --no-restore --nologo -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s`: Domain 195 passed; Application 1,694 passed / 8 opt-in skips; Infrastructure 1,190 passed / 10 opt-in skips. API final solution invocation still finishing at candidate recording time. |
| Full API separately | `dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo -m:1 -nodeReuse:false --blame-hang --blame-hang-timeout 120s`: 527 passed / 3 opt-in live skips. |
| Focused mail Infrastructure | `dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --filter 'FullyQualifiedName~ImapSmtpEmailProviderTests|FullyQualifiedName~EmailAccountIntegrationTests|FullyQualifiedName~SystemCredentialTests|FullyQualifiedName~GmailEmailProviderTests' --nologo -m:1 -nodeReuse:false`: 34 passed. |
| Frontend broad suite | `pnpm --dir web run test --run --maxWorkers=2`: 115 files / 928 passed. Subsequent STARTTLS editor regression and picker refresh fencing: `pnpm --dir web run test --run src/features/admin/EmailAccountDialog.test.tsx src/features/chat/EmailAccountPicker.test.tsx --maxWorkers=1`: 4 passed. Hosted CI includes the final extra test. |
| Build | `pnpm --dir web run build`: passed; existing SignalR annotation / bundle-size warnings. |
| Synthetic affected journeys | `pnpm --dir web exec playwright test email-accounts.spec.ts email-harness.spec.ts system-credentials.spec.ts credential-bindings-work.spec.ts self-management.spec.ts --project=synthetic`: 9 passed on isolated :5328/:5428. Final keyboard/send recheck: 4 passed on :5338/:5438. Final chooser layout/focus recheck: 3 passed on :5348/:5448, at 1440/768/390px. Runs use `CI=1 PLAYWRIGHT_FAITHFUL_MANUAL=1`, disposable SQLite/workspace/key-ring paths and dedicated fixture ports. |
| Compose / key ring | `AGENTCORE_COMPOSE_PORT=5318 COMPOSE_PROJECT_NAME=email-account-verify-20261012 bash scripts/compose-sqlite-volume.sh`: passed. |
| Migration model | `dotnet ef migrations has-pending-model-changes --project src/AgentCore.Infrastructure --startup-project src/AgentCore.Infrastructure --no-build`: no changes. Additive migration defaults existing Credentials to General and retains encrypted payload/revision; upgrade test passes. |
| Static documentation | `git diff --check` passed; 725 local Markdown links/images/anchors checked successfully. | Initial stale Definition version expectations were updated; an initial unrestricted frontend run produced seven Admin test timeouts under concurrent heavy checks, retained as failed evidence. Browser fixture/default-field bugs and the durable continuation admission receipt gap were corrected and rechecked. No failure is relabeled as success.

## EA matrix and deferred gates

| Acceptance | Offline evidence |
| --- | --- |
| EA-01–05 | `EmailAccountIntegrationTests` (InMemory/SQLite), `EmailAccountApiTests`, existing Credential/role/projection suites: additive migration, protection/revisions, bound inventory, shared aliases, isolation, deterministic ambiguity and Browser sink rejection. |
| EA-07, 09–13 | `ImapSmtpEmailProviderTests`, `EmailAccountIntegrationTests`, existing `EmailToolTests` and `GmailEmailProviderTests`: PEEK/UIDVALIDITY, reopened Core drafts, full exact approval, acceptance/uncertainty, concurrent distinct approvals and revoked/rotated authority. Owner-only receipt API covers unfinished and immutable completed receipts. |
| EA-14–16 | `EmailSelectionRuntimeTests`, `email-accounts.spec.ts`, picker components and existing continuation/admission suites: same-owner/session access, fresh pins, dedupe, cancel/expiry/newer input/detach/revision fences and unattended NeedsAttention. Read-only checkpoint/effect eligibility is also reviewed at both Runtime and durable admission boundaries. |
| EA-17–19 | Fake TLS transports, public-DNS policy tests, existing header/injection/approval tests and captured model requests: reject downgrade/invalid certificates/private addresses, preserve independent authority and redact protected values. This is bounded adversarial test evidence, not a claim to prove every possible malicious email. |
| EA-20–21 | Responsive interactive Playwright/MCP, keyboard explicit choice and focus receipt, safe Admin create/edit/rotate/test plus existing binding/send/SelfManagement regressions. Full backend/frontend suites retain legacy contracts. | EA-06 has separate Synthetic Admin observations and fake TLS authentication without dispatch, not live Gmail connectivity. EA-08 preserves Gmail REST contract tests but does not enable that adapter in the production factory until typed OAuth profiles exist. EA-22 live Gmail/delivery remains deliberately deferred. EA-23 canonical owners, setup, README/TODO/plan and DESIGN/PRODUCT context are synchronized. EA-24 local checks are reported precisely; exact behavior-commit hosted CI is unrun and full release acceptance is not claimed.

Custom DNS rebinding/private-address policy tests exercise the validation and pinned-socket boundary, not a real public DNS server. SMTP Sent evidence means server acceptance, not guaranteed final delivery. Unfinished/Indeterminate receipts never cause automatic retries. General Credentials default to General through the additive migration; retain the existing SQLite/key-ring backup policy.

## Screenshots

![Desktop email editor](assets/email-account/email-edit-1440.png)

![Mobile account details](assets/email-account/email-admin-390.png)

![Desktop owner chooser](assets/email-account/email-chooser-1440.png)

![Tablet owner chooser](assets/email-account/email-chooser-768.png)

![Mobile owner chooser](assets/email-account/email-chooser-390.png)
