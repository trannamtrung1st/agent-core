# Tenant Artifact delivery verification — 2026-10-19

Bounded tenant rich references now use actual Session-owned sources and current executor read policy before mailbox mapping. Current human reads independently filter live delivery and protected history. Existing Artifact cards, preview/download and durable Session block receipts retain their owners. This checkpoint extends `0a557781`; it does not complete the full multi-tenant proposal.

## Implemented boundaries

- The supervised model worker prepares at most 32 candidate references through the bound tenant authorizer. Actual owner/Session, admitted/current capability, current Run claim, independent Session/Artifact reads and current Allowed read policy are required. The mailbox receives a response-local synchronous authorization set; no store I/O or read grant is added there.
- A live recipient receives only currently readable actual owned Artifacts. Metadata comes from the canonical human read service; model-selected labels are discarded. Publication authority and its exceptional grant do not authorize Agent or recipient reads. Policy-required Artifact reads remain unavailable pending their separate exact-consent conversion.
- Existing attachment/response evidence admits at most 32 distinct delivered Markdown/Unknown/Artifact block IDs. Typed saved identity and actual Artifact source/current human read are checked again at the existing store commit. Existing `DisplayDelivered` flags survive late writer snapshots; display receipt credit never becomes heard credit.
- Tenant cards reuse `ArtifactView`, `ChatFileCard`, `FilePreviewButton` and the shared read-only preview. Tenant identity travels explicitly through metadata/content/Office requests without owner fallback or browser-wide metadata caching. Read loss removes private filename/content/actions. The shared Modal title reserves Close space with Ant Design tokens.
- DOCX/XLSX human previews reuse `TenantArtifactService.ProjectContentAsync` and the existing Office processor. Size checks precede content reads, integrity is checked, and original-human/security/Session/source fences repeat after processing. No new schema, receipt ledger or file processor is introduced.

## Focused execution

All provider work used Synthetic adapters and zero outbound provider attempts. Local logs are in `/private/tmp` and are machine-local evidence, not committed artifacts.

| Check | Observed result |
| --- | --- |
| API reference boundaries (`agent-core-artifact-reference-boundaries.log`) | 6 pass: both stores, independent Agent read absent/present, policy-required read unavailable, recipient exact-file grant and revocation |
| API reference/review/text-receipt regression (`agent-core-artifact-delivery-api-regression.log`) | 24 pass: six reference cases, four actual reviewed successor rich references, fourteen existing attachment/text race cases |
| Infrastructure source/claim boundary (`agent-core-artifact-reference-source-boundary.log`) | 2 pass: actual source, foreign Session, fixture ID, wrong claim and read revocation |
| Application receipt carry (`agent-core-artifact-receipt-carry-fixed.log`) | 5 pass, including reproduced late pre-envelope snapshot erasure fixed at the shared carry owner |
| Frontend source/card checks (`agent-core-artifact-delivery-ui-unit.log`) | 22 pass |
| Existing preview/conversation/tenant transport (`agent-core-artifact-delivery-web-regression.log`) | 48 pass across four matched suites |
| Tenant rendered-block receipt and realtime races (`agent-core-artifact-delivery-wire-unit.log`) | 66 pass |
| Final shared preview/card/source checks (`agent-core-artifact-delivery-final-web.log`) | 18 pass |
| Final Office/read and reviewed rich API checks (`agent-core-artifact-delivery-final-api.log`) | 6 pass: actual owned DOCX/XLSX byte/hash projections, exact grants/revocation and wrong Session through both stores, plus four rich continuations |
| Existing exact publication Review/grant flow (`agent-core-artifact-delivery-review-regression.log`) | 8 pass after extending the common fixture |
| Final interactive fixture (`agent-core-artifact-delivery-final-ui-host.log`) | 6 pass, with the Allowed memory case held for actual browser operations |
| `pnpm --dir web build` (`agent-core-artifact-delivery-final-build.log`) | Pass; existing large-chunk warning |

The first ordinary sandboxed API invocation could not open a local MSBuild pipe; its owned process was stopped and the focused command passed with local pipe/loopback access. Earlier failing reference-history and carry checks reproduced defects that were fixed and rechecked. These are not unrun passes.

## Actual browser journey

Playwright MCP used a disposable Synthetic Kestrel host and Vite on loopback. The C# SignalR fixture performed initial model publication/live delivery and exact receipt; the browser subsequently opened already-acknowledged history as the separately scoped reader. This is not evidence of initial live JavaScript publication.

The ready conversation showed the authorized long-name card. Preview opened through tenant metadata/options/content requests; Rendered view escaped/removed executable HTML, and Source showed the literal `<script>` text with zero script elements in the preview. Escape closed the native Modal and restored its exact Preview opener. The browser download matched all **1,784 UTF-8 bytes**, including Vietnamese text and literal HTML.

After revoking the reader's exact Artifact role, Download received 403, removed the filename and Preview/Download actions, and showed File unavailable. Explicit Retry remained denied without leaking metadata. A full reload waited for Ready and the surrounding published-response text: the private card was absent while conversation text remained. Expected denied requests and aborted obsolete reads were observed; the final ready page had no console errors. No owner fallback was used. Only task-owned hosts were stopped afterward.

Final captures under `.impeccable/review/artifact-delivery/` cover card and preview at 1440, 1024, 768, 390 and 320px (height 1050), Source at 320, read loss at 320 and revoked ready history at 320. Root opened all thirteen. No document overflow occurred. Preview title/Close boxes did not overlap at any width; at 320 the title ended at x260 and the 40px Close control began at x268. Source intentionally retains internal horizontal scrolling with Wrap lines off. Existing file card gaps/action sizing remain shared.

A fresh no-history Impeccable finish reviewer inspected all thirteen captures and returned **ship** at this bounded scope, with no material fixes. A separate fresh documenter compared the finished extension to incumbent tokens, CSS and shared owners and wrote `.impeccable/surfaces/web-src-features-chat-artifactview-tsx.md`. PRODUCT, DESIGN and sidecar remain unchanged. No separate direction/quality card was supplied for this incumbent extension; that limitation was disclosed. The active hook found no deterministic issue in the final shared preview edit; no redundant detector pass was run. Dedicated loading and recoverable-error visual captures remain unrun; focused component checks cover their behavior.

## Scope and remaining verification

CI was not requested or run. This report proves focused local behavior and store delivery boundaries, not whole-proposal acceptance, application-host restart of rich receipts, real-provider behavior, native-device accessibility or every Office rendering feature. C# live publication and browser history checks are separate evidence. General free-text disclosure filtering is unchanged.

Generic model Artifact read/list, exact read consent, Browser/Workspace/Office publication adapters, owned-blob cleanup, autonomous families, remaining resource/transport conversion and final authority/schema cutover remain open. The existing sign-in flow returned to Organizations despite an encoded Chat return path; direct authorized Chat navigation worked. Preserve that observed routing follow-up in final proposal review. Pre-existing design-context fingerprint drift remains reported rather than repaired as a side effect.

The unfinished credential-check extension mentioned in the earlier cleanup question is absent from this working tree; its machine-local backup remains under `/private/tmp/agent-core-credential-check-draft-20261017`. No supported flow was removed in this checkpoint.

Changed-document relative paths and Markdown fences were checked across eleven Markdown files; no missing paths or unbalanced fences were found. New surface-brief links/source paths and its owning UX anchor were checked separately by the documenter. `git diff --check` passed.
