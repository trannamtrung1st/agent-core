using System.Text.Json;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

public sealed class EvaluationSuiteRow
{
    public Guid TenantId { get; set; }
    public Guid SuiteId { get; set; }
    public int Version { get; set; }
    public Guid? DraftId { get; set; }
    public string Json { get; set; } = "";
}
public sealed class EvaluationScriptRow
{
    public Guid TenantId { get; set; }
    public Guid ScriptId { get; set; }
    public int Version { get; set; }
    public string Json { get; set; } = "";
}
public sealed class EvaluationAttemptRow
{
    public Guid TenantId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid ActorId { get; set; }
    public string DedupKey { get; set; } = "";
    public Guid SuiteId { get; set; }
    public int SuiteVersion { get; set; }
    public EvaluationSubjectKind SubjectKind { get; set; }
    public Guid SubjectId { get; set; }
    public long Revision { get; set; }
    public long? CommitSequence { get; set; }
    public EvaluationStatus Status { get; set; }
    public long ExpiresAtUtc { get; set; }
    public string Json { get; set; } = "";
}
public sealed class EvaluationGovernanceRow
{
    public Guid TenantId { get; set; }
    public long Revision { get; set; }
    public string Json { get; set; } = "";
}
internal static class EvaluationPersistence
{
    internal static IReadOnlyList<EvaluationAttempt> CurrentDraftEvidence(IEnumerable<EvaluationSuiteVersion> suites, IEnumerable<EvaluationAttempt> attempts)
    {
        var remaining = suites.SelectMany(s => s.Checks.Select(c => (s.SuiteId, c.CheckId))).ToHashSet();
        var selected = new List<EvaluationAttempt>();
        foreach (var attempt in attempts)
        {
            if (attempt.Findings.Any(f => remaining.Contains((attempt.SuiteId, f.CheckId))))
            {
                selected.Add(attempt);
                foreach (var finding in attempt.Findings) remaining.Remove((attempt.SuiteId, finding.CheckId));
            }
            if (remaining.Count == 0) break;
        }
        return selected;
    }
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    internal static EvaluationSuiteVersion Value(EvaluationSuiteRow row) => JsonSerializer.Deserialize<EvaluationSuiteVersion>(row.Json)!;
    internal static ScriptVersion Value(EvaluationScriptRow row) => JsonSerializer.Deserialize<ScriptVersion>(row.Json)!;
    internal static EvaluationAttempt Value(EvaluationAttemptRow row) => JsonSerializer.Deserialize<EvaluationAttempt>(row.Json)!;
    internal static EvaluationSuiteRow Row(EvaluationSuiteVersion v) => new() { TenantId = v.TenantId, SuiteId = v.SuiteId, Version = v.Version, DraftId = v.DraftId, Json = JsonSerializer.Serialize(v) };
    internal static EvaluationScriptRow Row(ScriptVersion v) => new() { TenantId = v.TenantId, ScriptId = v.ScriptId, Version = v.Version, Json = JsonSerializer.Serialize(v) };
    internal static EvaluationAttemptRow Row(EvaluationAttempt v) => new() { TenantId = v.TenantId, AttemptId = v.AttemptId, ActorId = v.ActorId, DedupKey = v.DedupKey,
        SuiteId = v.SuiteId, SuiteVersion = v.SuiteVersion, SubjectKind = v.SubjectKind, SubjectId = v.SubjectId, Revision = v.Revision, CommitSequence = v.CommitSequence, Status = v.Status, ExpiresAtUtc = v.CreatedAt.AddSeconds(EvaluationRules.TimeoutSeconds + 5).ToUnixTimeMilliseconds(), Json = JsonSerializer.Serialize(v) };
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<EvaluationGovernanceRow>(e => { e.ToTable("TenantEvaluationPolicies"); e.HasKey(r => r.TenantId); e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<EvaluationSuiteRow>(e => {
            e.ToTable("EvaluationSuites"); e.HasKey(r => new { r.TenantId, r.SuiteId, r.Version });
            e.HasIndex(r => new { r.TenantId, r.DraftId, r.Version });
            e.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<EvaluationScriptRow>(e => {
            e.ToTable("EvaluationScripts"); e.HasKey(r => new { r.TenantId, r.ScriptId, r.Version });
            e.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<EvaluationAttemptRow>(e => {
            e.ToTable("EvaluationAttempts"); e.HasKey(r => new { r.TenantId, r.AttemptId });
            e.Property(r => r.Revision).IsConcurrencyToken();
            e.HasIndex(r => new { r.Status, r.ExpiresAtUtc });
            e.HasIndex(r => new { r.TenantId, r.ActorId, r.DedupKey }).IsUnique();
            e.HasIndex(r => new { r.TenantId, r.SubjectKind, r.SubjectId, r.CommitSequence });
            e.HasOne<EvaluationSuiteRow>().WithMany().HasForeignKey(r => new { r.TenantId, r.SuiteId, Version = r.SuiteVersion }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
