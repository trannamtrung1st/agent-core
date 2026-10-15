using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentCore.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CanonicalFilesystemBaseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdminEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ActorKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: true),
                    SummaryJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdminEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionDraftEvaluationResults",
                columns: table => new
                {
                    ResultId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DraftRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConfigurationFingerprint = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScenarioId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScenarioVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RuntimeKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Passed = table.Column<bool>(type: "INTEGER", nullable: false),
                    FindingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    RecordedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDraftEvaluationResults", x => x.ResultId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionDraftEvaluationScenarios",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ScenarioId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ScenarioVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    RequirementLevel = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckType = table.Column<int>(type: "INTEGER", nullable: false),
                    ToolName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDraftEvaluationScenarios", x => new { x.DraftId, x.ScenarioId });
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionDraftResources",
                columns: table => new
                {
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDraftResources", x => new { x.DraftId, x.ResourceId });
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionDrafts",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CandidateJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionDrafts", x => x.DraftId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionPublicationResources",
                columns: table => new
                {
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    MediaType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContentSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ByteLength = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionPublicationResources", x => new { x.DefinitionId, x.Version, x.ResourceId });
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionPublications",
                columns: table => new
                {
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    SourceDraftRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MetadataRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    PublishedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionPublications", x => new { x.DefinitionId, x.Version });
                });

            migrationBuilder.CreateTable(
                name: "AgentInstances",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DefinitionId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ActiveVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonaJson = table.Column<string>(type: "TEXT", nullable: false),
                    Lifecycle = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    PersonaRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    UnattendedModelCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UnattendedReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SettingsOverridesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionBudgetsJson = table.Column<string>(type: "TEXT", nullable: true),
                    HarnessManagementJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInstances", x => x.InstanceId);
                });

            migrationBuilder.CreateTable(
                name: "Artifacts",
                columns: table => new
                {
                    ArtifactId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BlobKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256Hex = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceAttachmentId = table.Column<string>(type: "TEXT", nullable: true),
                    AgentRunId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    WorkspaceLogicalPath = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Artifacts", x => x.ArtifactId);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    AttachmentId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    BlobKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", nullable: false),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256Hex = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Readable = table.Column<bool>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: true),
                    StageForNextTurn = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    BoundAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.AttachmentId);
                });

            migrationBuilder.CreateTable(
                name: "Automations",
                columns: table => new
                {
                    AutomationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    TriggerMode = table.Column<int>(type: "INTEGER", nullable: false),
                    PresetId = table.Column<string>(type: "TEXT", nullable: true),
                    PresetVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    ExecutionTargetKind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    ReportToSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Instructions = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    NextOccurrenceAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    AuthorizationOrigin = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    SuspensionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ModelOverrideCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModelOverrideReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequiresVision = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiresTools = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Automations", x => x.AutomationId);
                    table.CheckConstraint("CK_Automations_Destination", "(ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)");
                    table.CheckConstraint("CK_Automations_Mode", "TriggerMode IN (0,1)");
                    table.CheckConstraint("CK_Automations_Preset", "(PresetId IS NULL AND PresetVersion IS NULL) OR (PresetId IS NOT NULL AND PresetVersion >= 1)");
                });

            migrationBuilder.CreateTable(
                name: "BrowserPrivacy",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    PolicyJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrowserPrivacy", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CoreEventBuckets",
                columns: table => new
                {
                    BucketId = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerId = table.Column<string>(type: "TEXT", nullable: false),
                    CoverageJson = table.Column<string>(type: "TEXT", nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", nullable: false),
                    TriggerRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    DueAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Flushed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreEventBuckets", x => x.BucketId);
                });

            migrationBuilder.CreateTable(
                name: "CoreEventDeliveries",
                columns: table => new
                {
                    TriggerId = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: true),
                    Code = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreEventDeliveries", x => new { x.EventId, x.TriggerId });
                });

            migrationBuilder.CreateTable(
                name: "CoreEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Snapshotted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CoreEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "Credentials",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    ContainerRegistryJson = table.Column<string>(type: "TEXT", nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "General"),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: false),
                    AllowedOriginsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectedPayload = table.Column<string>(type: "TEXT", nullable: false),
                    ProtectionVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SecretRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Credentials", x => x.CredentialId);
                });

            migrationBuilder.CreateTable(
                name: "EmailAccountSelections",
                columns: table => new
                {
                    RequestId = table.Column<string>(type: "TEXT", nullable: false),
                    SelectionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAccountSelections", x => x.RequestId);
                });

            migrationBuilder.CreateTable(
                name: "EmailDrafts",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    DraftJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDrafts", x => x.DraftId);
                });

            migrationBuilder.CreateTable(
                name: "ExperienceSettings",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperienceSettings", x => x.AgentInstanceId);
                });

            migrationBuilder.CreateTable(
                name: "Experiences",
                columns: table => new
                {
                    ExperienceId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceId = table.Column<string>(type: "TEXT", nullable: false),
                    ThroughCursor = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiences", x => x.ExperienceId);
                });

            migrationBuilder.CreateTable(
                name: "ExternalEventDeliveries",
                columns: table => new
                {
                    TriggerId = table.Column<string>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    DecisionJson = table.Column<string>(type: "TEXT", nullable: true),
                    AutomationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEventDeliveries", x => new { x.EventId, x.TriggerId });
                });

            migrationBuilder.CreateTable(
                name: "ExternalEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OccurredAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AdmittedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalEvents", x => x.EventId);
                });

            migrationBuilder.CreateTable(
                name: "IdentityMaintenanceSettings",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    AllowAgentConsolidation = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IdentityMaintenanceSettings", x => x.AgentInstanceId);
                });

            migrationBuilder.CreateTable(
                name: "MessageAttachments",
                columns: table => new
                {
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    AttachmentId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageAttachments", x => new { x.EntryId, x.AttachmentId });
                });

            migrationBuilder.CreateTable(
                name: "OwnerCapabilities",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerCapabilities", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "SandboxExecutions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentRunId = table.Column<string>(type: "TEXT", nullable: false),
                    ToolCallId = table.Column<string>(type: "TEXT", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxExecutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SandboxInstanceVerifications",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    InstanceRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxInstanceVerifications", x => new { x.InstanceId, x.InstanceRevision });
                });

            migrationBuilder.CreateTable(
                name: "SandboxProfileVerifications",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxProfileVerifications", x => new { x.ProfileId, x.ProfileRevision });
                });

            migrationBuilder.CreateTable(
                name: "SandboxProfiles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxProfiles", x => new { x.Id, x.Revision });
                });

            migrationBuilder.CreateTable(
                name: "SandboxSessionLeases",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    Json = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SandboxSessionLeases", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    AgentId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    DefinitionJson = table.Column<string>(type: "TEXT", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", nullable: false),
                    PendingMode = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    PauseReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RuntimeEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    WorkspaceOwned = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    ArchivedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    DurablyDeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    PinnedPersonaJson = table.Column<string>(type: "TEXT", nullable: true),
                    PinnedPersonaRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    PendingAgentInputIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginJson = table.Column<string>(type: "TEXT", nullable: false, defaultValue: "{\"kind\":0}"),
                    Surfaces = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "StructuredMemories",
                columns: table => new
                {
                    MemoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    SubjectKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceEntryIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SupersedesMemoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ProvenanceRecordedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    OwnerInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    OwnerProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    OriginMemoryId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    OriginSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    DerivedFromMemoryIdsJson = table.Column<string>(type: "TEXT", nullable: false),
                    MaintenanceOrigin = table.Column<string>(type: "TEXT", nullable: true),
                    MaintenanceAgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    MaintenanceSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    MaintenanceAgentRunId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StructuredMemories", x => x.MemoryId);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    PreferencesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.ProfileId);
                });

            migrationBuilder.CreateTable(
                name: "WebhookEvents",
                columns: table => new
                {
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    EventKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CredentialHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebhookEvents", x => x.ResourceId);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionResourceStates",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    EnabledOverride = table.Column<bool>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionResourceStates", x => new { x.InstanceId, x.ResourceId });
                    table.ForeignKey(
                        name: "FK_AgentDefinitionResourceStates_AgentInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionSkillStates",
                columns: table => new
                {
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DefinitionSkillId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    EnabledOverride = table.Column<bool>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionSkillStates", x => new { x.AgentInstanceId, x.DefinitionSkillId });
                    table.ForeignKey(
                        name: "FK_AgentDefinitionSkillStates_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentInstanceResources",
                columns: table => new
                {
                    InstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    LogicalPath = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInstanceResources", x => new { x.InstanceId, x.ResourceId });
                    table.ForeignKey(
                        name: "FK_AgentInstanceResources_AgentInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentInstanceSkills",
                columns: table => new
                {
                    SkillId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Procedure = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Projection = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    RequiredCapabilitiesJson = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceDefinitionId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SourceDefinitionVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceDefinitionSkillId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInstanceSkills", x => new { x.AgentInstanceId, x.SkillId });
                    table.ForeignKey(
                        name: "FK_AgentInstanceSkills_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AutomationTriggers",
                columns: table => new
                {
                    TriggerId = table.Column<string>(type: "TEXT", nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    ScheduleJson = table.Column<string>(type: "TEXT", nullable: true),
                    EventId = table.Column<string>(type: "TEXT", nullable: true),
                    CoreEventKey = table.Column<string>(type: "TEXT", nullable: true),
                    FilterExpression = table.Column<string>(type: "TEXT", nullable: true),
                    DispatchMode = table.Column<int>(type: "INTEGER", nullable: false),
                    DispatchWindowSeconds = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomationTriggers", x => x.TriggerId);
                    table.CheckConstraint("CK_AutomationTriggers_Dispatch", "(DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (DispatchMode = 1 AND DispatchWindowSeconds BETWEEN 60 AND 3600)");
                    table.CheckConstraint("CK_AutomationTriggers_Identity", "length(TriggerId) = 36 AND TriggerId <> '00000000-0000-0000-0000-000000000000' AND Revision >= 1");
                    table.CheckConstraint("CK_AutomationTriggers_Source", "(Kind = 0 AND ScheduleJson IS NOT NULL AND EventId IS NULL AND CoreEventKey IS NULL AND FilterExpression IS NULL AND DispatchMode = 0 AND DispatchWindowSeconds IS NULL) OR (Kind = 1 AND ScheduleJson IS NULL AND EventId IS NOT NULL AND CoreEventKey IS NULL) OR (Kind = 2 AND ScheduleJson IS NULL AND EventId IS NULL AND CoreEventKey IN ('run.completed','run.failed','session.completed','session.ended','instance.config_changed','harness.definition_adopted','email.message.received'))");
                    table.ForeignKey(
                        name: "FK_AutomationTriggers_Automations_AutomationId",
                        column: x => x.AutomationId,
                        principalTable: "Automations",
                        principalColumn: "AutomationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailWatchEvents",
                columns: table => new
                {
                    EventId = table.Column<string>(type: "TEXT", nullable: false),
                    AuthorityJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailWatchEvents", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_EmailWatchEvents_CoreEvents_EventId",
                        column: x => x.EventId,
                        principalTable: "CoreEvents",
                        principalColumn: "EventId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentCredentialBindings",
                columns: table => new
                {
                    BindingId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCredentialBindings", x => x.BindingId);
                    table.ForeignKey(
                        name: "FK_AgentCredentialBindings_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentCredentialBindings_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DriveAccountProfiles",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    GoogleSubject = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveAccountProfiles", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_DriveAccountProfiles_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriveOperations",
                columns: table => new
                {
                    OperationId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    EffectHash = table.Column<string>(type: "TEXT", nullable: false),
                    AuthEpoch = table.Column<long>(type: "INTEGER", nullable: false),
                    Tool = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<string>(type: "TEXT", nullable: false),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ProtectedUploadSession = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriveOperations", x => x.OperationId);
                    table.ForeignKey(
                        name: "FK_DriveOperations_AgentInstances_AgentInstanceId",
                        column: x => x.AgentInstanceId,
                        principalTable: "AgentInstances",
                        principalColumn: "InstanceId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DriveOperations_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailAccountProfiles",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileJson = table.Column<string>(type: "TEXT", nullable: false),
                    GoogleSubject = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailAccountProfiles", x => x.CredentialId);
                    table.ForeignKey(
                        name: "FK_EmailAccountProfiles_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailWatches",
                columns: table => new
                {
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextCheckAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RuntimeRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    StateJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailWatches", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_EmailWatches_Credentials_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SyntheticDriveFiles",
                columns: table => new
                {
                    CredentialId = table.Column<string>(type: "TEXT", nullable: false),
                    FileId = table.Column<string>(type: "TEXT", nullable: false),
                    MetadataJson = table.Column<string>(type: "TEXT", nullable: false),
                    Bytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Version = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyntheticDriveFiles", x => new { x.CredentialId, x.FileId });
                    table.ForeignKey(
                        name: "FK_SyntheticDriveFiles_Credentials_CredentialId",
                        column: x => x.CredentialId,
                        principalTable: "Credentials",
                        principalColumn: "CredentialId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailSendOperations",
                columns: table => new
                {
                    DraftId = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    DraftRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ApprovalId = table.Column<string>(type: "TEXT", nullable: false),
                    ActionHash = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ClaimedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSendOperations", x => x.DraftId);
                    table.ForeignKey(
                        name: "FK_EmailSendOperations_EmailDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "EmailDrafts",
                        principalColumn: "DraftId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Activations",
                columns: table => new
                {
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    DedupeKey = table.Column<string>(type: "TEXT", nullable: false),
                    BackgroundSourceKey = table.Column<string>(type: "TEXT", nullable: true),
                    AdmissionHash = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
                    AdmittedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Activations", x => x.ActivationId);
                    table.ForeignKey(
                        name: "FK_Activations_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationEntries",
                columns: table => new
                {
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    EntrySequence = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", nullable: true),
                    Role = table.Column<string>(type: "TEXT", nullable: false),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    ResponseId = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DeliveryMode = table.Column<string>(type: "TEXT", nullable: false),
                    HeardTextEndExclusive = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceivedTextEndExclusive = table.Column<int>(type: "INTEGER", nullable: false),
                    EnvelopeJson = table.Column<string>(type: "TEXT", nullable: true),
                    FailureReferenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    AttachmentRefsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SourceAdmissionFingerprint = table.Column<string>(type: "TEXT", nullable: true),
                    UserPartsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApplicationMessageEffectKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    FinishReason = table.Column<string>(type: "TEXT", nullable: true),
                    InterruptReason = table.Column<string>(type: "TEXT", nullable: true),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", nullable: true),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationEntries", x => x.EntryId);
                    table.ForeignKey(
                        name: "FK_ConversationEntries_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionSnapshots",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    SchemaVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    SummarizedThroughEntrySequence = table.Column<long>(type: "INTEGER", nullable: false),
                    PendingTopic = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: true),
                    LastEntrySequence = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    LastUserActivityAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LifecycleStatus = table.Column<string>(type: "TEXT", nullable: true),
                    PurposeKind = table.Column<string>(type: "TEXT", nullable: true),
                    PurposeDescription = table.Column<string>(type: "TEXT", nullable: true),
                    DeadlineAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    PurposeMetadataJson = table.Column<string>(type: "TEXT", nullable: true),
                    AgentCompletion = table.Column<string>(type: "TEXT", nullable: true),
                    UserCompletionAllowed = table.Column<bool>(type: "INTEGER", nullable: true),
                    UserCancellationAllowed = table.Column<bool>(type: "INTEGER", nullable: true),
                    LifecycleReason = table.Column<string>(type: "TEXT", nullable: true),
                    LifecycleSource = table.Column<string>(type: "TEXT", nullable: true),
                    LifecycleChangedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SpeechLocaleOverride = table.Column<string>(type: "TEXT", nullable: true),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", nullable: true),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", nullable: true),
                    ModelSelectionSource = table.Column<string>(type: "TEXT", nullable: true),
                    ModelHasExplicitReasoningEffort = table.Column<bool>(type: "INTEGER", nullable: false),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryFormatVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    SummaryGeneratedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    SummaryModelCatalogKey = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryModelProviderAlias = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryModelId = table.Column<string>(type: "TEXT", nullable: true),
                    SummaryModelReasoningEffort = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionSnapshots", x => x.SessionId);
                    table.ForeignKey(
                        name: "FK_SessionSnapshots_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentRuns",
                columns: table => new
                {
                    AgentRunId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    NextRetryAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    LeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ApprovalExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.AgentRunId);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Activations_ActivationId",
                        column: x => x.ActivationId,
                        principalTable: "Activations",
                        principalColumn: "ActivationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActivationSourceEntries",
                columns: table => new
                {
                    SessionId = table.Column<string>(type: "TEXT", nullable: false),
                    EntryId = table.Column<string>(type: "TEXT", nullable: false),
                    ActivationId = table.Column<string>(type: "TEXT", nullable: false),
                    Ordinal = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActivationSourceEntries", x => new { x.SessionId, x.EntryId });
                    table.ForeignKey(
                        name: "FK_ActivationSourceEntries_Activations_ActivationId",
                        column: x => x.ActivationId,
                        principalTable: "Activations",
                        principalColumn: "ActivationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ActivationSourceEntries_ConversationEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "ConversationEntries",
                        principalColumn: "EntryId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BackgroundCompletionReceipts",
                columns: table => new
                {
                    ChildAgentRunId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentSessionId = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimRunId = table.Column<string>(type: "TEXT", nullable: true),
                    InboxJson = table.Column<string>(type: "TEXT", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1L),
                    AgentInstanceId = table.Column<string>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", nullable: false),
                    ParentActivationId = table.Column<string>(type: "TEXT", nullable: true),
                    SkipReason = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackgroundCompletionReceipts", x => x.ChildAgentRunId);
                    table.ForeignKey(
                        name: "FK_BackgroundCompletionReceipts_Activations_ParentActivationId",
                        column: x => x.ParentActivationId,
                        principalTable: "Activations",
                        principalColumn: "ActivationId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BackgroundCompletionReceipts_AgentRuns_ChildAgentRunId",
                        column: x => x.ChildAgentRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TriggerOccurrences",
                columns: table => new
                {
                    OccurrenceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    TriggerId = table.Column<string>(type: "TEXT", nullable: true),
                    ExecutionTargetKind = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    ReportToSessionId = table.Column<string>(type: "TEXT", nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AutomationId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    AgentInstanceId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    ProfileId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    SourceKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    AdmittedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    EvidenceJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceEventId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    TriggerRevision = table.Column<long>(type: "INTEGER", nullable: true),
                    Disposition = table.Column<int>(type: "INTEGER", nullable: false),
                    DispositionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RoutingRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    RoutingUpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ClaimId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    ClaimLeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ExecutionSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    AcceptedAgentRunId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    LiveSessionId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    LiveEvaluationCompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    ModelCatalogKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModelProviderAlias = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ModelReasoningEffort = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ModelSource = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerOccurrences", x => x.OccurrenceId);
                    table.CheckConstraint("CK_TriggerOccurrences_Destination", "(ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)");
                    table.CheckConstraint("CK_TriggerOccurrences_ExecutionLink", "(ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (ExecutionSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (ExecutionSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_TriggerOccurrences_AgentRuns_AcceptedAgentRunId",
                        column: x => x.AcceptedAgentRunId,
                        principalTable: "AgentRuns",
                        principalColumn: "AgentRunId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TriggerOccurrences_Sessions_ExecutionSessionId",
                        column: x => x.ExecutionSessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TriggerOccurrences_Sessions_LiveSessionId",
                        column: x => x.LiveSessionId,
                        principalTable: "Sessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActivationSourceEntries_ActivationId_Ordinal",
                table: "ActivationSourceEntries",
                columns: new[] { "ActivationId", "Ordinal" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActivationSourceEntries_EntryId",
                table: "ActivationSourceEntries",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_AgentInstanceId_ProfileId_BackgroundSourceKey",
                table: "Activations",
                columns: new[] { "AgentInstanceId", "ProfileId", "BackgroundSourceKey" },
                unique: true,
                filter: "BackgroundSourceKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Activations_SessionId_DedupeKey",
                table: "Activations",
                columns: new[] { "SessionId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminEvents_OperationId",
                table: "AdminEvents",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdminEvents_TargetType_TargetId_OccurredAtUtc",
                table: "AdminEvents",
                columns: new[] { "TargetType", "TargetId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_AgentInstanceId_CredentialId",
                table: "AgentCredentialBindings",
                columns: new[] { "AgentInstanceId", "CredentialId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_AgentInstanceId_Reference",
                table: "AgentCredentialBindings",
                columns: new[] { "AgentInstanceId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCredentialBindings_CredentialId",
                table: "AgentCredentialBindings",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftEvaluationResults_DraftId_ScenarioId_RecordedAtUtc",
                table: "AgentDefinitionDraftEvaluationResults",
                columns: new[] { "DraftId", "ScenarioId", "RecordedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftEvaluationScenarios_DraftId",
                table: "AgentDefinitionDraftEvaluationScenarios",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftResources_DraftId",
                table: "AgentDefinitionDraftResources",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDraftResources_DraftId_LogicalPath",
                table: "AgentDefinitionDraftResources",
                columns: new[] { "DraftId", "LogicalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionDrafts_DefinitionId",
                table: "AgentDefinitionDrafts",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionPublicationResources_DefinitionId_Version_LogicalPath",
                table: "AgentDefinitionPublicationResources",
                columns: new[] { "DefinitionId", "Version", "LogicalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionSkillStates_AgentInstanceId",
                table: "AgentDefinitionSkillStates",
                column: "AgentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstanceResources_InstanceId_LogicalPath",
                table: "AgentInstanceResources",
                columns: new[] { "InstanceId", "LogicalPath" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstanceSkills_AgentInstanceId_UpdatedAtUtc",
                table: "AgentInstanceSkills",
                columns: new[] { "AgentInstanceId", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentInstances_DefinitionId",
                table: "AgentInstances",
                column: "DefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_ActivationId",
                table: "AgentRuns",
                column: "ActivationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_AgentInstanceId_ProfileId_SessionId_CreatedAtUtc",
                table: "AgentRuns",
                columns: new[] { "AgentInstanceId", "ProfileId", "SessionId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_SessionExecution",
                table: "AgentRuns",
                column: "SessionId",
                unique: true,
                filter: "Status IN (1, 2, 7)");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_Status_NextRetryAtUtc_CreatedAtUtc",
                table: "AgentRuns",
                columns: new[] { "Status", "NextRetryAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_SessionId",
                table: "Artifacts",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Artifacts_SessionId_AgentRunId",
                table: "Artifacts",
                columns: new[] { "SessionId", "AgentRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_SessionId",
                table: "Attachments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_State_ExpiresAtUtc",
                table: "Attachments",
                columns: new[] { "State", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTriggers_AutomationId_CoreEventKey",
                table: "AutomationTriggers",
                columns: new[] { "AutomationId", "CoreEventKey" },
                unique: true,
                filter: "CoreEventKey IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTriggers_AutomationId_EventId",
                table: "AutomationTriggers",
                columns: new[] { "AutomationId", "EventId" },
                unique: true,
                filter: "EventId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTriggers_AutomationId_Kind",
                table: "AutomationTriggers",
                columns: new[] { "AutomationId", "Kind" },
                unique: true,
                filter: "Kind = 0");

            migrationBuilder.CreateIndex(
                name: "IX_AutomationTriggers_EventId_Enabled",
                table: "AutomationTriggers",
                columns: new[] { "EventId", "Enabled" });

            migrationBuilder.CreateIndex(
                name: "IX_Automations_AgentInstanceId_ProfileId_Status",
                table: "Automations",
                columns: new[] { "AgentInstanceId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Automations_Status_NextOccurrenceAtUtc_AutomationId",
                table: "Automations",
                columns: new[] { "Status", "NextOccurrenceAtUtc", "AutomationId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_AgentInstanceId_ProfileId_ParentSessionId_CreatedAtUtc_ChildAgentRunId",
                table: "BackgroundCompletionReceipts",
                columns: new[] { "AgentInstanceId", "ProfileId", "ParentSessionId", "CreatedAtUtc", "ChildAgentRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_ClaimRunId",
                table: "BackgroundCompletionReceipts",
                column: "ClaimRunId");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_ParentActivationId",
                table: "BackgroundCompletionReceipts",
                column: "ParentActivationId",
                filter: "ParentActivationId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BackgroundCompletionReceipts_Status_CreatedAtUtc_ChildAgentRunId",
                table: "BackgroundCompletionReceipts",
                columns: new[] { "Status", "CreatedAtUtc", "ChildAgentRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_SessionId_EntrySequence",
                table: "ConversationEntries",
                columns: new[] { "SessionId", "EntrySequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ConversationEntries_SessionId_SourceEventId",
                table: "ConversationEntries",
                columns: new[] { "SessionId", "SourceEventId" },
                unique: true,
                filter: "SourceEventId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CoreEventBuckets_Flushed_DueAtUtc",
                table: "CoreEventBuckets",
                columns: new[] { "Flushed", "DueAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreEventDeliveries_AgentInstanceId_ProfileId_Status",
                table: "CoreEventDeliveries",
                columns: new[] { "AgentInstanceId", "ProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_CoreEvents_DedupeKey",
                table: "CoreEvents",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CoreEvents_Snapshotted_ReceivedAtUtc_EventId",
                table: "CoreEvents",
                columns: new[] { "Snapshotted", "ReceivedAtUtc", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_DriveAccountProfiles_GoogleSubject",
                table: "DriveAccountProfiles",
                column: "GoogleSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriveOperations_AgentInstanceId_CredentialId_EffectHash",
                table: "DriveOperations",
                columns: new[] { "AgentInstanceId", "CredentialId", "EffectHash" },
                unique: true,
                filter: "State IN ('Planned','Dispatched','Indeterminate')");

            migrationBuilder.CreateIndex(
                name: "IX_DriveOperations_CredentialId",
                table: "DriveOperations",
                column: "CredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailAccountProfiles_GoogleSubject",
                table: "EmailAccountProfiles",
                column: "GoogleSubject",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmailWatches_Enabled_NextCheckAtUtc",
                table: "EmailWatches",
                columns: new[] { "Enabled", "NextCheckAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Experiences_AgentInstanceId_CreatedAtUtc",
                table: "Experiences",
                columns: new[] { "AgentInstanceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Experiences_AgentInstanceId_SourceKind_SourceId_ThroughCursor",
                table: "Experiences",
                columns: new[] { "AgentInstanceId", "SourceKind", "SourceId", "ThroughCursor" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEventDeliveries_Status_EventId_TriggerId",
                table: "ExternalEventDeliveries",
                columns: new[] { "Status", "EventId", "TriggerId" });

            migrationBuilder.CreateIndex(
                name: "IX_ExternalEvents_ResourceId_SourceEventId",
                table: "ExternalEvents",
                columns: new[] { "ResourceId", "SourceEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageAttachments_SessionId",
                table: "MessageAttachments",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_SandboxExecutions_SessionId_AgentRunId_ToolCallId",
                table: "SandboxExecutions",
                columns: new[] { "SessionId", "AgentRunId", "ToolCallId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SandboxSessionLeases_AgentInstanceId_ExpiresAt",
                table: "SandboxSessionLeases",
                columns: new[] { "AgentInstanceId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_AgentInstanceId",
                table: "Sessions",
                column: "AgentInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_DurablyDeletedAtUtc_ArchivedAtUtc_UpdatedAtUtc_SessionId",
                table: "Sessions",
                columns: new[] { "DurablyDeletedAtUtc", "ArchivedAtUtc", "UpdatedAtUtc", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_OwnerInstanceId_OwnerProfileId_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "OwnerInstanceId", "OwnerProfileId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0 AND Scope = 1");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_SessionId_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "SessionId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0 AND Scope = 0");

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_SessionId_Status",
                table: "StructuredMemories",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StructuredMemories_UserOwner_Kind_SubjectKey",
                table: "StructuredMemories",
                columns: new[] { "OwnerProfileId", "Kind", "SubjectKey" },
                unique: true,
                filter: "Status = 0 AND Scope = 2");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_AcceptedAgentRunId",
                table: "TriggerOccurrences",
                column: "AcceptedAgentRunId",
                unique: true,
                filter: "AcceptedAgentRunId IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_AgentInstanceId_ProfileId_DedupeKey",
                table: "TriggerOccurrences",
                columns: new[] { "AgentInstanceId", "ProfileId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_AgentInstanceId_ProfileId_Disposition",
                table: "TriggerOccurrences",
                columns: new[] { "AgentInstanceId", "ProfileId", "Disposition" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_Disposition_ClaimLeaseExpiresAtUtc",
                table: "TriggerOccurrences",
                columns: new[] { "Disposition", "ClaimLeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_ExecutionSessionId",
                table: "TriggerOccurrences",
                column: "ExecutionSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_TriggerOccurrences_LiveSessionId",
                table: "TriggerOccurrences",
                column: "LiveSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_WebhookEvents_EventKey",
                table: "WebhookEvents",
                column: "EventKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ActivationSourceEntries");

            migrationBuilder.DropTable(
                name: "AdminEvents");

            migrationBuilder.DropTable(
                name: "AgentCredentialBindings");

            migrationBuilder.DropTable(
                name: "AgentDefinitionDraftEvaluationResults");

            migrationBuilder.DropTable(
                name: "AgentDefinitionDraftEvaluationScenarios");

            migrationBuilder.DropTable(
                name: "AgentDefinitionDraftResources");

            migrationBuilder.DropTable(
                name: "AgentDefinitionDrafts");

            migrationBuilder.DropTable(
                name: "AgentDefinitionPublicationResources");

            migrationBuilder.DropTable(
                name: "AgentDefinitionPublications");

            migrationBuilder.DropTable(
                name: "AgentDefinitionResourceStates");

            migrationBuilder.DropTable(
                name: "AgentDefinitionSkillStates");

            migrationBuilder.DropTable(
                name: "AgentInstanceResources");

            migrationBuilder.DropTable(
                name: "AgentInstanceSkills");

            migrationBuilder.DropTable(
                name: "Artifacts");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "AutomationTriggers");

            migrationBuilder.DropTable(
                name: "BackgroundCompletionReceipts");

            migrationBuilder.DropTable(
                name: "BrowserPrivacy");

            migrationBuilder.DropTable(
                name: "CoreEventBuckets");

            migrationBuilder.DropTable(
                name: "CoreEventDeliveries");

            migrationBuilder.DropTable(
                name: "DriveAccountProfiles");

            migrationBuilder.DropTable(
                name: "DriveOperations");

            migrationBuilder.DropTable(
                name: "EmailAccountProfiles");

            migrationBuilder.DropTable(
                name: "EmailAccountSelections");

            migrationBuilder.DropTable(
                name: "EmailSendOperations");

            migrationBuilder.DropTable(
                name: "EmailWatchEvents");

            migrationBuilder.DropTable(
                name: "EmailWatches");

            migrationBuilder.DropTable(
                name: "ExperienceSettings");

            migrationBuilder.DropTable(
                name: "Experiences");

            migrationBuilder.DropTable(
                name: "ExternalEventDeliveries");

            migrationBuilder.DropTable(
                name: "ExternalEvents");

            migrationBuilder.DropTable(
                name: "IdentityMaintenanceSettings");

            migrationBuilder.DropTable(
                name: "MessageAttachments");

            migrationBuilder.DropTable(
                name: "OwnerCapabilities");

            migrationBuilder.DropTable(
                name: "SandboxExecutions");

            migrationBuilder.DropTable(
                name: "SandboxInstanceVerifications");

            migrationBuilder.DropTable(
                name: "SandboxProfileVerifications");

            migrationBuilder.DropTable(
                name: "SandboxProfiles");

            migrationBuilder.DropTable(
                name: "SandboxSessionLeases");

            migrationBuilder.DropTable(
                name: "SessionSnapshots");

            migrationBuilder.DropTable(
                name: "StructuredMemories");

            migrationBuilder.DropTable(
                name: "SyntheticDriveFiles");

            migrationBuilder.DropTable(
                name: "TriggerOccurrences");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "WebhookEvents");

            migrationBuilder.DropTable(
                name: "ConversationEntries");

            migrationBuilder.DropTable(
                name: "Automations");

            migrationBuilder.DropTable(
                name: "AgentInstances");

            migrationBuilder.DropTable(
                name: "EmailDrafts");

            migrationBuilder.DropTable(
                name: "CoreEvents");

            migrationBuilder.DropTable(
                name: "Credentials");

            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropTable(
                name: "Activations");

            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
