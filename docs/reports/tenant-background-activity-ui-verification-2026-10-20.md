# Tenant Activity and Background work UI — 2026-10-20

The canonical tenant UI now extends the [Activity/foreground backend](tenant-background-activity-verification-2026-10-20.md) and [immediate-background runtime](tenant-background-runtime-verification-2026-10-20.md). This is a bounded UI/read-model checkpoint. Creation Review, protected/native background effects, inbox/wait tools, scheduled/event/initiative origins, remaining transport/System/audit families and final authority/schema consolidation remain open. The full original proposal goal stays active; this report claims no milestone or whole-proposal acceptance.

## Implemented behavior

Tenant Agent Activity has Sessions and Runs routes, exact query-backed inspectors, reload and return navigation in the existing Ant Design Admin shell. Chat exposes the existing Background work inbox/badge. Shared Activity, Background Work, Run-history and file-inspector compositions receive an explicit tenant/Agent/principal-scoped source; legacy consumers retain their original source. Conversation/report links preserve tenant context. No second UI kit, visual identity or backend runtime is introduced.

Session and Run access remain independent. Missing initial Run authority withholds the original result and original-task files instead of requesting all Session files. Missing file access is not represented as a fabricated zero count. Exact current reads gate actions; a failed current detail/poll clears the actionable snapshot. Tenant list failures hide cached rows. Read acknowledgement keys include tenant, actual principal, Run ID and revision/update time.

Continue submits the displayed Session/security witnesses and confirms saved ChatList visibility before opening the same Session. A stale/lost/unconfirmed response leaves an explicit saved-fact read action and locks the command; this read never repeats Continue. An already-visible Session opens under current read/chat without another management command. Cancellation uses current security and exact displayed Run revision; an unconfirmed reply requires explicit Run reload before another action. These UI hints do not replace final server authorization.

The bounded read API now also exposes exact-Agent background summaries and Runs, including exact Agent/Run lookup through its actual owning Session. Both stores intersect independently readable Session and Run IDs before Agent-wide Run count/paging. Session mode, actual Model key, diagnostic ID and cancellation-requested state are projected; cancellation availability is false after a request. No schema/migration or persisted result cache is added.

## Functional runtime evidence

A disposable Synthetic/InMemory API at loopback 6998 and Vite at 6999 exercised the actual authenticated public UI and backend. Native Browser/fixture/OpenSandbox and external providers were disabled. Setup used canonical registration, invitation, roles, Agent configuration and Session APIs; it added no public debug endpoint and changed no user data.

- The owner started immediate background work through the model, opened the header inbox and inspected the immutable initial task/result. Continue navigated to that exact child Session, made it visible in Chat and enabled the composer. A later human message received a Synthetic reply in that same Session. Activity Run history retained both the initial task and later chat turn; renaming the mutable Session title did not replace the original task/title/result.
- Both Activity routes and exact Session/Run query links loaded through browser navigation/reload. Parent/report conversation links retained the tenant route. The shared Run inspector exposed actual model and original task facts.
- A separate human with exact Agent/child Session read but no Run, chat, management, parent or artifact read could inspect the allowed Session. Initial Run/result and original files remained unavailable. Adding only the exact initial Run read revealed that result while chat stayed disabled and the parent target remained withheld. File metadata remained independently permission-filtered.
- Revoking the exact child Session grant while its inspector was open caused the next current read to clear protected facts/actions, remove the row from the available list and expose explicit exact-detail retry. The intentional denied requests produced expected 403 browser resource errors; normal owner journeys had no console errors/warnings.

Lost network replies were not simulated in the MCP browser. Controlled component cases separately verify committed-but-lost Continue, exact saved-fact read without replay, missing Run access without all-files fallback and lost cancellation requiring an explicit current Run read. Backend integration cases exercise actual active-mailbox continuation, denied human persistence and SQLite host recreation; those are not inferred from screenshots.

## Focused checks

The selected module checks cover 81 distinct cases: 62 frontend, 14 infrastructure and five API cases. Follow-up infrastructure reruns overlap and are not added twice. All selected cases passed. No CI or full CI suite was run.

