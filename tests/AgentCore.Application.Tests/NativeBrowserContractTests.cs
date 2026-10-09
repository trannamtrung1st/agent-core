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
    public async Task Malformed_configuration_is_rejected_before_provider_execution_using_the_empty_object_schema()
    {
        var browser = new Subset(true);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll);
        var definition = Definition(ToolCatalog.BrowserConfiguration);
        foreach (var args in new[] { "[]", "\"bad\"", "{bad", "{\"unknown\":true}" })
        {
            var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("config", ToolCatalog.BrowserConfiguration, args), ToolLimits.MaxOutputBytes,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
            Assert.Contains("invalid", result.Text);
            Assert.Contains("message", result.Text);
        }
        Assert.Equal(0, browser.Commands);
        Assert.DoesNotContain("error", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("valid", ToolCatalog.BrowserConfiguration, "{}"), ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn))).Text);
        Assert.Equal(1, browser.Commands);
    }

    [Theory]
    [InlineData("role", "textbox")]
    [InlineData("text", "Email")]
    [InlineData("label", "Email")]
    [InlineData("placeholder", "Enter your email")]
    [InlineData("altText", "Logo")]
    [InlineData("title", "Help")]
    [InlineData("testId", "email")]
    public void Single_strategy_normalizes_harmless_optional_fields(string by, string value)
    {
        var args = JsonSerializer.SerializeToElement(new { by, value, name = "  ", hasText = "", scopeRef = "", frameRef = (string?)null, exact = (bool?)null, limit = (int?)null });
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserFind, args, out var request, out _));
        var query = request.Options.Query!;
        Assert.Contains(value, new[] { query.Role, query.Text, query.Label, query.Placeholder, query.AltText, query.Title, query.TestId });
        Assert.Null(query.ScopeRef); Assert.Null(query.FrameRef); Assert.Null(query.Name); Assert.Null(query.HasText);
    }

    [Theory]
    [InlineData("{\"by\":\"label\",\"value\":\"Email\",\"name\":\"Email\"}")]
    [InlineData("{\"by\":\"role\",\"value\":\"textbox\",\"text\":\"Email\"}")]
    [InlineData("{\"by\":\"unknown\",\"value\":\"Email\"}")]
    [InlineData("{\"by\":\"text\",\"value\":\"Email\",\"scopeRef\":\"el_0123456789abcdefghijkl\",\"frameRef\":\"frame-1\"}")]
    public void Incompatible_nonempty_criteria_are_rejected(string json) =>
        Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserFind, JsonSerializer.Deserialize<JsonElement>(json), out _, out _));

    [Fact]
    public async Task Malformed_discovery_explains_query_repair_before_reference_repair()
    {
        const string overloaded = """{"role":"textbox","name":"Email","text":"Email","label":"Email","placeholder":"Enter your email","scopeRef":"","frameRef":""}""";
        Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), ToolCatalog.BrowserFind,
            JsonSerializer.Deserialize<JsonElement>(overloaded), out _, out var error));
        Assert.Equal("invalid", error);
        var browser = new Subset();
        var executor = new SessionToolExecutor(browser: browser, configurationGate: ToolConfigurationGates.AllowAll);
        var definition = Definition(ToolCatalog.BrowserFind, ToolCatalog.BrowserSnapshot);
        foreach (var args in new[] { overloaded, "{}", "{\"label\":\"Email\",\"name\":\"Email\"}" })
        {
            var result = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("find", ToolCatalog.BrowserFind, args),
                ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
            Assert.Contains("Choose exactly one", result.Text);
            Assert.Contains("placeholder", result.Text);
            Assert.Contains("Omit", result.Text);
        }
        var badScope = await executor.ExecuteAsync(definition, Guid.NewGuid(),
            new("find", ToolCatalog.BrowserFind, "{\"by\":\"placeholder\",\"value\":\"Enter your email\",\"scopeRef\":\"invalid\"}"),
            ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("invalid_reference", badScope.Text);
        Assert.Contains("scopeRef", badScope.Text);
        var snapshot = await executor.ExecuteAsync(definition, Guid.NewGuid(),
            new("snapshot", ToolCatalog.BrowserSnapshot, "{\"targetRef\":\"\"}"), ToolLimits.MaxOutputBytes,
            admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("Omit it to inspect the page", snapshot.Text);
        Assert.Equal(0, browser.Commands);
    }
    [Fact]
    public void Relational_text_filter_is_bounded_literal_neutral_data_and_requires_a_primary_query()
    {
        var id = Guid.NewGuid();
        var args = JsonSerializer.SerializeToElement(new { by = "role", value = "row", hasText = "Record.*B" });
        Assert.True(BrowserToolArguments.TryRequest(id, ToolCatalog.BrowserFind, args, out var request, out _));
        Assert.Equal("row", request.Options.Query!.Role);
        Assert.Equal("Record.*B", request.Options.Query.HasText);
        foreach (var invalid in new object[] { new { hasText = "Record B" }, new { by = "text", value = "row", name = "Invalid" },
            new { by = "role", value = "row", hasText = new string('x', 201) }, new { by = "role", value = "row", hasText = new { regex = "Record.*" } } })
            Assert.False(BrowserToolArguments.TryRequest(id, ToolCatalog.BrowserFind, JsonSerializer.SerializeToElement(invalid), out _, out _));
        Assert.False(BrowserToolArguments.ValidQuery(new(Role: "row", HasText: new string('x', 201))));
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
        Assert.True(gate.IsConfigured("browser.navigate"));Assert.False(gate.IsConfigured("browser.hover"));
        var result=await subset.ExecuteAsync(AgentCore.Application.Tools.BrowserToolArguments.Request(Guid.NewGuid(),"browser.hover",JsonSerializer.SerializeToElement(new { @ref = "el_0123456789abcdefghijkl" })));
        Assert.Equal("unsupported_operation",result.ErrorCode);
        Assert.Null((await subset.ExecuteAsync(BrowserTestRequests.Navigate(Guid.NewGuid(),new Uri("http://127.0.0.1/")))).ErrorCode);
        var definition = Definition("browser.navigate", "browser.hover");
        var executor = new SessionToolExecutor(browser: subset, configurationGate: gate);
        var forced = await executor.ExecuteAsync(definition, Guid.NewGuid(), new("trace", "browser.hover", "{\"ref\":\"el_0123456789abcdefghijkl\"}"),
            ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
        Assert.Contains("unsupported_operation", forced.Text);
        var unauthorized = await executor.ExecuteAsync(definition with { Environment = new RoleEnvironment(ToolAllowlist: ["browser.navigate"]) },
            Guid.NewGuid(), new("trace", "browser.hover", "{\"ref\":\"el_0123456789abcdefghijkl\"}"), ToolLimits.MaxOutputBytes, admission: new(false, TriggerKind.UserTurn));
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
    [Fact]
    public async Task Environment_tools_preserve_negotiation_approval_and_direct_turn_boundaries()
    {
        var subset = new Subset();
        var subsetGate = new ToolConfigurationGate(null, null, null, subset, true);
        Assert.False(subsetGate.IsConfigured(ToolCatalog.BrowserConfiguration));
        Assert.False(subsetGate.IsConfigured(ToolCatalog.BrowserGeolocation));
        var browser = new Subset(true);
        var executor = new SessionToolExecutor(browser: browser, configurationGate: new ToolConfigurationGate(null, null, null, browser, true));
        var definition = Definition(ToolCatalog.BrowserConfiguration, ToolCatalog.BrowserGeolocation, ToolCatalog.BrowserMedia, ToolCatalog.BrowserType);
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn);
        var call = new ModelToolCall("geo", ToolCatalog.BrowserGeolocation, "{\"operation\":\"set\",\"origin\":\"http://127.0.0.1\",\"latitude\":10,\"longitude\":20}");
        Assert.Contains("approval_required", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, admission: admission)).Text);
        using var args = JsonDocument.Parse(call.ArgumentsJson);
        var grant = new ToolApprovalGrant(Guid.NewGuid(), call.Name, ToolActionHash.Compute(call.Name, args.RootElement), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        Assert.Contains("stale_approval", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call with { ArgumentsJson = call.ArgumentsJson.Replace(":10", ":11") }, ToolLimits.MaxOutputBytes, approvalGrant: grant, admission: admission)).Text);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, approvalGrant: grant,
            admission: new(false, TriggerKind.ScheduledOccurrence))).Text);
        Assert.Equal(0, browser.Commands);
        Assert.DoesNotContain("error", (await executor.ExecuteAsync(definition, Guid.NewGuid(), call, ToolLimits.MaxOutputBytes, approvalGrant: grant, admission: admission)).Text);
        Assert.Equal(1, browser.Commands);
        foreach (var json in new[] { "{\"reducedMotion\":\"reduce\"}", "{\"colorScheme\":null}", "{\"forcedColors\":\"active\",\"contrast\":\"more\"}" })
            Assert.DoesNotContain("error", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("media", ToolCatalog.BrowserMedia, json), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        foreach (var json in new[] { "{\"colorScheme\":true}", "{\"contrast\":\"wrong\"}", "{\"colorScheme\":null,\"colorScheme\":\"light\"}" })
            Assert.Contains("invalid", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("media", ToolCatalog.BrowserMedia, json), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.DoesNotContain("error", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("type", ToolCatalog.BrowserType, "{\"ref\":\"el_0123456789012345678901\",\"text\":\"abc\",\"slowly\":true}"), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.DoesNotContain("error", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("type", ToolCatalog.BrowserType, "{\"ref\":\"el_0123456789012345678901\",\"text\":\"abc\",\"slowly\":false}"), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition, Guid.NewGuid(), new("config", ToolCatalog.BrowserConfiguration, "{\"enabled\":true}"), ToolLimits.MaxOutputBytes, admission: admission)).Text);
        Assert.Contains("forbidden", (await executor.ExecuteAsync(definition with { Environment = new RoleEnvironment(ToolAllowlist: []) }, Guid.NewGuid(), new("config", ToolCatalog.BrowserConfiguration, "{}"), ToolLimits.MaxOutputBytes, admission: admission)).Text);
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
