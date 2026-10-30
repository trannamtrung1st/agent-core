# Documentation map

Start here for current contracts. The [root README](../README.md) introduces the product; [TODO](../TODO.md) owns current priorities and the [implementation plan](18-implementation-plan.md) owns milestone gates.

## Current baseline

Qualified executable/test source: `bc8124f7b8a0486dd38fb8364789a0891992df1f`, 2026-10-30; closing documentation is a descendant with no executable changes. The implementation includes tenant IAM, Spaces/Environments, dynamic models, self-management, Saved/Temporary Chat, Decision Intelligence S1/S2, Evaluation, Goals, bounded Task communication, General Assistant v36 and Formal Workflows. Five production .NET assemblies and one React/Vite app remain the ownership boundaries. Evidence is scoped to its recorded source; sustained production load, live providers, physical devices and customer deployment require their own proof. [Refactoring delivery](reports/project-refactoring-delivery-2026-10-30.md) preserves the preceding automated baseline. [Production Hardening acceptance](reports/production-hardening-delivery-2026-10-30.md#exact-source-acceptance-and-order-5-closure) records all 62 required hosted jobs, exact case accounting, committed source/image provenance and packaged recovery for the controlled Synthetic single-host profile. Order 6 — Platform-wide UI/UX Polish is next.

| Question | Current authoritative owner |
| --- | --- |
| Product purpose / scope | [Vision](01-product-vision.md), [MVP scope](02-mvp-scope.md) |
| Runtime, identity, tenant and resource ownership | [Architecture](03-system-architecture.md) |
| Ports, policies and application service contracts | [Backend interfaces](04-backend-interfaces.md), [backend implementation](12-backend-implementation-spec.md) |
| Turn taking, voice and normalized events | [Controller](05-interaction-controller.md), [voice](06-realtime-voice.md), [events](07-event-model.md) |
| Technologies and source ownership | [Decisions](10-technology-decisions.md), [repository structure](11-repository-structure.md) |
| Screens and interaction | [Frontend specification](13-frontend-implementation-spec.md); [Ant Design presentation guidance](../.agents/context/DESIGN.md) |
| HTTP, SignalR and MessagePack | [Protocol](14-api-and-realtime-protocol.md) |
| SQLite schema, migrations, options and protected roots | [Persistence/configuration](15-persistence-and-configuration.md), [host input reference](host-configuration-reference.md) |
| Automated evidence and test selection | [Testing strategy](16-testing-strategy.md) |
| Startup, shutdown, deployment and recovery | [Operations](17-observability-and-operations.md) |
| Work order / completed gates | [Implementation plan](18-implementation-plan.md), [TODO](../TODO.md) |
| Shipped immutable definitions / developer tooling | [agents](../agents/README.md), [AGENTS](../AGENTS.md), [scripts](../scripts/README.md) |
| Dated acceptance evidence | [Report index and retention policy](reports/README.md) |

Proposals document approved or proposed changes; consult their status and delivery evidence before treating them as implemented. Canonical owners above win over historical reports and tooling context. The [refactoring decision record](proposals/project-refactoring-architecture-stabilization.md) records accepted moves and explicit no-go choices.
