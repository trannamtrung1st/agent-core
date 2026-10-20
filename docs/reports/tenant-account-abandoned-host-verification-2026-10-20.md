# Tenant abandoned account dispatch host verification — 2026-10-20

Foreground account-action recovery now has actual composed SQLite application-host qualification across durable reservation, native dispatch and canonical settlement boundaries. Existing production recovery behavior required no change. The tests add controlled fault boundaries to the existing host fixture, execute the real Synthetic native owners, dispose the first application host, then construct a new host against the same database, keys and files with the old claim expired.

The original canonical dispatch settles as Indeterminate when its settlement was lost. This does not erase a native Email send/Drive success or uncertainty already durably recorded. Those facts stay separate. Recovery never calls the native operation again. A subsequently approved Drive read reports the original canonical outcome separately from retained/native metadata verification and never rewrites the original receipt or grant.

## Executed focused evidence

**24 distinct focused cases passed.** Initial and expanded runs of the same cases are not added twice.

| Group | Cases | Expected and observed outcome |
| --- | ---: | --- |
| Actual SQLite account dispatch host recreation | 14 | Email send, Drive folder creation, original-byte upload and ordered trash batch at three windows: reserved before native entry; native acknowledgement before lost canonical settlement; and accepted native effect with lost acknowledgement before canonical settlement. Two additional Email/upload cases revoke authority after dispatch. The first host executes actual reservation/native owners; only canonical settlement is faulted. The new host records Indeterminate, reaches a terminal Run, and repeating recovery or exact operation execution causes no new native write. Native Email claims/results and Drive operation rows remain identical across recovery. No private draft body enters the terminal result. |
| Post-recovery read-only Drive inspection | Included above | Nine of those host cases start a new human Session and separately approve exact operation inspection. Reserved work returns missing evidence, acknowledged work returns retained verification, and uncertain accepted work can verify through current metadata. Partial trash batches leave their second item unattempted. Canonical outcomes stay Indeterminate and native write counters do not change. |
| Abandoned exceptional grants | 2 | Folder acknowledgement and uncertain original-byte upload execute with missing raw `drive.write` under exact primary-only exceptional grants. Grants are Claimed before the lost settlement, then Indeterminate after recovery. Separate subsequent read consent succeeds without old write authority; the original grant remains Indeterminate. Exactly one original result audit is retained, and no write is repeated. |
| Existing exact-read cold-host regression | 6 | Four family-discovery pending-Review and two prior-operation pending-read cases retain exact request/source facts across actual host recreation. Current source authority still governs approval versus cancellation. The fixture's optional fault hook leaves ordinary hosts unchanged. |
| Existing both-store recovery regression | 2 | Memory and SQLite abandoned claimed exceptional effects settle once as Indeterminate after lease expiry, retain exact receipt/grant fences and perform no native call on repeat recovery/execution. |

The final solution build passed with zero warnings/errors. Changed Markdown links/fences and whitespace checks passed. No CI or full local CI suite was requested or run.

## Mechanism and evidence limits

`CrashAccountStore` forwards the real start/maintenance owners and injects failure before canonical settlement. Its reserved window fails immediately after the real start transaction. Operation-local Synthetic Email/Drive decorators execute the actual native effect and optionally lose its acknowledgement. A shared counter detects every attempted native write across both hosts. No public debug endpoint, production fault option, recovery engine or persistence table is added.

A controlled clock expires the original claim before constructing the second host. Tests use the actual registered `AgentRunHostedService` pass to drive subsequent requests without advancing execution budgets solely for cadence. This is actual application-host disposal/recreation with durable fault injection; it is not a physical process-kill or real Google/SMTP/network test. Upload byte/status-probe behavior under controlled real Google HTTP remains separately qualified in [operation reconciliation evidence](tenant-account-operation-read-verification-2026-10-20.md). No new frontend source or dedicated crash-recovery browser journey is claimed.

Logs: `/private/tmp/tenant-account-abandoned-host-tests.log` (initial 14 native recovery cases), `/private/tmp/tenant-account-abandoned-reconciliation-tests.log` (expanded same 14 with separate Drive reads), `/private/tmp/tenant-account-abandoned-grant-host-regressions.log` (two grants plus six host regressions), `/private/tmp/tenant-account-abandoned-store-regressions.log` (two stores), and `/private/tmp/tenant-account-abandoned-final-build.log`.

## Remaining proposal scope

This supersedes abandoned foreground account-dispatch cold-host-pending notes for the exercised native owners/windows only. It does not establish every background/autonomous crash variant or real-provider recovery. Autonomous occurrence/scheduler/webhook/email-watcher ownership, remaining interaction/voice/System/audit families, dedicated browser paths where still required, final obsolete-authority/schema consolidation and the exhaustive proposal acceptance audit remain open. The full original goal stays active.
