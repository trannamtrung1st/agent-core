# Host configuration reference

Application options use binder-aligned PascalCase segments and `__` in process environment (`:` in JSON). Uppercase names belong to orchestration, platform/tool conventions or documented external integrations. Model connection credentials, tenant Credentials, Space Environments and host environment remain separate owners.

Native precedence is profile defaults → inherited process environment → selected dotenv → launcher-owned derived values. The dotenv parser never evaluates commands or expands variables. Duplicate normalized assignments and retired inputs fail with names-only diagnostics. Native launch translates `GOOGLE_OAUTH_CLIENT_ID`, `GOOGLE_OAUTH_CLIENT_SECRET`, `GOOGLE_OAUTH_PUBLIC_APP_URL` and `AGENTCORE_DRIVE_ENABLED` once, removes those orchestration variables, and exports `GoogleOAuth__ClientId`, `GoogleOAuth__ClientSecret`, `GoogleOAuth__PublicAppUrl`, `GoogleDrive__Enabled`. Setting both nonblank inputs for an option is an error even if they agree. Pure native `dotnet` hosts accept only the binder keys.

Use `python3 scripts/compose.py` in place of `docker compose` for application deployments. It validates selected dotenv files and inherited environment before Docker interpolation, rejecting retired/ambiguous inputs and native equivalents of the four translated options. Compose's inherited environment overrides dotenv. It forwards only the explicit service environment in the Compose files; arbitrary .NET keys stay native-only. Raw Docker Compose does not inspect unused dotenv keys and bypasses the operator preflight. Generated private Sandbox handoff remains authoritative only for its own topology, identity and roots.

| Retired input | Required replacement/action |
| --- | --- |
| `Providers__SpeechRecognizers__primary-stt__*` | `Providers__Speech__Recognition__*` |
| `Providers__SpeechSynthesizers__primary-tts__*` | `Providers__Speech__Synthesis__*` |
| `GoogleEmailOAuth__*`, `GoogleDriveOAuth__*`, `GOOGLE_EMAIL_OAUTH_*`, `GOOGLE_DRIVE_OAUTH_*` | One `GoogleOAuth` client; reconnect unproven saved grants |
| `FilePreview__Limits__CsvBytes` | `FilePreview__Limits__DelimitedBytes` |

There are no runtime alias readers. Absent OAuth configuration leaves integration unconfigured; partial/malformed OAuth origins fail. Speech defaults independently to Synthetic; explicitly selected unknown adapters or hosted adapters without keys remain unavailable, never silently become another adapter. `OPENAI_API_KEY` only supplies a missing key to an explicitly selected hosted speech adapter. Recognition/synthesis option keys can provide their own key. `BRAVE_SEARCH_API_KEY` is the existing search integration convention. SQLite bootstrap is explicit and one-use. InMemory has no durable initialization between processes and requires all five `Security__EphemeralBootstrap__*` values on startup; this active test/demo path is retained, with no default account.

The active startup preset is [`.env.example`](../.env.example). Optional shapes below are defaults/examples, not another active configuration tree. Leave unused settings absent; array indices and `ENTRY_NAME` are templates. Actual safety validation and immutable ceilings remain in their option owners. Secret fields are blank. Launcher-owned values are identified in the preset and Operations.

