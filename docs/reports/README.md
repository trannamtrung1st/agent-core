# Delivery evidence

Reports are dated, source-bound evidence. They preserve the checks performed, outcomes and limitations at that time; they are not current operational or API contracts. Start with the [documentation map](../README.md) for current owners.

| Evidence family | Main ledger |
| --- | --- |
| Original MVP / historical phases | [Implementation plan](../18-implementation-plan.md), [MVP handoff](m12-mvp-handoff.md) |
| Tenant IAM / hosting stabilization | [Final IAM delivery](tenant-final-delivery-2026-10-24.md), [stabilization](iam-stabilization-2026-10-24.md) |
| Spaces / models | [Space delivery](space-environment-implementation-2026-10-25.md), [model delivery](dynamic-model-management-delivery-2026-10-25.md) |
| Self-management / Chat | [Self-management](agent-self-management-delivery-2026-10-26.md), [Chat](chat-simplification-2026-10-26.md) |
| Decision Intelligence | [Delivery](decision-intelligence-delivery-2026-10-26.md), [native qualification](kev-native-verification-2026-10-26.md) |
| Evaluation / Goals | [Native scripts](native-evaluation-scripts-2026-10-27.md), [Goals](session-goals-delivery-2026-10-27.md) |
| Task communication | [Initiative 9](initiative-9-local-verification-2026-10-27.md), [review follow-up](initiative-9-review-fixes-2026-10-28.md) |
| Managed General Assistant / Formal Workflows | [General Assistant](general-assistant-delivery-2026-10-28.md), [Workflows](formal-workflows-delivery-2026-10-28.md) |
| Production Hardening | [Controlled single-host design, implementation and exact-source acceptance](production-hardening-delivery-2026-10-30.md) |
| Project refactoring | [Delivery and final review](project-refactoring-delivery-2026-10-30.md), [design decisions](../proposals/project-refactoring-architecture-stabilization.md) |

## Inventory and retention

The refactoring baseline contains 1,148 tracked files under this directory, including 546 under `assets/`. Its ignored inventory records every path, size and SHA-256. Markdown delivery ledgers and attached logs are historical evidence; screenshots/fixtures are supporting evidence. Current contracts remain in their canonical documents outside this tree. There is no confirmed disposable set: retain unreferenced or ambiguous evidence until its source/provenance and all docs/code/test/CI consumers have been audited. No baseline reports or assets are deleted or relocated by this initiative.

New delivery reports should remain concise, with exact source, commands, counts and qualification limits. Keep large transient logs/traces under ignored `local/` or bounded CI artifacts. Preserve stable paths and historical assertions; correct current summaries at their canonical owner. Deleting tracked evidence does not shrink historical Git objects.
