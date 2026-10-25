using System.Security.Cryptography;
using System.Text;

namespace AgentCore.Infrastructure.Security;

internal static class SpaceAutomationPinApprovalPersistence
{
    private static readonly Lazy<string> Frozen = new(() =>
    {
        using var resource = typeof(SpaceAutomationPinApprovalPersistence).Assembly.GetManifestResourceStream(
            "AgentCore.Infrastructure.Persistence.Migrations.Sql.20261025050000_SpaceAutomationPins.Check.sql")
            ?? throw new InvalidOperationException("Frozen Automation pin approval shape is missing.");
        using var memory = new MemoryStream(); resource.CopyTo(memory); var bytes = memory.ToArray();
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != "001fad56bdf4bb94240127e026d82f3197cdb028bb186c14bfd87917aa9770f4") throw new InvalidOperationException("Frozen Automation pin approval shape changed.");
        return Encoding.UTF8.GetString(bytes);
    });
    internal static string ValuesConstraint => Frozen.Value;
}
