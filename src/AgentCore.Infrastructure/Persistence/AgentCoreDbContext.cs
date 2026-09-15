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
    public long CreatedAtUtc { get; set; }
    public long UpdatedAtUtc { get; set; }
    public long Revision { get; set; }
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

public sealed class AgentCoreDbContext(DbContextOptions<AgentCoreDbContext> options) : DbContext(options)
{
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<SnapshotRecord> Snapshots => Set<SnapshotRecord>();
    public DbSet<EntryRecord> Entries => Set<EntryRecord>();
    public DbSet<ProfileRecord> Profiles => Set<ProfileRecord>();

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
    }
}
