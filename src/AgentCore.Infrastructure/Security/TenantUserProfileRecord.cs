using System.Text.Json;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed record TenantUserProfileRecord(Guid TenantId, Guid PrincipalId, Guid AccountId, long Revision, string PreferencesJson, long UpdatedAtUtc)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<TenantUserProfileRecord>(); row.ToTable("TenantUserProfiles", t => {
            t.HasCheckConstraint("CK_TenantUserProfiles_Revision", "Revision > 0 AND UpdatedAtUtc >= 0");
            t.HasCheckConstraint("CK_TenantUserProfiles_Preferences", "length(CAST(PreferencesJson AS BLOB)) <= 4096 AND CASE WHEN json_valid(PreferencesJson) THEN json_type(PreferencesJson) = 'object' ELSE 0 END");
        });
        row.HasKey(p => new { p.TenantId, p.PrincipalId }); row.Property(p => p.Revision).IsConcurrencyToken();
        row.HasOne<TenantUser>().WithMany().HasForeignKey(p => new { p.TenantId, p.PrincipalId, p.AccountId })
            .HasPrincipalKey(u => new { u.TenantId, u.PrincipalId, u.AccountId }).OnDelete(DeleteBehavior.Restrict);
    }
    internal static TenantUserProfileRecord Row(TenantUserProfile value)
    {
        value = value.Freeze();
        return new(value.TenantId, value.PrincipalId, value.AccountId, value.Revision, JsonSerializer.Serialize(value.Preferences, Json), value.UpdatedAt.ToUnixTimeMilliseconds());
    }
    internal TenantUserProfile Domain() => new TenantUserProfile(TenantId, PrincipalId, AccountId, Revision,
        JsonSerializer.Deserialize<Dictionary<string, UserProfileValue>>(PreferencesJson, Json) ?? throw new JsonException("Invalid User Profile."),
        DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtUtc)).Freeze();
}
