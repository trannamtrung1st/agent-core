using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class SyntheticSelfManagementTests
{
    [Theory]
    [InlineData("synthetic-tenant-management-read: policy", false)]
    [InlineData("synthetic-tenant-management-read: policy", true)]
    [InlineData("synthetic-tenant-management-mutation: {\"tool\":\"management.freeze\",\"arguments\":{\"expectedInstanceRevision\":1}}", false)]
    [InlineData("synthetic-tenant-management-mutation: {\"tool\":\"management.freeze\",\"arguments\":{\"expectedInstanceRevision\":1}}", true)]
    public async Task Management_scripts_select_only_the_latest_user_task(string instruction, bool newOrdinaryTask)
    {
        var messages = new List<ModelMessage> { new(ModelRole.User, instruction) };
        if (newOrdinaryTask)
        {
            messages.Add(new(ModelRole.Assistant, "The prior management task finished."));
            messages.Add(new(ModelRole.User, "Continue ordinary conversation while management is frozen."));
        }
        var request = new ModelRequest(Guid.NewGuid(), messages, Tools: [new(ToolCatalog.CapabilitiesLoad, "Discover", "{}")]);
        var events = new List<ModelGenerationEvent>();
        await foreach (var item in new ScriptedLanguageModel(["Ordinary response."]).GenerateAsync(request)) events.Add(item);
        if (newOrdinaryTask)
        {
            Assert.Empty(events.OfType<ModelToolCallEvent>());
            Assert.Equal("Ordinary response.", string.Concat(events.OfType<ModelTextDelta>().Select(item => item.Text)));
        }
        else Assert.Equal(ToolCatalog.CapabilitiesLoad, Assert.Single(events.OfType<ModelToolCallEvent>()).Call.Name);
    }
}
