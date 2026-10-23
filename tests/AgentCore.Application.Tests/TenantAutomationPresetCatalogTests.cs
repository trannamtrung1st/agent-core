using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAutomationPresetCatalogTests
{
    [Theory]
    [InlineData("improve-harness", "C1DF57B069D8574593DB9E21393FFF6A7C5939C73103A98114BA81BD45FAA123")]
    [InlineData("consolidate-continuity", "9C5FDB5516D79B7C37E2CE37DD534F70017E8F09F2A4825CABDDEF0FB21F1730")]
    [InlineData("review-recent-work", "9DE94967611E58921BDF7A1A1FAD1262559679988089A2752208EA24A4F517D2")]
    public void Immutable_preset_content_survives_global_catalog_retirement(string id, string expected)
    {
        var preset = TenantAutomationPresetRules.Template(id, 1)!;
        var content = string.Join("\n", preset.PresetId, preset.PresetVersion, preset.Name, preset.Description, preset.Instructions);
        Assert.Equal(expected, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))));
        Assert.Equal(3, TenantAutomationPresetRules.Templates.Count);
    }
}
