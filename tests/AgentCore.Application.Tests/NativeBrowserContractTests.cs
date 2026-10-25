using AgentCore.Tests.Shared;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Tools;
namespace AgentCore.Application.Tests;
public sealed class NativeBrowserContractTests
{
    [Fact]
    public async Task Malformed_configuration_schema_rejects_bad_payloads_and_unowned_execution_never_dispatches()
    {
        var browser = new Subset(true);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll);
        var definition = Definition(ToolCatalog.BrowserConfiguration);
        foreach (var args in new[] { "[]", "\"bad\"", "{bad", "{\"unknown\":true}" })
        {
            var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("config", ToolCatalog.BrowserConfiguration, args), ToolLimits.MaxOutputBytes,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
            Assert.Contains("forbidden", result.Text);
            Assert.Contains("message", result.Text);
            if (args == "{bad") Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(args));
            else
            {
                using var parsed = JsonDocument.Parse(args);
                Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserConfiguration, parsed.RootElement, out _, out _));
            }
        }
        Assert.Equal(0, browser.Commands);
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserConfiguration, JsonSerializer.Deserialize<JsonElement>("{}"), out _, out _));
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("valid", ToolCatalog.BrowserConfiguration, "{}"), ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn))).Text);
        Assert.Equal(0, browser.Commands);
    }

    [Fact]
    public async Task Pinned_active_Skill_validation_does_not_authorize_unowned_browser_execution()
    {
        var browser = new Subset(true);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll);
        var old = new EffectiveSkill("instance:old", SkillOrigin.Instance, "old", "Old browser", "Historical", "Use browser.find and pass its opaque ref as scopeRef.", SkillProjection.OnDemand, [], []);
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, PinnedSkillCatalog: [old], ActiveSkillKeys: []);
        var call = new AgentCore.Application.Ports.ModelToolCall("config", ToolCatalog.BrowserConfiguration, "{}");
        var definition = Definition(ToolCatalog.BrowserConfiguration);
        Assert.Null(BrowserContractCutover.Diagnostic(definition, [old], []));
        var diagnostic = BrowserContractCutover.Diagnostic(definition, [old], [old.Key]);
        Assert.Contains(old.Key, diagnostic!); Assert.Contains("retired", diagnostic!);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Equal(0, browser.Commands);
        var rejected = await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission with { ActiveSkillKeys = [old.Key] });
        Assert.Contains("forbidden", rejected.Text);
        Assert.Equal(0, browser.Commands);
    }

    [Theory]
    [InlineData("{\"frameRef\":\"main\"}")]
    [InlineData("{\"frameRef\":\"fr_00000000000000000000000000000000\",\"target\":{\"by\":\"label\",\"value\":\"Email\",\"frameRef\":\"fr_11111111111111111111111111111111\"}}")]
    public void Snapshot_rejects_malformed_or_conflicting_frame_identity(string json)
    {
        Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserSnapshot, JsonSerializer.Deserialize<JsonElement>(json), out _, out var error));
        Assert.Equal("invalid_frame", error);
    }

    [Theory]
    [InlineData("role", "textbox")]
    [InlineData("text", "Email")]
    [InlineData("label", "Email")]
    [InlineData("placeholder", "Your email")]
    [InlineData("altText", "Logo")]
    [InlineData("title", "Help")]
    [InlineData("testId", "email")]
    public void Direct_target_is_shared_by_discovery_and_actions(string by, string value)
    {
        var target = new BrowserTarget(by, value);
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserClick, JsonSerializer.SerializeToElement(new { target }), out var click, out _));
        Assert.Equal(target, Assert.IsType<BrowserClick>(click.Command).Target);
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserFind, JsonSerializer.SerializeToElement(new { target }), out var find, out _));
        Assert.Equal(target, Assert.IsType<BrowserFind>(find.Command).Target);
    }

    [Theory]
    [InlineData("{\"ref\":\"el_0123456789abcdefghijkl\"}")]
    [InlineData("{\"target\":{\"by\":\"css\",\"value\":\"#password\"}}")]
    [InlineData("{\"target\":{\"by\":\"label\",\"value\":\"Email\",\"name\":\"Email\"}}")]
    [InlineData("{\"target\":{\"by\":\"role\",\"value\":\"button\",\"within\":{\"by\":\"text\",\"value\":\"row\",\"within\":{}}}}")]
    public void Retired_refs_selectors_incompatible_names_and_recursive_scopes_fail_before_dispatch(string json) =>
        Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserClick, JsonSerializer.Deserialize<JsonElement>(json), out _, out _));

    [Fact]
    public void Relational_filter_is_bounded_literal_data()
    {
        var target = new BrowserTarget("role", "button", Name: "Edit", Within: new("role", "row", HasText: "Record.*B"));
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserClick, JsonSerializer.SerializeToElement(new { target }), out var request, out _));
        Assert.Equal("Record.*B", Assert.IsType<BrowserClick>(request.Command).Target.Within!.HasText);
        Assert.False(BrowserToolArguments.ValidTarget(target with { HasText = new string('x', 201) }));
    }

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
            Assert.False(doc.RootElement.TryGetProperty("oneOf", out _));
        }
        foreach(var retired in new[]{"observe","act","pages","capture"}) Assert.False(ToolRegistry.TryGet("browser."+retired,out _));
        Assert.Equal(ToolEffect.SensitiveWrite,ToolCatalog.EffectOf("browser.route"));
        Assert.Equal(ToolEffect.SensitiveWrite,ToolCatalog.EffectOf("browser.local_storage"));
        Assert.Contains("diagnostics",ToolRegistry.Get("browser.console_messages").Tags);
    }
    [Fact]
    public async Task Subset_provider_filters_unsupported_tools_without_losing_navigation()
    {
        var subset=new Subset();var gate=new ToolConfigurationGate(null,null,subset,true);
        Assert.True(gate.IsConfigured("browser.navigate"));Assert.False(gate.IsConfigured("browser.hover"));
        var result=await subset.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(Guid.NewGuid(),"browser.hover",JsonSerializer.SerializeToElement(new { target = new { by = "role", value = "button", name = "Submit" } })));
        Assert.Equal("unsupported_operation",result.ErrorCode);
        Assert.Null((await subset.ExecuteAsync(BrowserTestRequests.Navigate(Guid.NewGuid(),new Uri("http://127.0.0.1/")))).ErrorCode);
        var definition = Definition("browser.navigate", "browser.hover");
        var executor = new SessionToolExecutor(browser: subset, configurationGate: gate);
        var forced = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("trace", "browser.hover", "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"el_0123456789abcdefghijkl\"}}"),
            ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("forbidden", forced.Text);
        var unauthorized = await executor.ExecuteAsync(definition with { ExecutionPolicy = new DefinitionExecutionPolicy(ToolAllowlist: ["browser.navigate"]) },
            Guid.NewGuid(), new("trace", "browser.hover", "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"el_0123456789abcdefghijkl\"}}"), ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("forbidden", unauthorized.Text);
        Assert.Equal(1, subset.Commands); // Only the explicit low-level adapter fixture navigation ran.
    }
    [Fact]
    public async Task Unowned_privileged_browser_effects_cannot_dispatch_and_coordinate_authority_remains_required()
    {
        var browser = new Subset(true); var gate = new ToolConfigurationGate(null, null, browser, true);
        var definition = Definition("browser.route", "browser.mouse");
        var executor = new SessionToolExecutor(browser: browser, configurationGate: gate);
        var call = new ModelToolCall("route", "browser.route", "{\"url\":\"http://127.0.0.1/mock\",\"action\":\"abort\"}");
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        var changed = call with { ArgumentsJson = "{\"url\":\"http://127.0.0.1/changed\",\"action\":\"abort\"}" };
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), changed, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Equal(0, browser.Commands);
        var mouse = new ModelToolCall("mouse", "browser.mouse", "{\"operation\":\"click\",\"x\":1,\"y\":1}");
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), mouse, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Equal(0, browser.Commands);
    }
    [Fact]
    public async Task Environment_argument_contracts_and_unowned_execution_preserve_independent_boundaries()
    {
        var subset = new Subset();
        var subsetGate = new ToolConfigurationGate(null, null, subset, true);
        Assert.False(subsetGate.IsConfigured(ToolCatalog.BrowserConfiguration));
        Assert.False(subsetGate.IsConfigured(ToolCatalog.BrowserGeolocation));
        var browser = new Subset(true);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: new ToolConfigurationGate(null, null, browser, true));
        var definition = Definition(ToolCatalog.BrowserConfiguration, ToolCatalog.BrowserGeolocation, ToolCatalog.BrowserMedia, ToolCatalog.BrowserType);
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn);
        var call = new ModelToolCall("geo", ToolCatalog.BrowserGeolocation, "{\"operation\":\"set\",\"origin\":\"http://127.0.0.1\",\"latitude\":10,\"longitude\":20}");
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call with { ArgumentsJson = call.ArgumentsJson.Replace(":10", ":11") }, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes,
            admission: new(false, TriggerKind.ScheduledOccurrence))).Text);
        Assert.Equal(0, browser.Commands);
        foreach (var json in new[] { "{\"reducedMotion\":\"reduce\"}", "{\"colorScheme\":null}", "{\"forcedColors\":\"active\",\"contrast\":\"more\"}" })
        {
            Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserMedia, JsonSerializer.Deserialize<JsonElement>(json), out _, out _));
            Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("media", ToolCatalog.BrowserMedia, json), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        }
        foreach (var json in new[] { "{\"colorScheme\":true}", "{\"contrast\":\"wrong\"}", "{\"colorScheme\":null,\"colorScheme\":\"light\"}" })
            Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserMedia, JsonSerializer.Deserialize<JsonElement>(json), out _, out _));
        foreach (var slowly in new[] { false, true })
        {
            var json = JsonSerializer.Serialize(new { target = new { by = "role", value = "button", name = "el_0123456789012345678901" }, text = "abc", slowly });
            Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserType, JsonSerializer.Deserialize<JsonElement>(json), out _, out _));
            Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("type", ToolCatalog.BrowserType, json), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        }
        Assert.Equal(0, browser.Commands);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("config", ToolCatalog.BrowserConfiguration, "{\"enabled\":true}"), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition with { ExecutionPolicy = new DefinitionExecutionPolicy(ToolAllowlist: []) }, Guid.NewGuid(), new("config", ToolCatalog.BrowserConfiguration, "{}"), ToolLimits.MaxOutputBytes, admission: admission)).Text);
    }

    private static AgentDefinition Definition(params string[] names)
    {
        return new AgentDefinition(2, "subset", 1, new("Subset", "Role", "Description", "Tone"), [], "instructions",
            new("answerNewTurn", true, true), new("balanced", false, "en", 2048),
            new(false, 60_000, 120_000, 1, ["longSilence"], 0), new(false, "default", 1),
            new("primary-stt", "primary-tts"), new Dictionary<string, string>(),
            new DefinitionExecutionPolicy(ToolAllowlist: names));
    }
    private sealed class Subset(bool advanced = false) : IBrowser,IBrowserRuntimeReadiness
    {
        public bool IsAvailable=>true; public bool IsRuntimeReady=>true;
        public int Commands { get; private set; }
        public BrowserProviderDescriptor Provider{get;}=new("subset","Subset", advanced ? new HashSet<BrowserFeature>{BrowserFeature.Navigate,BrowserFeature.NetworkControl,BrowserFeature.VisionMouse,BrowserFeature.Configuration,BrowserFeature.Geolocation,BrowserFeature.Media,BrowserFeature.Type} : new HashSet<BrowserFeature>{BrowserFeature.Navigate,BrowserFeature.Snapshot,BrowserFeature.Click});
        public BrowserHostPolicy HostPolicy{get;}=new(true,true,BrowserInteractionMode.InteractiveDemo,["http://127.0.0.1"]);
        public ValueTask<Uri?> GetCurrentUrlAsync(Guid id,CancellationToken ct=default)=>new(new Uri("http://127.0.0.1/"));
        public ValueTask<BrowserResult> NavigateAsync(BrowserRequest r,CancellationToken ct=default)=>new(new BrowserResult(null,new("http://127.0.0.1/","Subset","Ready",false,[])));
        public ValueTask<BrowserResult> SnapshotAsync(Guid id,CancellationToken ct=default)=>new(new BrowserResult(null,new("http://127.0.0.1/","Subset","Ready",false,[])));
        public ValueTask<BrowserResult> InteractAsync(BrowserRequest r,CancellationToken ct=default)=>new(new BrowserResult(null,new("http://127.0.0.1/","Subset","Ready",false,[])));
        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest c,CancellationToken ct=default)
        {
            if (!BrowserToolCatalog.TryGet(AgentCore.Application.Tools.BrowserToolArguments.ToolName(c.Operation), out var metadata) || !Provider.Supports(metadata.Feature)) return new(new BrowserResult("unsupported_operation"));
            Commands++; return new(new BrowserResult(null, DataJson: "{\"status\":\"ok\"}"));
        }
    }
}
