# Model management and Space integration on main

The owner requested commit → pull main → resolve both features together → commit/push main. The model implementation was first committed as `345550a9`. Remote main was `9ab12d44` (PR #19), containing the Space/Membership/Environment/Secrets implementation and its qualification successor. This report qualifies their merged local result; the two earlier delivery reports remain historical at their recorded source boundaries.

## Resolution and ownership

Forty-five conflicted paths were reviewed alongside automatically merged consumers. Model routes remain tenant owned; Space selection does not introduce environment-specific inference routing. Existing Session Runtime, Run admission/claim, approval, effect, checkpoint and recovery owners retain authority.

- Background/Automation/approval paths preserve both exact model bindings and Space/Environment witnesses. Context successors resolve the current tenant catalog asynchronously, carry the concrete binding, and retain original causal/effect ownership. JSON/tool/API/UI contracts use canonical offering IDs together with independent context pins. Speech aliases remain independent.
- IAM recognizes model connections/offerings plus Spaces/Secrets without substituting one scope for another. Model configuration grants no Space execution right; membership is also insufficient without current IAM. Runtime/live/offline fixtures now explicitly grant only `spaces.read` where they execute in a Space. No production permission was relaxed.
- Incoming assistant v33 retained the removed `ProviderPreferences.languageModel` field; startup reproduced the strict-contract refusal. Removing that obsolete inference alias preserves its new execution-policy content and speech preferences.
- Navigation, route parsing and Automation editing retain AI models, system presets, Spaces, Secrets, context fields and current unpublished draft behavior. The retired Instance workspace route remains retired. No second UI kit or execution path was introduced.
- Three modify/delete conflicts preserve removal of paid-only global-key probes. Offline browser/recovery cases and the canonical opt-in tenant-credential harness remain; the latter now supplies its explicit Space read fixture grant. No live provider was invoked.

A proposed broad heuristic conflict script was rejected by automatic approval review. It was not executed. Resolution proceeded through explicit reviewed patches and focused checks; no approval blocker remains.

## Coherent migrations

Fresh SQLite execution reproduced incompatible ordering: a landed Space rebuild expected legacy model column names after the unpublished model migration had renamed them. The unpublished model migrations now follow the landed Space chain:

1. `20261025080000_SpaceResourceApprovals` and its predecessors retain their landed operations.
2. `20261025085000_TenantDynamicModels` adds canonical model aggregates/binding columns and refuses incompatible retained references before mutation.
3. `20261025090000_CanonicalModelIdentity` renames the physical model columns and rebuilds the complete Space approval contract.

The rebuild retains all 23 approval columns, 13 indexes and 179 Space predecessor guards, alongside seven model guards for 186 total. Only the four model identity guard references change; other bodies remain exact. Frozen resources carry the combined constraint and complete rebuild; foreign-key checks remain authoritative. The latest target model and snapshot contain both features.

The combined assembly exposed the existing compiler user-string headroom gate. Three Space target-model literals now read the already existing content-addressed resource `c3d774f8790cf8b52cbdd012556731a6df9713964ea08f15cb2bd3370b946d2f`, preserving its original 774,199 SQL bytes. Independent target-model digest cases verify all three migrations. This changes storage of the literal, not migration SQL or behavior; the 14 MiB string-heap budget passes.

Installed incompatible state is refused. No user database, identity, key ring, files or history was reset. Previously generated model-only local migration IDs were unpublished; the current combined sequence is authoritative. Retained-data conversion and production rollout remain separate explicit work under the existing reset/cutover procedures.

## Focused local evidence

Counts qualify their named selection, not a sum of unique tests or a full CI run.

| Check | Result and observed behavior |
| --- | --- |
| Canonical SQLite startup/schema/reopen | 1 passed, 11s. Physical tables, columns, foreign keys, all schema definitions and repeated startup match both-feature model. `/private/tmp/agent-core-main-schema-tests.log`. |
| Migration/frozen metadata/Environment-Secret group | 58 passed; one predecessor-fixture ORM mismatch failed and was corrected/rechecked separately below. Exact SQL digests, string-heap budget, guards, unsafe-reference refusal, empty upgrade, canonical downgrade fences, Space source roundtrip and typed variable/Secret CAS/encryption/reopen execute. `/private/tmp/agent-core-main-migration-regression.log`. |
| Space Automation pin roundtrip recheck | 1 passed, 17s. The fixture now inserts frozen predecessor physical names, roundtrips the exact Space boundary, upgrades to canonical model schema, and reads the retained unpinned Automation through the current store. `/private/tmp/agent-core-main-pin-roundtrip-tests.log`. |
| Actual model/Space API and Runtime | 14 passed, 2m01s. Owned model credential→connection→offering/default, actual completed turn in Shared and Private Space, exact model/connection/effort/tenant pins, context successor at most once, refusal and SQLite restart boundaries. `/private/tmp/agent-core-main-context-api-tests.log`. |
| Native/compatible Runtime and route authority | 17 passed, 1m04s. All five kinds execute tool→durable checkpoint→validated reply through the existing Runtime; SQLite admission/reopen, explicit revocation and readable reference filtering also pass with independent Space authority. Live opt-out explicit. `/private/tmp/agent-core-main-native-runtime-tests.log`. |
| Model policy and background report contracts | 27 passed, 145ms; Application test project compiled. `/private/tmp/agent-core-main-policy-tests.log`. |
| Frontend routing/Automation/context/model administration | 49 passed across four files, 192.90s. Existing React act/measurement warnings remain. `/private/tmp/agent-core-main-web-tests.log`. |
| Types/production web build | Passed; Vite 6.47s. Incumbent large-chunk/annotation advisories remain. `/private/tmp/agent-core-main-web-build.log`. |
| Browser integrated journey | **2 passed, 55.9s.** Protected key→metadata fixture→offering/default, stale readback/confirmation, two actual Chat tool/reply turns across a Space switch, exact model/Space reload, two retained Run references and cancelled default replacement. Shared-preset/read-only isolation remains a separate scenario. `/private/tmp/agent-core-main-browser-tests.log`. |

Playwright MCP also opened the actual Synthetic fixture, selected **Model integration space · Shared** through the native context Drawer, applied it to the next turn and sent a message. Independent SQLite readback showed two completed Runs, different Space pins and the same offering/connection, with no failure. The CLI fixture later stopped its own servers; accumulated MCP network errors from that closure are not represented as a clean console. The final repeatable browser run owns current flow/error assertions.

No provider key was inspected, provider HTTP called, hosted CI dispatched/rerun/waited for, or user database reset. Physical devices, live model entitlement and matching live Sandbox deployment remain outside these local checks. No application milestone acceptance or deployment is claimed. Initial merge/build/fixture failures above were resolved before the final commit; original feature ledgers retain their earlier results.

Final browser assertions reported no page errors or unexpected Hub refusals. Vite emitted a WebSocket ECONNRESET during reload/closure; both completed conversations, reference counts and reload checks passed. No new visual design or broad UI recertification is implied by the functional merge review.

Final hygiene: all textual conflicts are resolved. Both staged and unstaged whitespace checks pass. The current-document scan resolves all 1,890 local links across eighteen documents; no scratch files, databases, logs, reject/original files or browser tokens are staged. Remote main remained `9ab12d44` at the final fetch.
