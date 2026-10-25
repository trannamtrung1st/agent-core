using System.Security.Cryptography;
using System.Text;
namespace AgentCore.Infrastructure.Security;
internal static class SpaceResourceApprovalPersistence
{
    private static readonly Lazy<string> Frozen = new(() => {
        using var stream = typeof(SpaceResourceApprovalPersistence).Assembly.GetManifestResourceStream("AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025080000_SpaceResourceApprovals.Check.sql")
            ?? throw new InvalidOperationException("Frozen Space source approval shape is missing.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); var bytes = memory.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != "1daabf0b11bf24b79fde9bc217bd149d011a8c60d0a3ed1803073da658cbdb90") throw new InvalidOperationException("Frozen Space source approval shape changed.");
        return Encoding.UTF8.GetString(bytes);
    });
    internal static string ValuesConstraint => Frozen.Value;
}