| Log | Check / result |
| --- | --- |
| `/private/tmp/tenant-activity-ui-review-tests.log` | Six frontend files, 62 passed: canonical Activity source/recovery, shared Background Work and Activity, tenant route and scoped read state. |
| `/private/tmp/tenant-activity-ui-store.log` | `Tenant_activity_` and `Tenant_foreground_`, 14 passed across InMemory/SQLite. |
| `/private/tmp/tenant-activity-ui-scopes-final.log` | Final modified scoped Activity test, two passed; exact Agent Run/background pages and already-visible chat hints. |
| `/private/tmp/tenant-activity-ui-api.log` | Five passed: active mailbox continuation, original result/visibility after actual SQLite host recreation and refused human persistence. |
| `/private/tmp/tenant-activity-ui-review-build.log` | TypeScript/Vite production build passed; existing large-chunk advisory remains. |

Commands:

```text
web: pnpm run test --run src/services/tenantActivity.test.ts src/features/chat/TenantBackgroundRecovery.test.tsx src/features/chat/BackgroundWorkDrawer.test.tsx src/features/admin/InstanceActivitySection.test.tsx src/features/access/accessRoute.test.ts src/features/chat/workReadState.test.tsx
Infrastructure: dotnet test tests/AgentCore.Infrastructure.Tests --no-restore --filter 'FullyQualifiedName~Tenant_activity_|FullyQualifiedName~Tenant_foreground_' --nologo
Infrastructure final: same project, filter 'FullyQualifiedName~Tenant_activity_filters_scoped_reads_before_count'
API: dotnet test tests/AgentCore.Api.Tests --no-restore --filter 'FullyQualifiedName~Tenant_active_background_continues_through_its_mailbox|FullyQualifiedName~Tenant_foreground_visibility_and_original_Activity_result_survive_actual_SQLite_host_recreation|FullyQualifiedName~Tenant_rejected_foreground_persistence_' --nologo
web: pnpm run build
```

The first shared frontend pass found five legacy selection/commit-ack regressions. One correction batch restored cached legacy selection and durable visibility acknowledgement while keeping tenant current-read fences; the final 62-case selection supersedes that failed pass. No regression was dismissed or relabeled as passing.

## Visual and documentation qualification

The code-led extension retains the incumbent dark Ant Design layout, shared collection density, 640px maximum/full-mobile inspectors and 16px body inset. Captures under `.impeccable/review/tenant-background-activity/` cover original results at 1440/1024/768/390/320, Sessions at 1440/768/320, Runs/Run detail/Activity detail at 1440/320, Background list at 1440/320 and narrow Chat header/three independent permission states. Captures show real settled Synthetic content and are visual evidence, not proof of authorization.

The changed-owner detector ran once and returned an empty finding list. A fresh `impeccable_finish_reviewer` requested four material fixes: narrow recovery Alert layout, stable table geometry, secondary contrast and permission-filtered file empty copy. One shared correction batch stacked recovery actions below messages at narrow inspector widths, fixed table columns with 800/1000px local scrolling, used the incumbent stronger secondary text token and made tenant empty copy access-aware. The same 62 frontend cases and production build passed afterward.

The first post-fix drawer captures caught animation/resize transitions; the reviewer returned recapture without scoring them. Those captures were replaced after finite animations finished and final transform/width/right alignment were verified (640px wide; full viewport below 768px). The next full review inspected all 20 required captures, returned **ship** and scored all four findings resolved. No second detector, additional polish hunt, new asset or design identity was introduced. The required fresh `impeccable_documenter` compared the incumbent DESIGN/sidecar, product/direction, seven named source owners, this report and eight representative current captures. It found the existing palette, typography, spacing and shared compositions applicable; no design-file rewrite or new guidance was needed. Historical sidecar fingerprints were preserved without unrequested repair. Both handoffs are complete.

Relative Markdown links and `git diff --check` passed. Only the two owned disposable Synthetic API/Vite processes were stopped after qualification; no Real host or user database was reset.
