using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record CredentialOAuthAttemptRow(Guid AttemptId, Guid TenantId, string CredentialId, Guid AccountId, Guid AuthenticationSessionId,
    Guid PrincipalId, string Purpose, long CredentialRevision, long AuthEpoch, string StateHash, string CorrelationHash, string ProtectedProof,
    string ProviderJson, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, long Revision, CredentialOAuthOutcome Outcome,
    DateTimeOffset? ClaimedAt, DateTimeOffset? CompletedAt)
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<CredentialOAuthAttemptRow>(); row.ToTable("CredentialOAuthAttempts"); row.HasKey(a => a.AttemptId);
        row.Property(a => a.Revision).IsConcurrencyToken(); row.Property(a => a.ProtectedProof).HasMaxLength(65536);
        row.Property(a => a.StateHash).HasMaxLength(64); row.Property(a => a.CorrelationHash).HasMaxLength(64); row.Property(a => a.ProviderJson).HasMaxLength(8192);
        row.HasIndex(a => new { a.TenantId, a.CredentialId }).IsUnique().HasFilter("Outcome IN (0, 1)");
        row.HasIndex(a => new { a.ExpiresAt, a.Outcome });
        row.HasOne<TenantCredentialRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.CredentialId })
            .HasPrincipalKey(c => new { c.TenantId, c.CredentialId }).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<TenantUser>().WithMany().HasForeignKey(a => new { a.TenantId, a.PrincipalId, a.AccountId })
            .HasPrincipalKey(u => new { u.TenantId, u.PrincipalId, u.AccountId }).OnDelete(DeleteBehavior.Restrict);
        row.HasOne<AuthenticationSession>().WithMany().HasForeignKey(a => new { a.AuthenticationSessionId, a.AccountId })
            .HasPrincipalKey(s => new { s.SessionId, s.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
    internal static CredentialOAuthAttemptRow Map(CredentialOAuthAttempt a) => new(a.AttemptId, a.TenantId, a.CredentialId.ToString("D"), a.AccountId,
        a.AuthenticationSessionId, a.PrincipalId, a.Purpose.ToString(), a.CredentialRevision, a.AuthEpoch, a.StateHash, a.CorrelationHash, a.ProtectedProof,
        JsonSerializer.Serialize(a.Provider), a.CreatedAt, a.ExpiresAt, a.Revision, a.Outcome, a.ClaimedAt, a.CompletedAt);
    internal CredentialOAuthAttempt Domain() => new(AttemptId, TenantId, Guid.Parse(CredentialId), Enum.Parse<AgentCore.Domain.Credentials.CredentialPurpose>(Purpose),
        AccountId, AuthenticationSessionId, PrincipalId, CredentialRevision, AuthEpoch, StateHash, CorrelationHash, ProtectedProof,
        JsonSerializer.Deserialize<CredentialOAuthDescriptor>(ProviderJson)!, CreatedAt, ExpiresAt, Revision, Outcome, ClaimedAt, CompletedAt);
}
