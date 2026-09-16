# Post-MVP handoff

Recorded 2026-09-16 on branch `develop`. Synthetic/offline default. This report does not embed its own git commit hash.

Canonical product and architecture live in [README](../../README.md) and [docs/01–18](../01-product-vision.md). The original four proposal files are not required to understand the delivered system. Historical MVP Milestones 0–12 remain **Complete** in [Implementation Plan](../18-implementation-plan.md). MVP packaging: [m12-mvp-handoff](m12-mvp-handoff.md). Proposal retirement: [proposal-retirement](proposal-retirement.md).

## Requirement matrix (R1–R6)

| ID | Required resolution | Canonical owners | Status |
| --- | --- | --- | --- |
| R1 | Trusted-local owner capability; SessionId not a credential; fail-closed | `docs/10`, `docs/14`, `docs/02`, `docs/17`, `docs/13` | Observed |
| R2 | Independent speech/display/block receipts; conservative heard | `docs/10`, `docs/05`, `docs/06`, `docs/15`, `docs/14` | Observed |
| R3 | Bind and catalog mutations in revision order; cleanup races | `docs/10`, `docs/15`, `docs/14` | Observed |
| R4 | v1 DELETE terminal-end; additive durable delete/deactivation/archive | `docs/10`, `docs/14`, `docs/15`, `docs/18` | Observed |
| R5 | Finite silent bounds; at-cap RequestDeactivate | `docs/10`, `docs/05`, `docs/18` | Observed |
| R6 | A–H mandatory including rename/archive and concrete sandbox; Phase I conditional | `docs/10` R6, `docs/18` phase table, `docs/08` | A–H observed; I **not-applicable** with future trigger |

## Phases A–I

| Phase | Status |
| --- | --- |
| A Sessions | Observed |
| B Attachments | Observed |
| C Rich responses | Observed |
| D Initiative / deactivation | Observed |
| E Role environments | Observed |
| F Workspace / artifacts | Observed |
| G Bounded tools | Observed |
| H Docker `sandbox.run` | Observed |
| I WorkItems | **Not-applicable.** In-flight G/H work is epoch-guarded; attachments/workspace/artifacts already survive deactivation via D/F. **Future trigger:** a later accepted Support, Compliance, or `sandbox.run` workflow that must continue after `RequestDeactivate` without repeating the user turn. |

## Commands and results (item-2dcab2b2a46c)

| Command | Result |
| --- | --- |
| `dotnet test AgentCore.sln` | Domain 12; Application 151; Infrastructure 67 passed / 8 skipped in that testhost (3 live + 5 sandbox PATH skip); Api 58 |
| `dotnet test tests/AgentCore.Infrastructure.Tests --filter FullyQualifiedName~DockerSandbox` | 6 passed / 0 skipped (`busybox:1.36`) |
| `pnpm run test --run` (web) | 103 passed |
| `pnpm exec tsc --noEmit && pnpm run build` | passed |
| `CI=1 pnpm exec playwright test` | 8 passed |
| Isolated `docker compose -p agent-core-tdp-gates-2dca up --build` | Synthetic health; session survived recreate; SPA 200; missing API 404; `down -v` only on that project; operator volume `agent-core_agent-core-data` retained |

Hosted OpenAI/OpenRouter smokes and headset checks were not opted in.

## Migrations

Apply at API startup before traffic. EF Core SQLite migrations under `src/AgentCore.Infrastructure/Persistence/Migrations/`:

| Migration | Concern |
| --- | --- |
| `20260915064647_InitialCreate` | Session, history, snapshot, profile |
| `20260916104118_SessionCatalogAndOwnerCapability` | Catalog fields, owner-capability grant, durable-delete tombstone |
| `20260916120000_Attachments` | Attachment metadata |
| `20260916180000_ResponseEnvelope` | EnvelopeJson / receipts |
| `20260916210000_Artifacts` | Artifact metadata |

Blobs stay outside SQLite: `data/attachments`, `data/workspaces/{sessionId}`, `data/artifacts/{sessionId}` — never under `local/tdp-workspace`.

## Operating notes

- Fast loop: native `dotnet run` + Vite (`docs/17`).
- Demo: `docker compose up --build` (Synthetic, key-free). Hosted overlay: `docker compose -f docker-compose.yml -f docker-compose.real.yml up`. Do not `down -v` the default project if operator SQLite must be kept.
- Backup SQLite with `SqliteMemoryStore.BackupToAsync` or a stopped-app copy of the DB plus WAL companions.
- Single process owns SessionManager; do not replica-scale against one SQLite file.
- Trusted-local capability: `POST /api/v1/local/owner-capability` on loopback; header `X-AgentCore-Owner-Capability`.

## Permitted omissions

- Hosted AI smokes and manual headset/speaker certification
- Durable WorkItems (Phase I N/A)
- OCR / Office / audio-video attachment processors
- Native speech-to-speech, WebRTC, vector memory, microservices, MediatR forwarding, Kafka/Redis
- Public multi-user hosting, OAuth, tenancy
- Extra sandbox backends; `process`/`shell` tools
- Pushing or publishing this repository

## Item-to-checkpoint map (production commits on `develop`)

Hashes below are earlier checkpoints, not this handoff commit.

| Concern | Commit |
| --- | --- |
| Phases A–F | `9b30d9f` |
| Phase G tools | `5de49cd` |
| Phase H sandbox | `26229c0` |
| Phase I N/A | `5a4d789` |
| Remaining integration/gates/docs | this documentation commit (hash recorded in TDP evidence after the commit lands) |