```dotenv
# General .NET host overrides (HTTP bind/origin policy is owned by dev-real.sh).
# DOTNET_ENVIRONMENT=Development
# ASPNETCORE_URLS=http://127.0.0.1:5080
# ASPNETCORE_Kestrel__Certificates__Default__Path=/absolute/server.pfx
# ASPNETCORE_Kestrel__Certificates__Default__Password=

# Advanced application reference. Uncomment only the values you want to override.
# Array index 0 represents N; use successive indices. ENTRY_NAME is a map key.
# Model inference configuration belongs to tenant-owned AI models records.

# AgentCore
# AgentCore__AgentDirectory: active native preset above.
# AgentCore__DetachGracePeriodSeconds=30
# AgentCore__MaxActiveSessions=10
# AgentCore__MaxEntriesPerSession=1000
# AgentCore__PendingVoiceTimeoutMs=30000
# AgentCore__Profile: active native preset above.

# AllowedHosts
# AllowedHosts='*'

# BraveSearch
# BraveSearch__ApiKey=

# Browser
# Browser__AllowUnmaskedCaptures=false
# Browser__Channel: active native preset above.
# Browser__Enabled=true
# Browser__Environment__Device=
# Browser__Environment__DeviceScaleFactor=
# Browser__Environment__HasTouch=
# Browser__Environment__IsMobile=
# Browser__Environment__Locale=
# Browser__Environment__TimezoneId=
# Browser__Environment__ViewportHeight=
# Browser__Environment__ViewportWidth=
# Browser__FixtureEnabled: active native preset above.
# Browser__FixturePort=5091
# Browser__Headless: active native preset above.
# Browser__InteractionMode: active native preset above.
# Browser__InteractionOrigins__0=http://127.0.0.1:5091
# Browser__Limits__AutomaticSettleMs=1600
# Browser__Limits__CaptureBytes=1500000
# Browser__Limits__CapturesPerScope=4
# Browser__Limits__DownloadBytes=5242880
# Browser__Limits__DownloadsPerScope=2
# Browser__Limits__FindMatches=20
# Browser__Limits__OperationTimeoutMs=25000
# Browser__Limits__SnapshotBytes=8000
# Browser__Limits__TextInputLength=500
# Browser__Limits__WaitTimeoutMs=5000
# Browser__NavigationOrigins__0=http://127.0.0.1:5091
# Browser__PolicyMode: active native preset above.
# Browser__ProfileMode: active native preset above.
# Browser__ProfileRoot: active native preset above.
# Browser__ResourceOrigins__0=
# Browser__ScreenshotPrivacy=Protected
# Browser__TargetOrigins__0=
# Browser__TrustedVisualCaptureOrigins__0=
# Browser__UnmaskedCaptureOrigins__0=

# Email
# Email__AllowedCustomEndpoints__0=
# Email__Enabled: active native preset above.
# Email__SendEnabled=
# Email__SmtpEnabled: active native preset above.
# Email__TimeoutSeconds=20

# EmailMonitoring
# EmailMonitoring__Enabled=true

# FilePreview
# FilePreview__Image__MaxDimension=32768
# FilePreview__Image__MaxPixels=40000000
# FilePreview__Limits__AudioBytes=33554432
# FilePreview__Limits__DelimitedBytes=5242880
# FilePreview__Limits__DocxBytes=10485760
# FilePreview__Limits__ImageBytes=15728640
# FilePreview__Limits__MaxBytes=33554432
# FilePreview__Limits__TextBytes=2097152
# FilePreview__Limits__VideoBytes=33554432
# FilePreview__Limits__XlsxBytes=10485760
# FilePreview__Spreadsheet__ColumnsPerWindow=64
# FilePreview__Spreadsheet__CsvMaxParsedColumns=256
# FilePreview__Spreadsheet__CsvMaxParsedRows=50000
# FilePreview__Spreadsheet__MaxCellCharacters=4096
# FilePreview__Spreadsheet__MaxCellsPerWindow=12800
# FilePreview__Spreadsheet__MaxWindowCharacters=64000
# FilePreview__Spreadsheet__RowsPerWindow=200
# FilePreview__Text__HighlightMaxBytes=262144
# FilePreview__Text__HighlightMaxLines=3000
# FilePreview__Text__MaxLines=10000
# FilePreview__WorkerTimeoutMs=5000

# GoogleDrive
# GoogleDrive__Enabled=true



# GoogleOAuth
# GoogleOAuth__ClientId=
# GoogleOAuth__ClientSecret=
# GoogleOAuth__PublicAppUrl=

# Hosting
# Hosting__AllowedOrigins__0=http://127.0.0.1:5173
# Hosting__BindUrl=http://127.0.0.1:5080
# Hosting__TrustPublishedPortGateway=false
# Hosting__UseViteProxy=true

# Custom compatible model endpoints: exact host-owned local origin allowance.
# Native provider origins are code-owned; model keys are protected tenant records.
# Models__AllowedLocalOrigins__0=http://127.0.0.1:8000

# Interaction
# Interaction__BackchannelMaxMs=700
# Interaction__DegradedInterruptMs=250
# Interaction__PendingVoiceTimeoutMs=30000

# Logging
# Logging__Console__FormatterOptions__IncludeScopes=true
# Logging__Console__FormatterOptions__TimestampFormat='yyyy-MM-ddTHH:mm:ss.fffZ '
# Logging__Console__FormatterOptions__UseUtcTimestamp=true
# Logging__LogLevel__Default=Information
# Logging__LogLevel__Microsoft.AspNetCore=Warning
# Logging__LogLevel__Microsoft.EntityFrameworkCore.Database.Command=Warning

# Observability
# Observability__HttpErrorResponses__Enabled=true
# Observability__LogConversationContent=false
# Observability__LogLevel=Information
# Observability__OtlpEnabled=false
# Observability__OtlpEndpoint=http://127.0.0.1:4317
# Observability__TimelineCapacity=64

# OpenSandbox
# OpenSandbox__AllowLocalImageIds=false
# OpenSandbox__AllowedImagePrefixes__0=docker.io
# OpenSandbox__AllowedImagePrefixes__1=ghcr.io
# OpenSandbox__AllowedImagePrefixes__2=mcr.microsoft.com
# OpenSandbox__AllowedImagePrefixes__3=quay.io
# OpenSandbox__AllowedImagePrefixes__4=public.ecr.aws
# OpenSandbox__AllowedRegistryTokenOrigins__0=
# OpenSandbox__ApiKey=
# OpenSandbox__ApiKeyFile=
# OpenSandbox__Enabled=true
# OpenSandbox__Endpoint=http://127.0.0.1:18080
# OpenSandbox__InsecureRegistryHosts__0=
# OpenSandbox__MaxWorkingStorageBytes=17179869184
# OpenSandbox__RequireHardStorageQuota=false
# OpenSandbox__TrustLocalComposeEndpoint=false
# OpenSandbox__WorkspaceDockerHostRoot=

# Persistence
# Derived from AGENTCORE_DEV_API_DATA_DIR unless explicitly overridden.
# Persistence__ArtifactRoot=data/artifacts
# Derived from AGENTCORE_DEV_API_DATA_DIR unless explicitly overridden.
# Persistence__AttachmentRoot=data/attachments
# Persistence__BusyTimeoutMs=5000
# Persistence__CheckpointMs=1000
# Derived from AGENTCORE_DEV_API_DATA_DIR unless explicitly overridden.
# Persistence__ConnectionString='Data Source=data/agent-core.db'
# Derived from AGENTCORE_DEV_API_DATA_DIR unless explicitly overridden.
# Persistence__CredentialProtectionKeyRoot=data/credential-protection-keys
# Derived from AGENTCORE_DEV_API_DATA_DIR unless explicitly overridden.
# Persistence__DefinitionResourceRoot=data/definition-resources
# Persistence__Provider: active native preset above.
# Persistence__TemplateRoot=agents/templates
# Derived from AGENTCORE_DEV_API_DATA_DIR unless explicitly overridden.
# Persistence__WorkspaceRoot=data/workspaces

# Providers
# Allowed: ChatCompletions, Responses
# Allowed: ChatCompletions, Responses
# Providers__Speech__Recognition__Adapter=Synthetic
# Providers__Speech__Recognition__AdditionalHeaders__ENTRY_NAME=
# Providers__Speech__Recognition__ApiKey=
# Providers__Speech__Recognition__BaseUrl=
# Providers__Speech__Recognition__DefaultModel=gpt-live-transcribe
# Providers__Speech__Recognition__DisabledCapabilities__ENTRY_NAME=false
# Providers__Speech__Recognition__IncludeTranscriptionDelay=false
# Providers__Speech__Recognition__Timeouts__SetupSeconds=10
# Providers__Speech__Recognition__Timeouts__StreamIdleSeconds=60
# Providers__Speech__Recognition__Timeouts__TotalSeconds=120
# Providers__Speech__Synthesis__Adapter=Synthetic
# Providers__Speech__Synthesis__AdditionalHeaders__ENTRY_NAME=
# Providers__Speech__Synthesis__ApiKey=
# Providers__Speech__Synthesis__BaseUrl=https://api.openai.com/v1/
# Providers__Speech__Synthesis__DefaultModel=tts-1
# Providers__Speech__Synthesis__DefaultVoice=alloy
# Providers__Speech__Synthesis__DisabledCapabilities__ENTRY_NAME=false
# Providers__Speech__Synthesis__Timeouts__SetupSeconds=10
# Providers__Speech__Synthesis__Timeouts__StreamIdleSeconds=60
# Providers__Speech__Synthesis__Timeouts__TotalSeconds=120
# Providers__Speech__Synthesis__Voices__ENTRY_NAME=

# PublicWeb
# PublicWeb__DownloadTimeoutSeconds=15
# PublicWeb__MaxDownloadBytes=10485760

# Security
# InMemory startup only; all five required. SQLite uses bootstrap-identity.
# Security__EphemeralBootstrap__Login=
# Security__EphemeralBootstrap__DisplayName=
# Security__EphemeralBootstrap__Password=
# Security__EphemeralBootstrap__TenantName=
# Security__EphemeralBootstrap__TenantSlug=
# Security__Http__AllowInsecureLocalHttp=true
# Security__Http__AllowedOrigins__0=http://localhost:5173
# Security__Http__AllowedOrigins__1=http://127.0.0.1:5173

# Voice
# Voice__MaxUtteranceSeconds=120
```
