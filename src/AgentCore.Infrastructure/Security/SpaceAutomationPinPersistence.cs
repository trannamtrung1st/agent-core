using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

internal static class SpaceAutomationPinPersistence
{
    private const string Shape = "(PinnedSpaceId IS NULL AND PinnedEnvironmentId IS NULL) OR (TenantId IS NOT NULL AND PinnedSpaceId IS NOT NULL AND lower(PinnedSpaceId) <> '00000000-0000-0000-0000-000000000000' AND (PinnedEnvironmentId IS NULL OR lower(PinnedEnvironmentId) <> '00000000-0000-0000-0000-000000000000'))";
    internal static void Configure(ModelBuilder model)
    {
        var automation = model.Entity<AutomationRecord>();
        automation.ToTable("Automations", table => table.HasCheckConstraint("CK_Automations_SpacePins", Shape));
        automation.HasOne<SpaceRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.PinnedSpaceId })
            .HasPrincipalKey(s => new { s.TenantId, s.SpaceId }).OnDelete(DeleteBehavior.Restrict);
        automation.HasOne<SpaceEnvironmentRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.PinnedSpaceId, a.PinnedEnvironmentId })
            .HasPrincipalKey(e => new { e.TenantId, e.SpaceId, e.EnvironmentId }).OnDelete(DeleteBehavior.Restrict);
        var occurrence = model.Entity<TriggerOccurrenceRecord>();
        occurrence.ToTable("TriggerOccurrences", table => table.HasCheckConstraint("CK_TriggerOccurrences_SpacePins", Shape));
        occurrence.HasOne<SpaceRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.PinnedSpaceId })
            .HasPrincipalKey(s => new { s.TenantId, s.SpaceId }).OnDelete(DeleteBehavior.Restrict);
        occurrence.HasOne<SpaceEnvironmentRecord>().WithMany().HasForeignKey(a => new { a.TenantId, a.PinnedSpaceId, a.PinnedEnvironmentId })
            .HasPrincipalKey(e => new { e.TenantId, e.SpaceId, e.EnvironmentId }).OnDelete(DeleteBehavior.Restrict);
    }
}
