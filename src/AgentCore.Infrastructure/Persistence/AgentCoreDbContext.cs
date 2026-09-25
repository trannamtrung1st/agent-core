using AgentCore.Domain.Conversation;
using AgentCore.Domain.Work;
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
    public string? AgentInstanceId { get; set; }
    public string? PinnedPersonaJson { get; set; }
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
    public string? AttachmentRefsJson { get; set; }
    public string? SourceAdmissionFingerprint { get; set; }
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

public sealed class AgentCoreDbContext(DbContextOptions<AgentCoreDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SnapshotRecord> Snapshots => Set<SnapshotRecord>();
    public DbSet<EntryRecord> Entries => Set<EntryRecord>();
    public DbSet<ProfileRecord> Profiles => Set<ProfileRecord>();
    public DbSet<OwnerCapabilityRecord> OwnerCapabilities => Set<OwnerCapabilityRecord>();
    public DbSet<AttachmentRecordRow> Attachments => Set<AttachmentRecordRow>();
    public DbSet<MessageAttachmentRow> MessageAttachments => Set<MessageAttachmentRow>();
    public DbSet<ArtifactRecordRow> Artifacts => Set<ArtifactRecordRow>();
    public DbSet<StructuredMemoryRecord> StructuredMemories => Set<StructuredMemoryRecord>();
    public DbSet<AgentInstanceRecord> AgentInstances => Set<AgentInstanceRecord>();
    public DbSet<TriggerRegistrationRecord> TriggerRegistrations => Set<TriggerRegistrationRecord>();
    public DbSet<TriggerOccurrenceRecord> TriggerOccurrences => Set<TriggerOccurrenceRecord>();
    public DbSet<WorkItemRecord> WorkItems => Set<WorkItemRecord>();
    public DbSet<WorkApprovalRecord> WorkApprovals => Set<WorkApprovalRecord>();
    public DbSet<AgentDefinitionDraftRecord> AgentDefinitionDrafts => Set<AgentDefinitionDraftRecord>();
    public DbSet<AgentDefinitionPublicationRecord> AgentDefinitionPublications => Set<AgentDefinitionPublicationRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36);
            entity.HasIndex(row => row.AgentInstanceId);
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
            entity.HasIndex(row => row.DefinitionId)
                .IsUnique()
                .HasFilter("Compatibility = 1");
        });
        modelBuilder.Entity<TriggerRegistrationRecord>(entity =>
        {
            entity.ToTable("TriggerRegistrations");
            entity.HasKey(row => row.RegistrationId);
            entity.Property(row => row.RegistrationId).HasMaxLength(36);
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ProfileId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.Intent).HasMaxLength(TriggerLimitsIntent).IsRequired();
            entity.Property(row => row.ScheduleJson).HasMaxLength(4000).IsRequired();
            entity.Property(row => row.SourceSessionId).HasMaxLength(36);
            entity.Property(row => row.SourceEventId).HasMaxLength(36);
            entity.Property(row => row.SuspensionReason).HasMaxLength(200);
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.Status });
            entity.HasIndex(row => new { row.Status, row.NextOccurrenceAtUtc, row.RegistrationId });
        });
        modelBuilder.Entity<TriggerOccurrenceRecord>(entity =>
        {
            entity.ToTable("TriggerOccurrences");
            entity.HasKey(row => row.OccurrenceId);
            entity.Property(row => row.OccurrenceId).HasMaxLength(36);
            entity.Property(row => row.DedupeKey).HasMaxLength(200).IsRequired();
            entity.Property(row => row.RegistrationId).HasMaxLength(36);
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ProfileId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.EvidenceJson).HasMaxLength(4096).IsRequired();
            entity.Property(row => row.SourceEventId).HasMaxLength(36);
            entity.Property(row => row.DispositionReason).HasMaxLength(200);
            entity.Property(row => row.ClaimId).HasMaxLength(36);
            entity.Property(row => row.DurableWorkItemId).HasMaxLength(36);
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.DedupeKey }).IsUnique();
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.Disposition });
            entity.HasIndex(row => new { row.Disposition, row.ClaimLeaseExpiresAtUtc });
        });
        modelBuilder.Entity<WorkItemRecord>(entity =>
        {
            entity.ToTable("WorkItems");
            entity.HasKey(row => row.WorkItemId);
            entity.Property(row => row.WorkItemId).HasMaxLength(36);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.Property(row => row.AgentInstanceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ProfileId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.SourceOccurrenceId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.DedupeKey).HasMaxLength(WorkLimits.MaxDedupeKeyCharacters).IsRequired();
            entity.Property(row => row.EvidenceJson).HasMaxLength(WorkLimits.MaxEvidenceBytes).IsRequired();
            entity.Property(row => row.DefinitionId).HasMaxLength(WorkLimits.MaxDefinitionIdCharacters).IsRequired();
            entity.Property(row => row.PersonaName).HasMaxLength(WorkLimits.MaxPersonaNameCharacters).IsRequired();
            entity.Property(row => row.ModelCatalogKey).HasMaxLength(WorkLimits.MaxModelFieldCharacters).IsRequired();
            entity.Property(row => row.ModelProviderAlias).HasMaxLength(WorkLimits.MaxModelFieldCharacters).IsRequired();
            entity.Property(row => row.ModelId).HasMaxLength(WorkLimits.MaxModelFieldCharacters).IsRequired();
            entity.Property(row => row.ModelReasoningEffort).HasMaxLength(WorkLimits.MaxReasoningEffortCharacters);
            entity.Property(row => row.ClaimGeneration).HasMaxLength(36);
            entity.Property(row => row.CurrentApprovalId).HasMaxLength(36);
            entity.Property(row => row.RegistrationId).HasMaxLength(36);
            entity.Property(row => row.SourceSessionId).HasMaxLength(36);
            entity.Property(row => row.SourceEventId).HasMaxLength(36);
            entity.Property(row => row.ProgressSummary).HasMaxLength(WorkLimits.MaxProgressCharacters);
            entity.Property(row => row.FailureCode).HasMaxLength(WorkLimits.MaxFailureCodeCharacters);
            entity.Property(row => row.FailureSummary).HasMaxLength(WorkLimits.MaxFailureSummaryCharacters);
            entity.Property(row => row.KnownEffectSummary).HasMaxLength(WorkLimits.MaxKnownEffectCharacters);
            entity.Property(row => row.ResultText).HasMaxLength(WorkLimits.MaxResultCharacters);
            entity.Property(row => row.CheckpointJson).HasMaxLength(WorkLimits.MaxCheckpointBytes);
            entity.Property(row => row.SideEffectToolCallId).HasMaxLength(128);
            entity.Property(row => row.SideEffectActionHash).HasMaxLength(WorkLimits.ActionHashCharacters);
            entity.HasIndex(row => row.SourceOccurrenceId).IsUnique();
            entity.HasIndex(row => new { row.AgentInstanceId, row.ProfileId, row.CreatedAtUtc });
            entity.HasIndex(row => new { row.Status, row.ClaimLeaseExpiresAtUtc });
            entity.HasIndex(row => new { row.Status, row.NextRetryAtUtc });
        });
        modelBuilder.Entity<WorkApprovalRecord>(entity =>
        {
            entity.ToTable("WorkApprovals");
            entity.HasKey(row => row.ApprovalId);
            entity.Property(row => row.ApprovalId).HasMaxLength(36);
            entity.Property(row => row.Revision).IsConcurrencyToken();
            entity.Property(row => row.WorkItemId).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ExecutionGeneration).HasMaxLength(36).IsRequired();
            entity.Property(row => row.ToolName).HasMaxLength(WorkLimits.MaxToolNameCharacters).IsRequired();
            entity.Property(row => row.PreparedActionJson).HasMaxLength(WorkLimits.MaxPreparedActionBytes).IsRequired();
            entity.Property(row => row.ActionHash).HasMaxLength(WorkLimits.ActionHashCharacters).IsRequired();
            entity.Property(row => row.Preview).HasMaxLength(WorkLimits.MaxPreviewCharacters).IsRequired();
            entity.HasIndex(row => row.WorkItemId);
            entity.HasOne<WorkItemRecord>()
                .WithMany()
                .HasForeignKey(row => row.WorkItemId)
                .OnDelete(DeleteBehavior.Restrict);
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
    }

    private const int TriggerLimitsIntent = 500;
}
