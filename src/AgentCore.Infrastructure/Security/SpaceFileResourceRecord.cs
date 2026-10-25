using AgentCore.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AgentCore.Infrastructure.Security;

internal static class SpaceFileResourcePersistence
{
    internal static void Configure(ModelBuilder model)
    {
        var resource = model.Entity<SpaceFileResource>();
        resource.ToTable("SpaceFileResources", table => table.HasCheckConstraint("CK_SpaceFileResources_Values",
            "Revision >= 1 AND Kind BETWEEN 1 AND 4 AND ByteLength BETWEEN 1 AND 8388608 AND length(LogicalPath) BETWEEN 1 AND 240 AND substr(LogicalPath,1,1) <> '/' AND instr(LogicalPath,char(92))=0 AND instr(LogicalPath,':')=0 AND instr('/'||LogicalPath||'/','/../')=0 AND instr('/'||LogicalPath||'/','/./')=0 AND instr(LogicalPath,'//')=0 AND substr(LogicalPath,-1)<>'/' AND length(LogicalPath)=length(trim(LogicalPath)) AND length(MediaType) BETWEEN 1 AND 256 AND length(ContentSha256)=64 AND ContentSha256 NOT GLOB '*[^0-9a-f]*' AND substr(SourcePath,1,6)='/home/' AND length(SourcePath) BETWEEN 7 AND 4095 AND instr(SourcePath,char(92))=0 AND instr(SourcePath,'/../')=0 AND instr(SourcePath,'/./')=0 AND instr(SourcePath,'//')=0 AND substr(SourcePath,-1)<>'/' AND UpdatedAt >= CreatedAt AND (DeletedAt IS NULL OR DeletedAt >= CreatedAt)"));
        resource.HasKey(r => new { r.TenantId, r.SpaceId, r.ResourceId }); resource.Property(r => r.Revision).IsConcurrencyToken();
        resource.HasIndex(r => new { r.TenantId, r.SpaceId, r.LogicalPath }).IsUnique().HasFilter("DeletedAt IS NULL");
        resource.HasOne<SpaceRecord>().WithMany().HasForeignKey(r => new { r.TenantId, r.SpaceId }).OnDelete(DeleteBehavior.Restrict);
        resource.Ignore(r => r.Origin); resource.Ignore(r => r.Key);
        foreach (var property in resource.Metadata.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset)) property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)));
            if (property.ClrType == typeof(DateTimeOffset?)) property.SetValueConverter(new ValueConverter<DateTimeOffset?, long?>(v => v.HasValue ? v.Value.ToUnixTimeMilliseconds() : null, v => v.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(v.Value) : null));
        }
    }
}
