using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentCore.Infrastructure.Security;

internal sealed record SpaceRecord(Guid TenantId, Guid SpaceId, SpaceKind Kind, string? OwnerInstanceId, string Name,
    string Description, string Instructions, bool EnvironmentsEnabled, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? DeletedAt, long EnvironmentSelectionEpoch)
{
    internal static SpaceRecord Row(Space s) => new(s.TenantId, s.SpaceId, s.Kind, s.OwnerInstanceId?.ToString("D"), s.Name, s.Description,
        s.Instructions, s.EnvironmentsEnabled, s.Revision, s.CreatedAt, s.UpdatedAt, s.DeletedAt, s.EnvironmentSelectionEpoch);
    internal Space Domain() => new(TenantId, SpaceId, Kind, OwnerInstanceId is null ? null : Guid.Parse(OwnerInstanceId), Name, Description,
        Instructions, EnvironmentsEnabled, Revision, CreatedAt, UpdatedAt, DeletedAt, EnvironmentSelectionEpoch);
}

internal sealed record SpaceMembershipRecord(Guid TenantId, Guid SpaceId, string InstanceId, string Responsibility, string Instructions,
    long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt)
{
    internal static SpaceMembershipRecord Row(SpaceMembership value) => new(value.TenantId, value.SpaceId, value.InstanceId.ToString("D"),
        value.Responsibility, value.Instructions, value.Revision, value.CreatedAt, value.UpdatedAt);
    internal SpaceMembership Domain(IEnumerable<SpaceDisabledBindingRecord> disabled, IEnumerable<SpaceAddedBindingRecord> added) =>
        new SpaceMembership(TenantId, SpaceId, Guid.Parse(InstanceId), Responsibility, Instructions, disabled.Select(b => b.BindingKey).ToArray(),
            added.Select(b => new SpaceResourceBinding(b.Kind, b.ResourceKey, b.AccountRef)).ToArray(), Revision, CreatedAt, UpdatedAt).Freeze();
}
internal sealed record SpaceDisabledBindingRecord(Guid TenantId, Guid SpaceId, string InstanceId, string BindingKey);
internal sealed record SpaceAddedBindingRecord(Guid TenantId, Guid SpaceId, string InstanceId, SpaceBindingKind Kind, string ResourceKey, string? AccountRef);

internal static class TenantSpaceRecords
{
    internal static void Configure(ModelBuilder model)
    {
        SpaceFileResourcePersistence.Configure(model);
        var spaces = model.Entity<SpaceRecord>();
        spaces.ToTable("Spaces", t => t.HasCheckConstraint("CK_Spaces_Owner", "Revision >= 1 AND ((Kind = 0 AND OwnerInstanceId IS NOT NULL) OR (Kind = 1 AND OwnerInstanceId IS NULL)) AND length(Name) BETWEEN 1 AND 160 AND length(Instructions) <= 16384 AND EnvironmentsEnabled IN (0,1)"));
        spaces.HasKey(s => new { s.TenantId, s.SpaceId });
        spaces.HasIndex(s => new { s.TenantId, s.OwnerInstanceId }).IsUnique().HasFilter("OwnerInstanceId IS NOT NULL");
        spaces.Property(s => s.Revision).IsConcurrencyToken();
        spaces.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).OnDelete(DeleteBehavior.Restrict);
        // Ownership is tenant-qualified by the same SystemIdentity used by Instance admission.
        spaces.HasOne<IamSystemIdentityRow>().WithMany().HasForeignKey("TenantId", "OwnerInstanceId").HasPrincipalKey(i => new { i.TenantId, i.AgentInstanceId }).OnDelete(DeleteBehavior.Restrict);
        var members = model.Entity<SpaceMembershipRecord>();
        members.ToTable("SpaceMemberships", t => t.HasCheckConstraint("CK_SpaceMemberships_Values", "Revision >= 1 AND length(Instructions) <= 16384 AND length(Responsibility) <= 160"));
        members.HasKey(m => new { m.TenantId, m.SpaceId, m.InstanceId }); members.Property(m => m.Revision).IsConcurrencyToken();
        members.HasOne<SpaceRecord>().WithMany().HasForeignKey(m => new { m.TenantId, m.SpaceId }).OnDelete(DeleteBehavior.Restrict);
        members.HasOne<IamSystemIdentityRow>().WithMany().HasForeignKey(m => new { m.TenantId, m.InstanceId }).HasPrincipalKey(i => new { i.TenantId, i.AgentInstanceId }).OnDelete(DeleteBehavior.Restrict);
        var disabled = model.Entity<SpaceDisabledBindingRecord>(); disabled.ToTable("SpaceMembershipDisabledBindings");
        disabled.HasKey(m => new { m.TenantId, m.SpaceId, m.InstanceId, m.BindingKey });
        disabled.HasOne<SpaceMembershipRecord>().WithMany().HasForeignKey(m => new { m.TenantId, m.SpaceId, m.InstanceId }).OnDelete(DeleteBehavior.Cascade);
        var added = model.Entity<SpaceAddedBindingRecord>();
        added.ToTable("SpaceMembershipAddedBindings", t => t.HasCheckConstraint("CK_SpaceBindings_Kind", "Kind BETWEEN 0 AND 4 AND length(ResourceKey) BETWEEN 1 AND 256 AND ((Kind = 3 AND AccountRef IS NOT NULL) OR (Kind <> 3 AND AccountRef IS NULL))"));
        added.HasKey(m => new { m.TenantId, m.SpaceId, m.InstanceId, m.Kind, m.ResourceKey });
        added.HasIndex(m => new { m.TenantId, m.SpaceId, m.InstanceId, m.AccountRef }).IsUnique().HasFilter("AccountRef IS NOT NULL");
        added.HasOne<SpaceMembershipRecord>().WithMany().HasForeignKey(m => new { m.TenantId, m.SpaceId, m.InstanceId }).OnDelete(DeleteBehavior.Cascade);
        foreach (var type in new[] { typeof(SpaceRecord), typeof(SpaceMembershipRecord) })
        foreach (var property in model.Entity(type).Metadata.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(new ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        }
    }
}
