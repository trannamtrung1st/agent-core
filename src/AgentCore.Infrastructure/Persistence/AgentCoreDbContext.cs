using AgentCore.Domain.Conversation;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class SessionRecord
{
    public string SessionId { get; set; } = "";
    public string AgentId { get; set; } = "";
    public int AgentVersion { get; set; }
    public string DefinitionJson { get; set; } = "";
    public string Mode { get; set; } = "";
    public string? PendingMode { get; set; }
    public string Status { get; set; } = "";
    public string? PauseReason { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Revision { get; set; }
    public string Title { get; set; } = SessionTitles.Default;
    public long RuntimeEpoch { get; set; }
    public bool WorkspaceOwned { get; set; } = true;
    public long? ArchivedAtUtc { get; set; }
    public long? DurablyDeletedAtUtc { get; set; }
    public string AgentInstanceId { get; set; } = "";
    public string? PinnedPersonaJson { get; set; }
    public long? PinnedPersonaRevision { get; set; }
    public string PendingAgentInputIdsJson { get; set; } = "[]";
    public string OriginJson { get; set; } = "{\"kind\":0}";
    public int Surfaces { get; set; } = (int)SessionSurface.ChatList;
    public SnapshotRecord? Snapshot { get; set; }
    public List<EntryRecord> Entries { get; set; } = [];
}

public sealed class SnapshotRecord
{
    public string SessionId { get; set; } = "";
    public int SchemaVersion { get; set; } = 1;
    public string Summary { get; set; } = "";
    public long SummarizedThroughEntrySequence { get; set; }
    public string? PendingTopic { get; set; }
    public string? ProfileId { get; set; }
    public long LastEntrySequence { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long? LastUserActivityAtUtc { get; set; }
    public string? LifecycleStatus { get; set; }
    public string? PurposeKind { get; set; }
    public string? PurposeDescription { get; set; }
    public long? DeadlineAtUtc { get; set; }
    public string? PurposeMetadataJson { get; set; }
    public string? AgentCompletion { get; set; }
    public bool? UserCompletionAllowed { get; set; }
    public bool? UserCancellationAllowed { get; set; }
    public string? LifecycleReason { get; set; }
    public string? LifecycleSource { get; set; }
    public long? LifecycleChangedAtUtc { get; set; }
    public string? SpeechLocaleOverride { get; set; }
    public string? ModelCatalogKey { get; set; }
    public string? ModelProviderAlias { get; set; }
    public string? ModelId { get; set; }
    public string? ModelSelectionSource { get; set; }
    public string? ModelReasoningEffort { get; set; }
    public int SummaryFormatVersion { get; set; }
    public long? SummaryGeneratedAtUtc { get; set; }
    public string? SummaryModelCatalogKey { get; set; }
    public string? SummaryModelProviderAlias { get; set; }
    public string? SummaryModelId { get; set; }
    public string? SummaryModelReasoningEffort { get; set; }
    public SessionRecord Session { get; set; } = null!;
}

public sealed class EntryRecord
{
    public string EntryId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public long EntrySequence { get; set; }
    public string? SourceEventId { get; set; }
    public string Role { get; set; } = "";
    public string Text { get; set; } = "";
    public string? ResponseId { get; set; }
    public string Status { get; set; } = "";
    public string DeliveryMode { get; set; } = "";
    public int HeardTextEndExclusive { get; set; }
    public int ReceivedTextEndExclusive { get; set; }
    public string? EnvelopeJson { get; set; }
    public string? FailureReferenceJson { get; set; }
    public string? AttachmentRefsJson { get; set; }
    public string? SourceAdmissionFingerprint { get; set; }

    public string? ApplicationMessageEffectKey { get; set; }
    public string? FinishReason { get; set; }
    public string? InterruptReason { get; set; }
    public string? ModelCatalogKey { get; set; }
    public string? ModelProviderAlias { get; set; }
    public string? ModelId { get; set; }
    public string? ModelReasoningEffort { get; set; }
    public long CreatedAtUtc { get; set; }
    public SessionRecord Session { get; set; } = null!;
}

public sealed class ProfileRecord
{
    public string ProfileId { get; set; } = "";
    public string PreferencesJson { get; set; } = "{}";
    public long Revision { get; set; }
    public long UpdatedAtUtc { get; set; }
}

public sealed class OwnerCapabilityRecord
{
    public string TokenHash { get; set; } = "";
    public long CreatedAtUtc { get; set; }
}

public sealed class AgentDefinitionDraftRecord
{
    public string DraftId { get; set; } = "";
    public string DefinitionId { get; set; } = "";
    public long Revision { get; set; }
    public string CandidateJson { get; set; } = "";
    public int SourceKind { get; set; }
    public int? SourceVersion { get; set; }
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
}

public sealed class AgentDefinitionPublicationRecord
{
    public string DefinitionId { get; set; } = "";
    public int Version { get; set; }
    public string PayloadJson { get; set; } = "";
    public long SourceDraftRevision { get; set; }
    public int Status { get; set; }
    public long MetadataRevision { get; set; }
    public long PublishedAtUtc { get; set; }
}

public sealed class AgentDefinitionDraftEvaluationScenarioRecord
{
    public string DraftId { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public int ScenarioVersion { get; set; }
    public string Title { get; set; } = "";
    public string Prompt { get; set; } = "";
    public int RequirementLevel { get; set; }
    public int CheckType { get; set; }
    public string ToolName { get; set; } = "";
    public long UpdatedAtUtc { get; set; }
}

public sealed class AgentDefinitionDraftEvaluationResultRecord
{
    public string ResultId { get; set; } = "";
    public string DraftId { get; set; } = "";
    public long DraftRevision { get; set; }
    public string ConfigurationFingerprint { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public int ScenarioVersion { get; set; }
    public string RuntimeKind { get; set; } = "";
    public bool Passed { get; set; }
    public string FindingsJson { get; set; } = "[]";
    public long RecordedAtUtc { get; set; }
}

public sealed class AgentDefinitionDraftResourceRecord
{
    public string ResourceId { get; set; } = "";
    public string DraftId { get; set; } = "";
    public string LogicalPath { get; set; } = "";
    public int Kind { get; set; }
    public string MediaType { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long ByteLength { get; set; }
    public long UpdatedAtUtc { get; set; }
}

public sealed class AgentDefinitionPublicationResourceRecord
{
    public string DefinitionId { get; set; } = "";
    public int Version { get; set; }
    public string ResourceId { get; set; } = "";
    public string LogicalPath { get; set; } = "";
    public int Kind { get; set; }
    public string MediaType { get; set; } = "";
    public string ContentSha256 { get; set; } = "";
    public long ByteLength { get; set; }
}

public sealed class AgentCoreDbContext(DbContextOptions<AgentCoreDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SnapshotRecord> Snapshots => Set<SnapshotRecord>();
    public DbSet<EntryRecord> Entries => Set<EntryRecord>();
    public DbSet<ProfileRecord> Profiles => Set<ProfileRecord>();
    public DbSet<OwnerCapabilityRecord> OwnerCapabilities => Set<OwnerCapabilityRecord>();
    public DbSet<AttachmentRecordRow> Attachments => Set<AttachmentRecordRow>();
    public DbSet<MessageAttachmentRow> MessageAttachments => Set<MessageAttachmentRow>();
    public DbSet<AgentWorkspaceRow> AgentWorkspaceItems => Set<AgentWorkspaceRow>();
    public DbSet<ArtifactRecordRow> Artifacts => Set<ArtifactRecordRow>();
    public DbSet<StructuredMemoryRecord> StructuredMemories => Set<StructuredMemoryRecord>();
    public DbSet<AgentInstanceRecord> AgentInstances => Set<AgentInstanceRecord>();
    public DbSet<AutomationRecord> Automations => Set<AutomationRecord>();
    public DbSet<WebhookEventRecord> WebhookEvents => Set<WebhookEventRecord>();
    public DbSet<ExternalEventRecord> ExternalEvents => Set<ExternalEventRecord>();
    public DbSet<ExternalEventDeliveryRecord> ExternalEventDeliveries => Set<ExternalEventDeliveryRecord>();
    public DbSet<TriggerOccurrenceRecord> TriggerOccurrences => Set<TriggerOccurrenceRecord>();
    public DbSet<BackgroundCompletionReceiptRecord> BackgroundCompletionReceipts => Set<BackgroundCompletionReceiptRecord>();
    public DbSet<ActivationRecord> Activations => Set<ActivationRecord>();
    public DbSet<AgentRunRecord> AgentRuns => Set<AgentRunRecord>();
    public DbSet<ActivationSourceEntryRecord> ActivationSourceEntries => Set<ActivationSourceEntryRecord>();

    public DbSet<AgentDefinitionDraftRecord> AgentDefinitionDrafts => Set<AgentDefinitionDraftRecord>();
    public DbSet<AgentDefinitionPublicationRecord> AgentDefinitionPublications => Set<AgentDefinitionPublicationRecord>();
    public DbSet<AgentDefinitionDraftResourceRecord> AgentDefinitionDraftResources => Set<AgentDefinitionDraftResourceRecord>();
    public DbSet<AgentDefinitionDraftEvaluationScenarioRecord> AgentDefinitionDraftEvaluationScenarios =>
        Set<AgentDefinitionDraftEvaluationScenarioRecord>();
    public DbSet<AgentDefinitionDraftEvaluationResultRecord> AgentDefinitionDraftEvaluationResults =>
        Set<AgentDefinitionDraftEvaluationResultRecord>();
    public DbSet<AgentDefinitionPublicationResourceRecord> AgentDefinitionPublicationResources =>
        Set<AgentDefinitionPublicationResourceRecord>();
    public DbSet<AdminEventRecord> AdminEvents => Set<AdminEventRecord>();

    public DbSet<CredentialRecord> Credentials => Set<CredentialRecord>();
    public DbSet<AgentCredentialBindingRecord> AgentCredentialBindings => Set<AgentCredentialBindingRecord>();


    public DbSet<ExperienceRecord> Experiences => Set<ExperienceRecord>();
    public DbSet<IdentityMaintenanceSettingsRecord> IdentityMaintenanceSettings => Set<IdentityMaintenanceSettingsRecord>();
    public DbSet<ExperienceSettingsRecord> ExperienceSettings => Set<ExperienceSettingsRecord>();

    public DbSet<AgentDefinitionSkillStateRecord> AgentDefinitionSkillStates => Set<AgentDefinitionSkillStateRecord>();
    public DbSet<AgentInstanceSkillRecord> AgentInstanceSkills => Set<AgentInstanceSkillRecord>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentDefinitionSkillStateRecord>(e => {
            e.ToTable("AgentDefinitionSkillStates"); e.HasKey(s => new { s.AgentInstanceId, s.DefinitionSkillId });
            e.Property(s => s.AgentInstanceId).HasMaxLength(36); e.Property(s => s.DefinitionSkillId).HasMaxLength(64);
            e.Property(s => s.Revision).IsConcurrencyToken(); e.HasIndex(s => s.AgentInstanceId);
            e.HasOne<AgentInstanceRecord>().WithMany().HasForeignKey(s => s.AgentInstanceId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<AgentInstanceSkillRecord>(e => {
            e.ToTable("AgentInstanceSkills"); e.HasKey(s => new { s.AgentInstanceId, s.SkillId });
            e.Property(s => s.SkillId).HasMaxLength(64); e.Property(s => s.AgentInstanceId).HasMaxLength(36);
            e.Property(s => s.Name).IsRequired().HasMaxLength(80);
            e.Property(s => s.Description).IsRequired().HasMaxLength(240);
            e.Property(s => s.Procedure).IsRequired().HasMaxLength(4000);
            e.Property(s => s.RequiredCapabilitiesJson).IsRequired().HasMaxLength(1024);
            e.Property(s => s.SourceDefinitionId).HasMaxLength(64);
            e.Property(s => s.SourceDefinitionSkillId).HasMaxLength(64);
            e.Property(s => s.Revision).IsConcurrencyToken();
            e.HasIndex(s => new { s.AgentInstanceId, s.UpdatedAtUtc });
            e.HasOne<AgentInstanceRecord>().WithMany().HasForeignKey(s => s.AgentInstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IdentityMaintenanceSettingsRecord>().HasKey(r => r.AgentInstanceId);
        modelBuilder.Entity<ExperienceSettingsRecord>().HasKey(r => r.AgentInstanceId);
        var experience = modelBuilder.Entity<ExperienceRecord>();
        experience.HasKey(r => r.ExperienceId);
        experience.Property(r => r.Revision).IsConcurrencyToken();
        experience.HasIndex(r => new { r.AgentInstanceId, r.SourceKind, r.SourceId, r.ThroughCursor }).IsUnique();
        experience.HasIndex(r => new { r.AgentInstanceId, r.CreatedAtUtc });
        modelBuilder.Entity<SessionRecord>(entity =>
        {
            entity.ToTable("Sessions");
            entity.HasKey(row => row.SessionId);
            entity.Property(row => row.SessionId).HasMaxLength(36);
            entity.Property(row => row.DefinitionJson).IsRequired();
            entity.HasOne(row => row.Snapshot)
                .WithOne(row => row.Session)
                .HasForeignKey<SnapshotRecord>(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(row => row.Entries)
                .WithOne(row => row.Session)
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.Property(row => row.Title).HasMaxLength(200).IsRequired();
            entity.Property(row => row.WorkspaceOwned).HasDefaultValue(true);
            entity.HasIndex(row => new { row.DurablyDeletedAtUtc, row.ArchivedAtUtc, row.UpdatedAtUtc, row.SessionId });
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.HasIndex(row => row.AgentInstanceId);
            entity.Property(row => row.OriginJson).HasDefaultValue("{\"kind\":0}").IsRequired();
            entity.Property(row => row.Surfaces).HasDefaultValue((int)SessionSurface.ChatList);
        });
        modelBuilder.Entity<BackgroundCompletionReceiptRecord>(entity =>
        {
            entity.ToTable("BackgroundCompletionReceipts");
            entity.Property(row => row.Revision).IsConcurrencyToken().HasDefaultValue(1L);
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.ParentSessionId, row.CreatedAtUtc, row.ChildAgentRunId });
            entity.HasIndex(row => new { row.Status, row.CreatedAtUtc, row.ChildAgentRunId });
            entity.HasIndex(row => row.ClaimRunId);
            entity.HasKey(row => row.ChildAgentRunId);
            entity.HasIndex(row => row.ParentActivationId).HasFilter("ParentActivationId IS NOT NULL");
            entity.HasOne<AgentRunRecord>().WithOne().HasForeignKey<BackgroundCompletionReceiptRecord>(row => row.ChildAgentRunId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ActivationRecord>().WithMany().HasForeignKey(row => row.ParentActivationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ActivationRecord>(entity =>
        {
            entity.ToTable("Activations");
            entity.HasKey(row => row.ActivationId);
            entity.HasIndex(row => new { row.SessionId, row.DedupeKey }).IsUnique();
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.BackgroundSourceKey })
                .IsUnique().HasFilter("BackgroundSourceKey IS NOT NULL");
            entity.HasOne<SessionRecord>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AgentRunRecord>(entity =>
        {
            entity.HasIndex(row => row.SessionId).IsUnique().HasFilter("Status IN (1, 2, 7)").HasDatabaseName("IX_AgentRuns_SessionExecution");
            entity.ToTable("AgentRuns");
            entity.HasKey(row => row.AgentRunId);
            entity.HasIndex(row => row.ActivationId).IsUnique();
            entity.HasOne<ActivationRecord>().WithOne().HasForeignKey<AgentRunRecord>(row => row.ActivationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRecord>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.Status, row.NextRetryAtUtc, row.CreatedAtUtc });
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.SessionId, row.CreatedAtUtc });
        });
        modelBuilder.Entity<ActivationSourceEntryRecord>(entity =>
        {
            entity.ToTable("ActivationSourceEntries");
            entity.HasKey(row => new { row.SessionId, row.EntryId });
            entity.HasIndex(row => new { row.ActivationId, row.Ordinal }).IsUnique();
            entity.HasOne<ActivationRecord>().WithMany().HasForeignKey(row => row.ActivationId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<EntryRecord>().WithMany().HasForeignKey(row => row.EntryId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<SnapshotRecord>(entity =>
        {
            entity.ToTable("SessionSnapshots");
            entity.HasKey(row => row.SessionId);
        });
        modelBuilder.Entity<EntryRecord>(entity =>
        {
            entity.ToTable("ConversationEntries");
            entity.HasKey(row => row.EntryId);
            entity.HasIndex(row => new { row.SessionId, row.EntrySequence }).IsUnique();
            entity.HasIndex(row => new { row.SessionId, row.SourceEventId })
                .IsUnique()
                .HasFilter("SourceEventId IS NOT NULL");
            entity.Property(row => row.ApplicationMessageEffectKey).HasMaxLength(200);
        });
        modelBuilder.Entity<ProfileRecord>(entity =>
        {
            entity.ToTable("UserProfiles");
            entity.HasKey(row => row.ProfileId);
        });
        modelBuilder.Entity<OwnerCapabilityRecord>(entity =>
        {
            entity.ToTable("OwnerCapabilities");
            entity.HasKey(row => row.TokenHash);
            entity.Property(row => row.TokenHash).HasMaxLength(64);
        });
        modelBuilder.Entity<AttachmentRecordRow>(entity =>
        {
            entity.ToTable("Attachments");
            entity.HasKey(row => row.AttachmentId);
            entity.Property(row => row.AttachmentId).HasMaxLength(36);
            entity.Property(row => row.SessionId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.BlobKey).HasMaxLength(80).IsRequired();
            entity.Property(row => row.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(row => row.Sha256Hex).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => row.SessionId);
            entity.HasIndex(row => new { row.State, row.ExpiresAtUtc });
        });
        modelBuilder.Entity<MessageAttachmentRow>(entity =>
        {
            entity.ToTable("MessageAttachments");
            entity.HasKey(row => new { row.EntryId, row.AttachmentId });
            entity.HasIndex(row => row.SessionId);
        });
        modelBuilder.Entity<AgentWorkspaceRow>(entity =>
        {
            entity.ToTable("AgentWorkspaceItems");
            entity.HasKey(row => row.ItemId);
            entity.Property(row => row.ItemId).HasMaxLength(36);
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.PathKey).HasMaxLength(512).IsRequired();
            entity.Property(row => row.BlobKey).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.AgentInstanceId, row.PathKey }).IsUnique();
        });
        modelBuilder.Entity<ArtifactRecordRow>(entity =>
        {
            entity.ToTable("Artifacts");
            entity.HasKey(row => row.ArtifactId);
            entity.Property(row => row.ArtifactId).HasMaxLength(36);
            entity.Property(row => row.SessionId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.BlobKey).HasMaxLength(80).IsRequired();
            entity.Property(row => row.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(row => row.Sha256Hex).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => row.SessionId);
            entity.Property(row => row.AgentRunId).HasMaxLength(36);
            entity.HasIndex(row => new { row.SessionId, row.AgentRunId });
        });
        modelBuilder.Entity<StructuredMemoryRecord>(entity =>
        {
            entity.ToTable("StructuredMemories");
            entity.HasKey(row => row.MemoryId);
            entity.Property(row => row.MemoryId).HasMaxLength(36);
            entity.Property(row => row.SessionId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.Subject).HasMaxLength(128).IsRequired();
            entity.Property(row => row.SubjectKey).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Content).IsRequired();
            entity.Property(row => row.Source).HasMaxLength(64).IsRequired();
            entity.Property(row => row.SourceEntryIdsJson).IsRequired();
            entity.Property(row => row.SupersedesMemoryId).HasMaxLength(36);
            entity.Property(row => row.Scope).HasDefaultValue(0);
            entity.Property(row => row.OwnerInstanceId).HasMaxLength(36);
            entity.Property(row => row.OwnerProfileId).HasMaxLength(36);
            entity.Property(row => row.OriginMemoryId).HasMaxLength(36);
            entity.Property(row => row.OriginSessionId).HasMaxLength(36);
            entity.Property(row => row.MaintenanceAgentInstanceId).HasMaxLength(36);
            entity.Property(row => row.MaintenanceSessionId).HasMaxLength(36);
            entity.Property(row => row.MaintenanceAgentRunId).HasMaxLength(36);
            entity.HasIndex(row => new { row.SessionId, row.Status });
            entity.HasIndex(row => new { row.SessionId, row.Kind, row.SubjectKey })
                .IsUnique()
                .HasFilter("Status = 0 AND Scope = 0");
            entity.HasIndex(row => new { row.OwnerInstanceId, row.OwnerProfileId, row.Kind, row.SubjectKey })
                .IsUnique()
                .HasFilter("Status = 0 AND Scope = 1");
            entity.HasIndex(row => new { row.OwnerProfileId, row.Kind, row.SubjectKey })
                .IsUnique()
                .HasFilter("Status = 0 AND Scope = 2")
                .HasDatabaseName("IX_StructuredMemories_UserOwner_Kind_SubjectKey");
        });
        modelBuilder.Entity<AgentInstanceRecord>(entity =>
        {
            entity.ToTable("AgentInstances");
            entity.HasKey(row => row.InstanceId);
            entity.Property(row => row.InstanceId).HasMaxLength(36);
            entity.Property(row => row.DefinitionId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.PersonaJson).IsRequired();
            entity.Property(row => row.Lifecycle).HasMaxLength(32).IsRequired();
            entity.Property(row => row.UnattendedModelCatalogKey).HasMaxLength(128);
            entity.Property(row => row.UnattendedReasoningEffort).HasMaxLength(64);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => row.DefinitionId);
        });
        modelBuilder.Entity<AutomationRecord>(entity =>
        {
            entity.ToTable("Automations", table => table.HasCheckConstraint("CK_Automations_Destination", "(ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)"));
            entity.HasKey(row => row.AutomationId);
            entity.Property(row => row.AutomationId).HasMaxLength(36);
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ProfileId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.Name).HasMaxLength(120).IsRequired();
            entity.Property(row => row.Instructions).HasMaxLength(2000).IsRequired();
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.Property(row => row.ScheduleJson).HasMaxLength(4000);
            entity.Property(row => row.SourceSessionId).HasMaxLength(36);
            entity.Property(row => row.SourceEventId).HasMaxLength(36);
            entity.Property(row => row.SuspensionReason).HasMaxLength(200);
            entity.Property(row => row.ModelOverrideCatalogKey).HasMaxLength(128);
            entity.Property(row => row.ModelOverrideReasoningEffort).HasMaxLength(64);
            entity.Property(row => row.EventId).HasMaxLength(36);
            entity.HasIndex(row => new { row.EventId, row.Status });
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.Status });
            entity.HasIndex(row => new { row.Status, row.NextOccurrenceAtUtc, row.AutomationId });
        });
        modelBuilder.Entity<WebhookEventRecord>(entity =>
        {
            entity.ToTable("WebhookEvents");
            entity.HasKey(row => row.ResourceId);
            entity.Property(row => row.ResourceId).HasMaxLength(36);
            entity.Property(row => row.DisplayName).HasMaxLength(80).IsRequired();
            entity.Property(row => row.EventKey).HasMaxLength(64).IsRequired();
            entity.Property(row => row.CredentialHash).HasMaxLength(64);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => row.EventKey).IsUnique();
        });
        modelBuilder.Entity<ExternalEventRecord>(entity =>
        {
            entity.ToTable("ExternalEvents");
            entity.HasKey(row => row.EventId);
            entity.Property(row => row.EventId).HasMaxLength(36);
            entity.Property(row => row.ResourceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.SourceEventId).HasMaxLength(64).IsRequired();
            entity.Property(row => row.EvidenceJson).HasMaxLength(4096).IsRequired();
            entity.HasIndex(row => new { row.ResourceId, row.SourceEventId }).IsUnique();
        });
        modelBuilder.Entity<ExternalEventDeliveryRecord>(entity =>
        {
            entity.ToTable("ExternalEventDeliveries");
            entity.HasKey(row => new { row.EventId, row.AutomationId });
            entity.Property(row => row.EventId).HasMaxLength(36);
            entity.Property(row => row.AutomationId).HasMaxLength(36);
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ProfileId).HasMaxLength(36).IsRequired();
            entity.HasIndex(row => new { row.Status, row.EventId, row.AutomationId });
        });
        modelBuilder.Entity<TriggerOccurrenceRecord>(entity =>
        {
            entity.ToTable("TriggerOccurrences", table => { table.HasCheckConstraint("CK_TriggerOccurrences_Destination", "(ExecutionTargetKind = 0 AND TargetSessionId IS NULL) OR (ExecutionTargetKind = 1 AND TargetSessionId IS NOT NULL AND ReportToSessionId IS NULL)"); table.HasCheckConstraint("CK_TriggerOccurrences_ExecutionLink",
                "(ExecutionSessionId IS NULL AND AcceptedAgentRunId IS NULL) OR (ExecutionSessionId IS NOT NULL AND LiveSessionId IS NULL AND AcceptedAgentRunId IS NOT NULL) OR (ExecutionSessionId IS NULL AND LiveSessionId IS NOT NULL AND AcceptedAgentRunId IS NOT NULL AND LiveEvaluationCompletedAtUtc IS NOT NULL)"); });
            entity.HasKey(row => row.OccurrenceId);
            entity.Property(row => row.OccurrenceId).HasMaxLength(36);
            entity.Property(row => row.DedupeKey).HasMaxLength(200).IsRequired();
            entity.Property(row => row.AutomationId).HasMaxLength(36);
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ProfileId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.EvidenceJson).HasMaxLength(4096).IsRequired();
            entity.Property(row => row.SourceEventId).HasMaxLength(36);
            entity.Property(row => row.DispositionReason).HasMaxLength(200);
            entity.Property(row => row.ClaimId).HasMaxLength(36);
            entity.Property(row => row.ExecutionSessionId).HasMaxLength(36);
            entity.Property(row => row.AcceptedAgentRunId).HasMaxLength(36);
            entity.Property(row => row.LiveSessionId).HasMaxLength(36);
            entity.HasIndex(row => row.ExecutionSessionId);
            entity.HasIndex(row => row.AcceptedAgentRunId).IsUnique().HasFilter("AcceptedAgentRunId IS NOT NULL");
            entity.Property(row => row.RoutingRevision).IsConcurrencyToken();
            entity.HasOne<SessionRecord>().WithMany().HasForeignKey(row => row.ExecutionSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SessionRecord>().WithMany().HasForeignKey(row => row.LiveSessionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AgentRunRecord>().WithMany().HasForeignKey(row => row.AcceptedAgentRunId).OnDelete(DeleteBehavior.Restrict);
            entity.Property(row => row.ModelCatalogKey).HasMaxLength(128);
            entity.Property(row => row.ModelProviderAlias).HasMaxLength(128);
            entity.Property(row => row.ModelId).HasMaxLength(128);
            entity.Property(row => row.ModelReasoningEffort).HasMaxLength(64);
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.DedupeKey }).IsUnique();
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.Disposition });
            entity.HasIndex(row => new { row.Disposition, row.ClaimLeaseExpiresAtUtc });
        });
        modelBuilder.Entity<AgentDefinitionDraftRecord>(entity =>
        {
            entity.ToTable("AgentDefinitionDrafts");
            entity.HasKey(row => row.DraftId);
            entity.Property(row => row.DraftId).HasMaxLength(36);
            entity.Property(row => row.DefinitionId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.CandidateJson).IsRequired();
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.HasIndex(row => row.DefinitionId);
        });
        modelBuilder.Entity<AgentDefinitionPublicationRecord>(entity =>
        {
            entity.ToTable("AgentDefinitionPublications");
            entity.HasKey(row => new { row.DefinitionId, row.Version });
            entity.Property(row => row.DefinitionId).HasMaxLength(128);
            entity.Property(row => row.PayloadJson).IsRequired();
            entity.Property(row => row.MetadataRevision).IsConcurrencyToken();
        });
        modelBuilder.Entity<AgentDefinitionDraftEvaluationScenarioRecord>(entity =>
        {
            entity.ToTable("AgentDefinitionDraftEvaluationScenarios");
            entity.HasKey(row => new { row.DraftId, row.ScenarioId });
            entity.Property(row => row.DraftId).HasMaxLength(36);
            entity.Property(row => row.ScenarioId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.Title).HasMaxLength(256).IsRequired();
            entity.Property(row => row.Prompt).HasMaxLength(4096).IsRequired();
            entity.Property(row => row.ToolName).HasMaxLength(128);
            entity.HasIndex(row => row.DraftId);
        });
        modelBuilder.Entity<AgentDefinitionDraftEvaluationResultRecord>(entity =>
        {
            entity.ToTable("AgentDefinitionDraftEvaluationResults");
            entity.HasKey(row => row.ResultId);
            entity.Property(row => row.ResultId).HasMaxLength(36);
            entity.Property(row => row.DraftId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ConfigurationFingerprint).HasMaxLength(128).IsRequired();
            entity.Property(row => row.ScenarioId).HasMaxLength(128).IsRequired();
            entity.Property(row => row.RuntimeKind).HasMaxLength(64).IsRequired();
            entity.Property(row => row.FindingsJson).IsRequired();
            entity.HasIndex(row => new { row.DraftId, row.ScenarioId, row.RecordedAtUtc });
        });
        modelBuilder.Entity<AgentDefinitionDraftResourceRecord>(entity =>
        {
            entity.ToTable("AgentDefinitionDraftResources");
            entity.HasKey(row => row.ResourceId);
            entity.Property(row => row.ResourceId).HasMaxLength(36);
            entity.Property(row => row.DraftId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.LogicalPath).HasMaxLength(240).IsRequired();
            entity.Property(row => row.MediaType).HasMaxLength(64).IsRequired();
            entity.Property(row => row.ContentSha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => new { row.DraftId, row.LogicalPath }).IsUnique();
            entity.HasIndex(row => row.DraftId);
        });
        modelBuilder.Entity<AgentDefinitionPublicationResourceRecord>(entity =>
        {
            entity.ToTable("AgentDefinitionPublicationResources");
            entity.HasKey(row => new { row.DefinitionId, row.Version, row.ResourceId });
            entity.Property(row => row.DefinitionId).HasMaxLength(128);
            entity.Property(row => row.ResourceId).HasMaxLength(36);
            entity.Property(row => row.LogicalPath).HasMaxLength(240).IsRequired();
            entity.Property(row => row.MediaType).HasMaxLength(64).IsRequired();
            entity.Property(row => row.ContentSha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(row => new { row.DefinitionId, row.Version, row.LogicalPath }).IsUnique();
        });
        modelBuilder.Entity<AdminEventRecord>(entity =>
        {
            entity.ToTable("AdminEvents");
            entity.HasKey(row => row.EventId);
            entity.Property(row => row.EventId).HasMaxLength(36);
            entity.Property(row => row.OperationId).HasMaxLength(36).IsRequired();
            entity.HasIndex(row => row.OperationId).IsUnique();
            entity.Property(row => row.ActorKind).HasMaxLength(32).IsRequired();
            entity.Property(row => row.Operation).HasMaxLength(64).IsRequired();
            entity.Property(row => row.TargetType).HasMaxLength(64).IsRequired();
            entity.Property(row => row.TargetId).HasMaxLength(256).IsRequired();
            entity.Property(row => row.SummaryJson).IsRequired();
            entity.HasIndex(row => new { row.TargetType, row.TargetId, row.OccurredAtUtc });
        });
        modelBuilder.Entity<CredentialRecord>(entity =>
        {
            entity.ToTable("Credentials"); entity.HasKey(c => c.CredentialId);
            entity.Property(c => c.Revision).IsConcurrencyToken();
            entity.Property(c => c.DisplayName).HasMaxLength(120).IsRequired();
            entity.Property(c => c.MetadataJson).HasMaxLength(16384).IsRequired();
            entity.Property(c => c.ProtectedPayload).IsRequired();
        });
        modelBuilder.Entity<AgentCredentialBindingRecord>(entity =>
        {
            entity.ToTable("AgentCredentialBindings"); entity.HasKey(b => b.BindingId);
            entity.Property(b => b.Revision).IsConcurrencyToken();
            entity.Property(b => b.Reference).HasMaxLength(64).IsRequired();
            entity.HasIndex(b => new { b.AgentInstanceId, b.Reference }).IsUnique();
            entity.HasIndex(b => new { b.AgentInstanceId, b.CredentialId }).IsUnique();
            entity.HasOne<AgentInstanceRecord>().WithMany().HasForeignKey(b => b.AgentInstanceId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<CredentialRecord>().WithMany().HasForeignKey(b => b.CredentialId).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private const int TriggerLimitsIntent = 500;
}
