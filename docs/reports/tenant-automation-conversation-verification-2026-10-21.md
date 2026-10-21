# Tenant Automation existing-conversation verification — 2026-10-21

This module adds canonical Existing-Session execution for tenant Automations. It preserves the existing Session Runtime, Run, occurrence, consent and native effect owners. It does not accept the full original proposal, an application milestone, voice/System/audit, final obsolete-authority/schema removal or the exhaustive final security/design audit.

## Behavior

The native Automation editor explicitly chooses Separate Background work or Selected conversation. The shared tenant conversation picker requires an active same-Instance UserChat target. Existing execution uses that conversation's model, clears separate completion delivery and refuses conflicting explicit Automation model overrides. Authoring and manual admission require current human raw target read and Agent Chat access.

Manual, Schedule, Core and Webhook ingress creates a fresh independent ConversationResponse Run in the actual selected Session, preserving its human creator, UserChat origin and retained history. Automatic origins have no human requester; manual ingress retains its actual invoker. No new Session, fake human message, conversational parent or source input ID is invented. The executor needs Run creation and exact target read/Chat authority without Session-create permission. Missing secondary authority cannot become an exceptional grant. Admission consent freezes the exact target, Session revision and durable input boundary. Current scope filters approval counts/paging before exposure. Busy targets reject direct admission; approved busy signals remain pending.

The Runtime projects history through the frozen input boundary and derives authored Automation behavior from admitted task intent. Protected native/file tools retain actual task lineage through approval successors with empty source input references. Current lifecycle, source, executor/requester, communication Review and qualified reviewer checks repeat before execution and publication. Answer-bearing progress and terminal writes share owning-store checks. Refusal fails the Run and changes only an already stored matching streaming stub to failed, without inserting the rejected answer. Accepted work retains its frozen target despite later registration edits/cancellation.

Closed schema 4 adds the exact existing target and revision/input-boundary witness. Historical versionless, schema-2 and schema-3 serialization remains unchanged. The new migration restores the prior 79 guards and installs four target guards (83 total), adding no table, queue or worker. Downgrade refuses retained tenant existing-target rows or schema-4 consent; legacy rows round-trip unchanged.

## Failures found and corrected

- Initial model-pin comparison used class reference equality; owning admission now compares its actual serialized value.
- Empty source input references initially reached human-input-only Runtime readers. Both readers now use the exact typed independent input boundary, also retained in protected-operation successors.
- Controlled mid-model raw Chat revocation/new communication Review refused final output but left a persistence-paused Run or streaming stub. The shared protected-output refusal path now fails the Run and atomically settles prior stored progress without adding the answer.
- Independent MCP exposed a false-positive browser assertion: counting an assistant row had accepted a failed empty stub. The strengthened repeatable test requires completed nonempty replies. The actual failure came from classifying authored tasks by a legacy dedupe prefix, which supplied Background instructions to an existing-conversation task. Runtime now uses the admitted Automation task intent; a shipped-Synthetic-model case checks this independently of canned semantic models.
- Event preview assertions were extended for the additional exact conversation witness. The older account-background fixture still prohibited separately authorized child-work discovery introduced by the earlier child module; its assertion now retains that capability while continuing to reject direct Email send/Drive rename. SQLite guard testing uses a typed Guid parameter for the actual Guid primary key rather than a case-mismatched text selector.
- Actual event execution then exposed a SQLite terminal guard failure: a prose prefix around structured occurrence evidence hid its causal fields from the incumbent Core event parser. Existing execution now retains the exact occurrence JSON, with untrusted labeling supplied by prompt assembly. Both-store event checks also assert emitted terminal root/depth/visited Automation facts.
- Final frontend compilation found unsupported Testing Library `exact` query options in three new assertions; removing them retains the already exact accessible names.

## Focused local evidence

No CI/Actions, full local CI suite, live provider/OAuth/mailbox call, deployment or production data was used. All app hosts and databases were disposable Synthetic fixtures. The unrelated API on port 5080 was untouched.

| Check | Result |
| --- | --- |
| API | 68 distinct focused API cases pass across the final selections: the main group passed58, and the corrected structured-evidence group passed15 (11 overlap, four newly complete Webhook/Core cases), plus two protected account-output revocation and four Background manual/Schedule report regressions. This is combined evidence, not a claim that the earlier62-case command passed. Initial127/135 and later58/62 runs exposed the failures described above. |
| Application | 65 passed: closed consent/hash/malformed/historical contracts, independent tool shape and affected authored-task prompt assembly. |
| Infrastructure | Four passed on the final binary: migration upgrade/downgrade/rebuild, 83 restored guards, historical destinations, model parity and foreign-key integrity. |
| Frontend | Initial affected group 132 passed across Automation editor, shared picker and approvals; final affected approval recheck passed116 cases after the test-query compilation correction. The incumbent JSDOM NaN-height warning remains. |
| Browser CLI | Three passed on repaired binary: selected conversation with explicit target/save gating, actual409 retained draft/reload/save, completed nonempty reply in same Session/no fake user/no new Session, exact11-field admission Review/approve/second completed reply; configured-report and admission regressions also pass. |
| Builds | Final API/Application/Infrastructure test builds pass with zero warnings/errors. Frontend build passes with incumbent bundle warning. |

