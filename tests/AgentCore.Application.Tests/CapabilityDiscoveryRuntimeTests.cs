using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Browser;
using Microsoft.Playwright;

namespace AgentCore.Application.Tests;

public sealed partial class TerminalDisplayRepairTests
{
    [Theory]
    [InlineData("[]")]
    [InlineData("{broken")]
    public async Task Invalid_discovery_returns_distinct_actionable_receipt_without_admitting_load(string arguments)
    {
        await using var runtime = CreateCore(new InvalidDiscoveryModel(arguments), null, null, [],
            environment: new RoleEnvironment(Capabilities: new("Selected", [ToolCatalog.CapabilitiesLoad]), Projection: new([])));
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Discover a tool."));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal(EntryStatus.Completed, runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant).Status);
        var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
        Assert.Empty(run.LoadedCapabilityIds);
        Assert.Equal(0, run.CapabilityLoadCount);
    }

    private sealed class InvalidDiscoveryModel(string arguments) : ILanguageModel
    {
        private bool _called;
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (!_called)
            {
                _called = true;
                yield return new ModelToolCallEvent(new("invalid-discovery", ToolCatalog.CapabilitiesLoad, arguments));
                yield return new ModelCompleted(ModelStopReason.ToolCalls);
            }
            else
            {
                using var receipt = JsonDocument.Parse(request.Messages.Last(m => m.Role == ModelRole.Tool).Text);
                Assert.Equal("load_invalid", receipt.RootElement.GetProperty("outcome").GetString());
                Assert.Equal("invalid", receipt.RootElement.GetProperty("error").GetString());
                Assert.Contains("query", receipt.RootElement.GetProperty("nextStep").GetString());
                yield return new ModelSemanticResponseReady(new("Discovery arguments were invalid; no capability was loaded.", new(ModelSpeechMode.Same, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
            await Task.CompletedTask;
        }
    }

}
