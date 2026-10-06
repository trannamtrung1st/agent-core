using System.Text.Json;
using AgentCore.Application.Continuity;
using AgentCore.Application.Tools;
using AgentCore.Domain.Experience;

namespace AgentCore.Application.Tests;

public sealed class IdentityMaintenanceToolContractTests
{
    [Fact]
    public void Exact_approval_shows_all_eight_sources_and_the_complete_bounded_replacement()
    {
        var ids = Enumerable.Range(0, IdentityMaintenanceLimits.MaxSources).Select(_ => Guid.NewGuid()).ToArray();
        var content = new string('a', 1900) + "tail qualifier";
        var args = JsonSerializer.SerializeToElement(new { sourceMemoryIds = ids, kind = "Preference", subject = "Samples", content });
        var (_, details) = ToolApprovalPreview.Build(ToolCatalog.MemoryConsolidate, args);
        foreach (var id in ids) Assert.Contains(id.ToString(), details["Sources"]);
        Assert.EndsWith("tail qualifier", details["Replacement"]);
        Assert.Contains(content, details["Replacement"]);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(9)]
    public void Source_bound_is_shared_by_runtime_and_model_contract(int count)
    {
        var args = JsonSerializer.SerializeToElement(new { sourceMemoryIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()) });
        Assert.Throws<AgentCore.Application.Sessions.AgentCoreException>(() => IdentityMaintenanceService.ReadIds(args, "sourceMemoryIds"));
        foreach (var tool in new[] { ToolCatalog.MemoryConsolidate, ToolCatalog.ExperienceConsolidate })
        {
            Assert.True(ToolRegistry.TryGet(tool, out var descriptor));
            using var schema = JsonDocument.Parse(descriptor.ModelDefinition.ParametersJson);
            var prop = schema.RootElement.GetProperty("properties").GetProperty(tool == ToolCatalog.MemoryConsolidate ? "sourceMemoryIds" : "sourceExperienceIds");
            Assert.Equal(IdentityMaintenanceLimits.MinSources, prop.GetProperty("minItems").GetInt32());
            Assert.Equal(IdentityMaintenanceLimits.MaxSources, prop.GetProperty("maxItems").GetInt32());
        }
    }
}
