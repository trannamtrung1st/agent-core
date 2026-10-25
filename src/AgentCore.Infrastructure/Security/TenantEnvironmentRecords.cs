using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SpaceEnvironment = AgentCore.Domain.Spaces.Environment;

namespace AgentCore.Infrastructure.Security;

internal sealed record SpaceEnvironmentRecord(Guid TenantId, Guid SpaceId, Guid EnvironmentId, string Name, string NormalizedName,
    string Description, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt)
{
    internal static SpaceEnvironmentRecord Row(SpaceEnvironment e) => new(e.TenantId, e.SpaceId, e.EnvironmentId, e.Name, e.NormalizedName, e.Description, e.Revision, e.CreatedAt, e.UpdatedAt, e.DeletedAt);
    internal SpaceEnvironment Domain() => new(TenantId, SpaceId, EnvironmentId, Name, Description, Revision, CreatedAt, UpdatedAt, DeletedAt);
}
internal sealed record EnvironmentVariableRecord(Guid TenantId, Guid SpaceId, Guid EnvironmentId, string Name, EnvironmentVariableKind Kind,
    string Description, string? PlainValue, Guid? SecretId, string? CredentialId, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    internal static EnvironmentVariableRecord Row(EnvironmentVariable v) => new(v.TenantId, v.SpaceId, v.EnvironmentId, v.Name, v.Kind, v.Description, v.PlainValue, v.SecretId, v.CredentialId?.ToString("D"), v.Revision, v.CreatedAt, v.UpdatedAt);
    internal EnvironmentVariable Domain() => new(TenantId, SpaceId, EnvironmentId, Name, Kind, Description, PlainValue, SecretId, CredentialId is null ? null : Guid.Parse(CredentialId), Revision, CreatedAt, UpdatedAt);
}
internal static class TenantEnvironmentRecords
{
    internal static void Configure(ModelBuilder model)
    {
        var secret = model.Entity<Secret>();
        secret.ToTable("TenantSecrets", t => t.HasCheckConstraint("CK_TenantSecrets_Values", "Status IN (0,1,2) AND Revision>=1 AND ProtectionVersion>=1 AND length(Name) BETWEEN 1 AND 120 AND length(Description)<=2048 AND ((Status=2 AND ProtectedPayload='') OR (Status<>2 AND length(ProtectedPayload) BETWEEN 1 AND 131072))"));
        secret.HasKey(s => new { s.TenantId, s.SecretId }); secret.Property(s => s.Revision).IsConcurrencyToken();
        secret.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        var env = model.Entity<SpaceEnvironmentRecord>();
        env.ToTable("SpaceEnvironments", t => t.HasCheckConstraint("CK_SpaceEnvironments_Values", "Revision>=1 AND length(Name) BETWEEN 1 AND 120 AND length(Description)<=2048 AND length(NormalizedName) BETWEEN 1 AND 240"));
        env.HasKey(e => new { e.TenantId, e.SpaceId, e.EnvironmentId }); env.Property(e => e.Revision).IsConcurrencyToken();
        env.HasIndex(e => new { e.TenantId, e.SpaceId, e.NormalizedName }).IsUnique().HasFilter("DeletedAt IS NULL");
        env.HasOne<SpaceRecord>().WithMany().HasForeignKey(e => new { e.TenantId, e.SpaceId }).OnDelete(DeleteBehavior.Restrict);
        var variable = model.Entity<EnvironmentVariableRecord>();
        variable.ToTable("EnvironmentVariables", t => t.HasCheckConstraint("CK_EnvironmentVariables_Values", "Revision>=1 AND length(Name) BETWEEN 1 AND 64 AND Name=upper(Name) AND Name NOT GLOB '*[^A-Z0-9_]*' AND substr(Name,1,1) GLOB '[A-Z_]' AND length(Description)<=1024 AND ((Kind=0 AND PlainValue IS NOT NULL AND length(CAST(PlainValue AS BLOB))<=4096 AND SecretId IS NULL AND CredentialId IS NULL) OR (Kind=1 AND PlainValue IS NULL AND SecretId IS NOT NULL AND CredentialId IS NULL) OR (Kind=2 AND PlainValue IS NULL AND SecretId IS NULL AND CredentialId IS NOT NULL))"));
        variable.HasKey(v => new { v.TenantId, v.EnvironmentId, v.Name }); variable.Property(v => v.Revision).IsConcurrencyToken();
        variable.HasOne<SpaceEnvironmentRecord>().WithMany().HasForeignKey(v => new { v.TenantId, v.SpaceId, v.EnvironmentId }).OnDelete(DeleteBehavior.Restrict);
        variable.HasOne<Secret>().WithMany().HasForeignKey(v => new { v.TenantId, v.SecretId }).OnDelete(DeleteBehavior.Restrict);
        variable.HasOne<TenantCredentialRecord>().WithMany().HasForeignKey(v => new { v.TenantId, v.CredentialId }).HasPrincipalKey(c => new { c.TenantId, c.CredentialId }).OnDelete(DeleteBehavior.Restrict);
        foreach (var type in new[] { typeof(Secret), typeof(SpaceEnvironmentRecord), typeof(EnvironmentVariableRecord) })
        foreach (var property in model.Entity(type).Metadata.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(new ValueConverter<DateTimeOffset,long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(new ValueConverter<DateTimeOffset?,long?>(v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        }
    }
}
