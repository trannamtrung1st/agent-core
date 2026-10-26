using System.Text.Json;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentCore.Infrastructure.Persistence;

public sealed record ModelPresetRecord(Guid PresetId, ModelProviderKind Kind, string ExternalModelId, string Label,
    ModelTransport Transport, string CapabilitiesJson, bool Enabled, bool Archived, long Revision);
public sealed record ProviderConnectionRecord(Guid ConnectionId, Guid TenantId, ModelProviderKind Kind, string Label,
    string Endpoint, string? CredentialId, bool NoCredential, bool Enabled, long Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastTestAt, string? LastTestResult);
public sealed record ModelOfferingRecord(Guid OfferingId, Guid TenantId, Guid ConnectionId, ModelProviderKind Kind,
    Guid? PresetId, string ExternalModelId, string Label, ModelTransport Transport, string CapabilitiesJson,
    bool Enabled, bool Retired, long Revision, ModelObservation Observation, DateTimeOffset? LastSeenAt);
public sealed record TenantModelSettingsRecord(Guid TenantId, Guid? DefaultOfferingId, string? DefaultReasoningEffort, long Revision, string? RoutingJson = null);

internal static class ModelManagementPersistence
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static ModelPresetRecord Row(ModelPreset p) => new(p.PresetId, p.Kind, p.ExternalModelId, p.Label, p.Transport, JsonSerializer.Serialize(p.Capabilities, Json), p.Enabled, p.Archived, p.Revision);
    internal static ProviderConnectionRecord Row(ProviderConnection p) => new(p.ConnectionId, p.TenantId, p.Kind, p.Label, p.Endpoint, p.CredentialId?.ToString("D"), p.NoCredential, p.Enabled, p.Revision, p.CreatedAt, p.UpdatedAt, p.LastTestAt, p.LastTestResult);
    internal static ModelOfferingRecord Row(ModelOffering p) => new(p.OfferingId, p.TenantId, p.ConnectionId, p.Kind, p.PresetId, p.ExternalModelId, p.Label, p.Transport, JsonSerializer.Serialize(p.Capabilities, Json), p.Enabled, p.Retired, p.Revision, p.Observation, p.LastSeenAt);
    internal static TenantModelSettingsRecord Row(TenantModelSettings p) => new(p.TenantId, p.DefaultOfferingId, p.DefaultReasoningEffort, p.Revision, p.Routing is null ? null : JsonSerializer.Serialize(p.Routing, Json));
    internal static ModelPreset Domain(ModelPresetRecord p) => ModelManagementRules.Freeze(new ModelPreset(p.PresetId, p.Kind, p.ExternalModelId, p.Label, p.Transport, Capabilities(p.CapabilitiesJson), p.Enabled, p.Archived, p.Revision));
    internal static ProviderConnection Domain(ProviderConnectionRecord p) => ModelManagementRules.Freeze(new ProviderConnection(p.ConnectionId, p.TenantId, p.Kind, p.Label, p.Endpoint, p.CredentialId is null ? null : Guid.Parse(p.CredentialId), p.NoCredential, p.Enabled, p.Revision, p.CreatedAt, p.UpdatedAt, p.LastTestAt, p.LastTestResult));
    internal static ModelOffering Domain(ModelOfferingRecord p) => ModelManagementRules.Freeze(new ModelOffering(p.OfferingId, p.TenantId, p.ConnectionId, p.Kind, p.PresetId, p.ExternalModelId, p.Label, p.Transport, Capabilities(p.CapabilitiesJson), p.Enabled, p.Retired, p.Revision, p.Observation, p.LastSeenAt));
    internal static TenantModelSettings Domain(TenantModelSettingsRecord p) => ModelManagementRules.Freeze(new TenantModelSettings(p.TenantId, p.DefaultOfferingId, p.DefaultReasoningEffort, p.Revision, p.RoutingJson is null ? null : JsonSerializer.Deserialize<AutoRoutingPolicy>(p.RoutingJson, Json)));
    private static ModelCapabilityProfile Capabilities(string json) => ModelManagementRules.Freeze(JsonSerializer.Deserialize<ModelCapabilityProfile>(json, Json) ?? throw new InvalidOperationException("Invalid persisted model capabilities."));

    internal static void Configure(ModelBuilder model)
    {
        var p = model.Entity<ModelPresetRecord>();
        p.ToTable("ModelPresets", t => t.HasCheckConstraint("CK_ModelPreset_Values", "Revision>=1 AND Kind BETWEEN 0 AND 4 AND Transport BETWEEN 0 AND 3 AND length(ExternalModelId) BETWEEN 1 AND 255 AND length(Label) BETWEEN 1 AND 120 AND json_valid(CapabilitiesJson)"));
        p.HasData(AgentCore.Application.Models.BuiltinModelPresets.All.Select(Row));
        p.HasKey(r => r.PresetId); p.HasAlternateKey(r => new { r.PresetId, r.Kind, r.ExternalModelId });
        p.HasIndex(r => new { r.Kind, r.ExternalModelId }).IsUnique(); p.Property(r => r.Revision).IsConcurrencyToken();
        var c = model.Entity<ProviderConnectionRecord>();
        c.ToTable("ProviderConnections", t => t.HasCheckConstraint("CK_ProviderConnection_Values", "Revision>=1 AND Kind BETWEEN 0 AND 4 AND length(Label) BETWEEN 1 AND 120 AND length(Endpoint) BETWEEN 1 AND 2048 AND ((NoCredential=1 AND CredentialId IS NULL AND Kind=4) OR (NoCredential=0 AND CredentialId IS NOT NULL))"));
        c.Property(r => r.CreatedAt).HasConversion(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        c.Property(r => r.UpdatedAt).HasConversion(value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value));
        c.Property(r => r.LastTestAt).HasConversion(new ValueConverter<DateTimeOffset?, long?>(value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null, value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null));
        c.HasKey(r => r.ConnectionId); c.HasAlternateKey(r => new { r.TenantId, r.ConnectionId, r.Kind });
        c.Property(r => r.Revision).IsConcurrencyToken();
        c.HasOne<Tenant>().WithMany().HasForeignKey(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        c.HasOne<TenantCredentialRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.CredentialId }).HasPrincipalKey(r => new { r.TenantId, r.CredentialId }).OnDelete(DeleteBehavior.Restrict);
        c.HasOne<CredentialRecord>().WithMany().HasForeignKey(r => r.CredentialId).OnDelete(DeleteBehavior.Restrict);
        var o = model.Entity<ModelOfferingRecord>();
        o.ToTable("ModelOfferings", t => t.HasCheckConstraint("CK_ModelOffering_Values", "Revision>=1 AND Kind BETWEEN 0 AND 4 AND Transport BETWEEN 0 AND 3 AND Observation BETWEEN 0 AND 2 AND NOT(Retired=1 AND Enabled=1) AND length(ExternalModelId) BETWEEN 1 AND 255 AND length(Label) BETWEEN 1 AND 120 AND json_valid(CapabilitiesJson)"));
        o.Property(r => r.LastSeenAt).HasConversion(new ValueConverter<DateTimeOffset?, long?>(value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null, value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null));
        o.HasKey(r => r.OfferingId); o.HasAlternateKey(r => new { r.TenantId, r.OfferingId });
        o.Property(r => r.Revision).IsConcurrencyToken(); o.HasIndex(r => new { r.TenantId, r.ConnectionId, r.ExternalModelId }).IsUnique();
        o.HasOne<ProviderConnectionRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.ConnectionId, r.Kind }).HasPrincipalKey(r => new { r.TenantId, r.ConnectionId, r.Kind }).OnDelete(DeleteBehavior.Restrict);
        o.HasOne<ModelPresetRecord>().WithMany().HasForeignKey(r => new { r.PresetId, r.Kind, r.ExternalModelId }).HasPrincipalKey(r => new { r.PresetId, r.Kind, r.ExternalModelId }).OnDelete(DeleteBehavior.Restrict);
        var s = model.Entity<TenantModelSettingsRecord>();
        s.ToTable("TenantModelSettings", t => t.HasCheckConstraint("CK_TenantModelSettings_Values", "Revision>=1 AND (DefaultOfferingId IS NOT NULL OR DefaultReasoningEffort IS NULL)"));
        s.HasKey(r => r.TenantId); s.Property(r => r.Revision).IsConcurrencyToken();
        s.HasOne<Tenant>().WithOne().HasForeignKey<TenantModelSettingsRecord>(r => r.TenantId).OnDelete(DeleteBehavior.Restrict);
        s.HasOne<ModelOfferingRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.DefaultOfferingId }).HasPrincipalKey(r => new { r.TenantId, r.OfferingId }).OnDelete(DeleteBehavior.Restrict);
    }
}