The combined final API selections cover both stores, actual Webhook/Core production, manual/Schedule, existing human history, selected model/conflicting override, read/Chat/Run Review, primary-only grants, rejection, missing authority/no reviewer, stale/archive/foreign/mixed report/busy targets, frozen accepted target and cancellation, mid-model access/Review changes, exact scope filtering before count/paging, pending/approved SQLite host recreation, direct database tampering/downgrade refusal, and actual native file/publication successors including cold SQLite recovery. The structured-evidence group also verifies terminal Core causal root/depth/visited facts in both stores. This table records only results established by the executed logs.

Commands (repository root except browser commands in `web/`):

```sh
dotnet build tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Existing_conversation_|FullyQualifiedName~Model_immediate_background_account_actions'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Existing_conversation_event_|FullyQualifiedName~Existing_conversation_Automation_file_Review|(FullyQualifiedName~Existing_conversation_Automation_keeps&(DisplayName~manual|DisplayName~scheduled|DisplayName~synthetic-default))'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Model_connected_account_read_waits&DisplayName~final-family-revoked'
dotnet test tests/AgentCore.Api.Tests/AgentCore.Api.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~Configured_Automation_completion_preserves&(DisplayName~manual|DisplayName~scheduled)'
dotnet build tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantEventAdmissionContractTests|FullyQualifiedName~TenantIndependentTaskContractTests|FullyQualifiedName~PromptContextBuilderTests'
dotnet build tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~TenantAutomationConversationMigrationTests|FullyQualifiedName~TenantAutomationCompletionMigrationTests|FullyQualifiedName~TenantAutomationMigrationTests|FullyQualifiedName~TenantEventMigrationTests'
pnpm --dir web run test --run src/features/access/TenantAgentAutomations.test.tsx src/features/admin/AdminSessionPicker.test.tsx src/features/access/TenantApprovals.test.tsx
pnpm --dir web run build
CI=1 TENANT_CHAT_API_PORT=5099 TENANT_CHAT_WEB_PORT=5199 TENANT_CHAT_TEST_RUN_ROOT=/private/tmp/tenant-conversation-browser-final pnpm exec playwright test --config playwright.tenant-conversation-automation.config.ts --grep 'selected-conversation|configured completion|exact Automation admission'
```

The .NET runner required its local-listener sandbox exception; an unprivileged discovery attempt failed before discovery and is not counted. Independent Playwright MCP exercised native sign-in, destination selection, real409 retained blocked draft, explicit reload/save, enable/save, deliberate Run and actual Activity. It reproduced the pre-fix tool failure. On the repaired host, deliberate Run returned201, owning history contained one completed nonempty `Hello!` reply and Activity displayed [Completed/Response](assets/tenant-automation-conversation-2026-10-21/mcp-completed-run.yml). Final MCP console had zero errors; the earlier intentional409 was the only resource error. Aborted retired editor reads are distinguished from product failures. A snapshot export outside MCP's permitted roots was rejected; the returned accessibility snapshot supplied the inspected evidence.

## Bounded UI finish

Five valid scrolled Execution-section captures were opened individually: [1440px](assets/tenant-automation-conversation-2026-10-21/existing-execution-1440.png), [1024px](assets/tenant-automation-conversation-2026-10-21/existing-execution-1024.png), [768px](assets/tenant-automation-conversation-2026-10-21/existing-execution-768.png), [390px](assets/tenant-automation-conversation-2026-10-21/existing-execution-390.png), [320px](assets/tenant-automation-conversation-2026-10-21/existing-execution-320.png). The CLI checks document overflow at each width. These are intentionally scrolled configured-section captures, not full-editor/first-viewport acceptance. The final functional assertion correction did not alter product UI or these captures.

One [detector pass](assets/tenant-automation-conversation-2026-10-21/detector.json) returned `[]`. A fresh reviewer returned [ship](assets/tenant-automation-conversation-2026-10-21/finish-review.md) with no material fixes at this scope. A separate documenter compared current sources and sampled1440/320 captures, then updated existing DESIGN/surface context only. Native Ant Design v6, shared picker, spacing and wrapping footer owners remain; no new CSS, token, font, kit or asset was introduced. Historical sidecar hash `8bdc3659a25b706454ac8cc0394323c5e0e60ea1ade616756b180efbaecef49b` remains unchanged; pre-existing fingerprint drift was reported without repair.

Loading, hover, empty choices, denial/stale/access-loss visuals, expanded options, measured contrast and the approval drawer have no dedicated visual acceptance from these captures. Existing-target voice, every other protected native operation family, explicit typed-wait/queued-human interleaving, exhaustive proposal/security/design qualification and final authority/schema removal remain outside this focused execution matrix. Previously verified independent tool owners are reused, without inflating this module's executed coverage. The original goal remains active.
