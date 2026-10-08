using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Tools;
namespace AgentCore.Application.Tests;
public sealed class BrowserV2ContractTests
{
    [Fact]
    public void Focused_catalog_preserves_authority_and_feature_metadata()
    {
        foreach(var metadata in BrowserToolCatalog.Tools.Values)
        {
            var tool=ToolRegistry.Get(metadata.Name);
            using var doc=JsonDocument.Parse(tool.ModelDefinition.ParametersJson);
            Assert.False(doc.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.Equal(metadata.Effect,tool.Effect);
            Assert.Equal(ToolResourceScope.Session,tool.Scope);
            Assert.Equal(metadata.Feature == BrowserFeature.Close ? ToolReplaySafety.IntegrationIdempotent : ToolReplaySafety.NonReplayable,tool.ReplaySafety);
            Assert.DoesNotContain("oneOf",tool.ModelDefinition.ParametersJson);
        }
        foreach(var retired in new[]{"observe","act","pages","capture"}) Assert.False(ToolRegistry.TryGet("browser."+retired,out _));
        Assert.Equal(ToolEffect.SensitiveWrite,ToolCatalog.EffectOf("browser.route"));
        Assert.Equal(ToolEffect.SensitiveWrite,ToolCatalog.EffectOf("browser.local_storage"));
        Assert.Contains("diagnostics",ToolRegistry.Get("browser.console_messages").Tags);
    }
    [Fact]
    public async Task Subset_provider_filters_unsupported_tools_without_losing_navigation()
    {
        var subset=new Subset();var gate=new ToolConfigurationGate(null,null,null,subset,true);
        Assert.True(gate.IsConfigured("browser.navigate"));Assert.False(gate.IsConfigured("browser.trace"));
        var result=await subset.ExecuteAsync(new(Guid.NewGuid(),"browser.trace",JsonSerializer.SerializeToElement(new{operation="start"})));
        Assert.Equal("unsupported_operation",result.ErrorCode);
        Assert.Null((await subset.NavigateAsync(new(Guid.NewGuid(),new Uri("http://127.0.0.1/")))).ErrorCode);
        var definition = Definition("browser.navigate", "browser.trace");
        var executor = new SessionToolExecutor(browser: subset, configurationGate: gate);
        var forced = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("trace", "browser.trace", "{\"operation\":\"start\"}"),
            ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("unsupported_operation", forced.Text);
        var unauthorized = await executor.ExecuteAsync(definition with { Environment = new RoleEnvironment(ToolAllowlist: ["browser.navigate"]) },
            Guid.NewGuid(), new("trace", "browser.trace", "{\"operation\":\"start\"}"), ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("forbidden", unauthorized.Text);
    }
    [Fact]
    public async Task Privileged_browser_effects_require_exact_approval_and_coordinate_authority()
    {
        var browser = new Subset(true); var gate = new ToolConfigurationGate(null, null, null, browser, true);
        var definition = Definition("browser.route", "browser.mouse");
        var executor = new SessionToolExecutor(browser: browser, configurationGate: gate);
        var call = new ModelToolCall("route", "browser.route", "{\"url\":\"http://127.0.0.1/mock\",\"action\":\"abort\"}");
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn);
        Assert.Contains("approval_required", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var changed = call with { ArgumentsJson = "{\"url\":\"http://127.0.0.1/changed\",\"action\":\"abort\"}" };
        Assert.Contains("stale_approval", (await executor.ExecuteAsync(definition, Guid.NewGuid(), changed, ToolLimits.MaxOutputBytes, approvalGrant: grant, admission: admission)).Text);
        Assert.Equal(0, browser.Commands);
        Assert.DoesNotContain("error", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, approvalGrant: grant, admission: admission)).Text);
        Assert.Equal(1, browser.Commands);
        var mouse = new ModelToolCall("mouse", "browser.mouse", "{\"operation\":\"click\",\"x\":1,\"y\":1}");
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), mouse, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Equal(1, browser.Commands);
    }
    private static AgentDefinition Definition(params string[] names)
    {
        return new AgentDefinition(1, "subset", 1, new("Subset", "Role", "Description", "Tone"), [], "instructions",
            new("answerNewTurn", true, true), new("balanced", false, "en", 2048),
            new(false, 60_000, 120_000, 1, ["longSilence"], 0), new(false, "default", 1),
            new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
            new RoleEnvironment(ToolAllowlist: names));
    }
    private sealed class Subset(bool advanced = false) : IBrowser,IBrowserRuntimeReadiness
    {
        public bool IsAvailable=>true; public bool IsRuntimeReady=>true;
        public int Commands { get; private set; }
        public BrowserProviderDescriptor Provider{get;}=new("subset","Subset", advanced ? new HashSet<BrowserFeature>{BrowserFeature.Navigate,BrowserFeature.NetworkControl,BrowserFeature.VisionMouse} : new HashSet<BrowserFeature>{BrowserFeature.Navigate,BrowserFeature.Snapshot,BrowserFeature.Click});
        public BrowserHostPolicy HostPolicy{get;}=new(true,true,BrowserInteractionMode.InteractiveDemo,[]);
        public ValueTask<Uri?> GetCurrentUrlAsync(Guid id,CancellationToken ct=default)=>new(new Uri("http://127.0.0.1/"));
        public ValueTask<BrowserOperationResult> NavigateAsync(BrowserNavigateRequest r,CancellationToken ct=default)=>new(new BrowserOperationResult(null,new("http://127.0.0.1/","Subset","Ready",false,[])));
        public ValueTask<BrowserOperationResult> SnapshotAsync(Guid id,CancellationToken ct=default)=>new(new BrowserOperationResult(null,new("http://127.0.0.1/","Subset","Ready",false,[])));
        public ValueTask<BrowserOperationResult> InteractAsync(BrowserInteractionRequest r,CancellationToken ct=default)=>new(new BrowserOperationResult(null,new("http://127.0.0.1/","Subset","Ready",false,[])));
        public ValueTask<BrowserCommandResult> ExecuteAsync(BrowserCommand c,CancellationToken ct=default)
        {
            if (!BrowserToolCatalog.TryGet(c.Tool, out var metadata) || !Provider.Supports(metadata.Feature)) return new(new BrowserCommandResult("unsupported_operation"));
            Commands++; return new(new BrowserCommandResult(null, DataJson: "{\"status\":\"ok\"}"));
        }
    }
}
