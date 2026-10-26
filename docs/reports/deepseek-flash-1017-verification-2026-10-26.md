# DeepSeek Flash 1017 snapshot update — 2026-10-26

The current OpenRouter preset and executable fixtures use `deepseek/deepseek-v4.1-flash-1017`, matching the [OpenRouter listing](https://openrouter.ai/deepseek/deepseek-v4.1-flash-1017). The tracked migration is `20261026090000_DeepSeekFlash1017`. [Configuration](../15-persistence-and-configuration.md#real-model-catalog) owns its installed-data contract and guards.

The source update includes the preset, EF seed snapshot/target model, model-selection/provider/attachment fixtures, design reference, current documentation and private local `.env` model references. Earlier migration metadata and historical provider reports retain their actual original model IDs. No credentials were printed or changed.

## Observed scenarios

Three SQLite integration cases passed:

- Create an old-route preset, owned connection, default offering, custom retired offering and conversation; admit a Run. Upgrade refuses unfinished work without changing the predecessor or preset. Cancel through the normal Run command, then upgrade successfully. Stable IDs/defaults, custom labels, retirement, connection, capabilities and reasoning remain; offering/preset/security revisions advance. Original Run payload bytes and Session history/pin remain unchanged.
- Resolve the migrated default and exercise the production provider factory with an offline HTTP fixture. The old captured route fails before HTTP; the current route sends the exact snapshot ID and produces the expected structured reply. The next actual admission in the retained conversation captures the new route while its earlier Run keeps the old pin. Reopen/reapply is idempotent, EF reports no pending model changes, foreign-key checks return zero violations, normal identity edits remain forbidden and populated downgrade refuses.
- Independently register an already-existing target offering or target preset. Each collision refuses atomically, preserving offering identities, preset and tenant revision.

The focused infrastructure regressions passed **114 tests**. These include actual tenant Runtime tool execution, checkpoint and terminal reply across native provider fixtures, OpenRouter request routing/reasoning, authorization, onboarding and predecessor Chat retention migrations. The focused application regressions passed **16 tests**, including model binding, capability validation and delivery of a PNG attachment into a vision-enabled offline request.

## Commands and results

Both affected test projects built with zero warnings/errors:

```sh
dotnet build tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-restore --disable-build-servers --nologo -m:1 -p:UseSharedCompilation=false -v minimal
dotnet build tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-restore --disable-build-servers --nologo -m:1 -p:UseSharedCompilation=false -v minimal
```

Final focused runs: **133 passed, zero failed/skipped** across these commands:

```sh
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter FullyQualifiedName~DeepSeekFlash1017 --logger 'console;verbosity=normal'
dotnet test tests/AgentCore.Infrastructure.Tests/AgentCore.Infrastructure.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~OpenAICompatibleLanguageModelTests|FullyQualifiedName~AutomationAuthorizationProviderTests|FullyQualifiedName~DynamicModels_guarded_factory|FullyQualifiedName~DynamicModels_actual_Runtime_executes|FullyQualifiedName~ChatRetentionMigrationTests|FullyQualifiedName~DynamicModels_onboarding' --logger 'console;verbosity=minimal'
dotnet test tests/AgentCore.Application.Tests/AgentCore.Application.Tests.csproj --no-build --no-restore --nologo --filter 'FullyQualifiedName~SessionModelBinderTests|FullyQualifiedName~UserTurnCapabilityValidatorTests|FullyQualifiedName~PngVisionRuntimeTests' --logger 'console;verbosity=minimal'
git diff --check
```

The first test attempt was blocked by the sandbox's local test-runner socket restriction; permitted execution resolved it. Static checks also confirmed valid design JSON, matching migration target/snapshot model bodies, no broken relative targets in changed documentation and no stale model references in active source/configuration outside migration source identifiers and historical evidence.

## Limits

No installed database was migrated, application restarted or provider inference called. Stop and back up the installed host, then apply the offline EF migration before restarting; normal startup refuses pending installed migrations. Live snapshot service behavior, physical devices, frontend browser journeys and optional CI were not exercised. The frontend edit changes an existing test fixture ID only. Capability declarations remain conservative; this update does not certify vision or other vendor capabilities. No milestone acceptance is claimed.
