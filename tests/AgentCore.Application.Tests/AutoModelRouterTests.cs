using AgentCore.Application.Decisions;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Tests;

public sealed class AutoModelRouterTests
{
    private const string A = "dca30000-0000-4000-8000-000000000001", B = "dca30000-0000-4000-8000-000000000002";
    private static readonly IReadOnlyDictionary<string, string> Signals = new Dictionary<string, string> { ["inputCharacters"] = "45" };
    [Fact]
    public async Task Fixed_choice_bypasses_ranking_and_explicit_effort_wins()
    {
        var engine = new Engine(B); var router = new AutoModelRouter(engine); var catalog = new Catalog();
        var selection = await router.ResolveAsync(catalog, ModelPreference.Fixed(A, "high"), ModelPreference.Fixed(B, "low"), false, true, "session", Signals);
        Assert.Equal(A, selection.OfferingId); Assert.Equal("high", selection.ReasoningEffort); Assert.Equal(0, engine.Calls);
        Assert.Equal("fixed", selection.Decision!.Basis);
        await Assert.ThrowsAsync<AgentCoreException>(() => router.ResolveAsync(catalog, ModelPreference.Fixed(A, "unsupported"), null, false, true, "session", Signals).AsTask());
        Assert.Equal(0, engine.Calls);
    }
    [Fact]
    public async Task Auto_vision_hard_filter_selects_single_eligible_without_inference()
    {
        var engine = new Engine(A); var router = new AutoModelRouter(engine);
        var selected = await router.ResolveAsync(new Catalog(), new("auto"), null, true, true, "session", Signals);
        Assert.Equal(B, selected.OfferingId); Assert.Equal("singleEligible", selected.Decision!.Basis); Assert.Equal(0, engine.Calls);
    }
    [Theory]
    [InlineData(DecisionStatus.Timeout)] [InlineData(DecisionStatus.LowConfidence)] [InlineData(DecisionStatus.Disabled)]
    [InlineData(DecisionStatus.InvalidResult)] [InlineData(DecisionStatus.Saturated)]
    public async Task Unavailable_Auto_requires_explicit_eligible_default_policy(DecisionStatus status)
    {
        var engine = new Engine(B) { Status = status }; var router = new AutoModelRouter(engine);
        await Assert.ThrowsAsync<AgentCoreException>(() => router.ResolveAsync(new Catalog(), new("auto"), null, false, true, "session", Signals).AsTask());
        var fallback = await router.ResolveAsync(new Catalog { RoutingPolicy = new(true, true) }, new("auto"), null, false, true, "session", Signals);
        Assert.Equal(A, fallback.OfferingId); Assert.Equal("configuredDefault", fallback.Decision!.Basis); Assert.Equal(status.ToString(), fallback.Decision.DiagnosticCode);
        await Assert.ThrowsAsync<AgentCoreException>(() => router.ResolveAsync(new Catalog { DefaultOfferingId = "missing", RoutingPolicy = new(true, true) }, new("auto"), null, false, true, "session", Signals).AsTask());
    }
    [Fact]
    public async Task Reasoning_intent_cannot_enable_tenant_Auto_implicitly_and_shadow_never_applies_recommendation()
    {
        var engine = new Engine(B) { Shadow = true }; var router = new AutoModelRouter(engine);
        var catalog = new Catalog { RoutingPolicy = new(false, false) };
        await Assert.ThrowsAsync<AgentCoreException>(() => router.ResolveAsync(catalog, ModelPreference.Fixed(A) with { Reasoning = new("auto") }, null, false, true, "session", Signals).AsTask());
        Assert.Equal(0, engine.Calls);
        var selected = await router.ResolveAsync(new Catalog { RoutingPolicy = new(true, true) }, new("auto"), null, false, true, "session", Signals);
        Assert.Equal(A, selected.OfferingId); Assert.Equal("shadowMode", selected.Decision!.DiagnosticCode);
    }
    [Fact]
    public async Task Inherited_model_preserves_separate_fixed_reasoning_intent()
    {
        var engine = new Engine(B); var router = new AutoModelRouter(engine);
        var selected = await router.ResolveAsync(new Catalog(), new("inherit", Reasoning: new("fixed", "high")), ModelPreference.Fixed(B, "low"), false, true, "session", Signals);
        Assert.Equal(B, selected.OfferingId); Assert.Equal("high", selected.ReasoningEffort); Assert.Equal(0, engine.Calls);
    }
    [Fact]
    public void Inherited_unattended_reasoning_preserves_model_choice_and_explicit_override()
    {
        var inherited = ExecutionModelPolicy.MergePreference(ModelPreference.Fixed(A, "low"), new("inherit", Reasoning: new("auto")));
        Assert.Equal(A, inherited.OfferingId); Assert.Equal("auto", inherited.Reasoning!.Mode);
        var fixedChoice = ExecutionModelPolicy.MergePreference(inherited, ModelPreference.Fixed(B, "high"));
        Assert.Equal(B, fixedChoice.OfferingId); Assert.Equal("high", fixedChoice.Reasoning!.Effort);
    }
    [Fact]
    public async Task Explicit_model_default_reasoning_does_not_inherit_tenant_fixed_effort()
    {
        var router = new AutoModelRouter(new Engine(B));
        var selected = await router.ResolveAsync(new Catalog { DefaultReasoningEffort = "high" },
            new("inherit", Reasoning: ReasoningPreference.Default), null, false, true, "session", Signals);
        Assert.Equal("low", selected.ReasoningEffort);
        selected = await router.ResolveAsync(new Catalog { DefaultReasoningEffort = "high" }, ModelPreference.Inherit, null, false, true, "session", Signals);
        Assert.Equal("high", selected.ReasoningEffort);
    }
    [Fact]
    public void Fresh_background_decisions_require_the_current_settings_witness()
    {
        var pin = new AgentCore.Domain.Conversation.AgentRunModelPin(A, B, "fixture", null,
            decision: new("ranked", "fixture", "v1", 3, "session", "routing-v1"));
        ExecutionModelPolicy.RequireCurrentDecision(pin, 3);
        Assert.Throws<AgentCore.Application.Security.SecurityConflictException>(() => ExecutionModelPolicy.RequireCurrentDecision(pin, 4));
        ExecutionModelPolicy.RequireCurrentDecision(new AgentCore.Domain.Conversation.AgentRunModelPin(A, B, "fixture", null, decision: pin.Decision! with { Basis = "fixed" }), 4);
    }
    private sealed class Catalog : IModelCatalog
    {
        public string DefaultOfferingId { get; init; } = A;
        public string? DefaultReasoningEffort { get; init; }
        public AutoRoutingPolicy RoutingPolicy { get; init; } = new(true, false);
        public long RoutingRevision => 12;
        public ModelDescriptor Default => Models[0];
        public IReadOnlyList<ModelDescriptor> Models { get; } = [
            new(A,"A","connection","model-a",true,false,true,true,["low","high"],"low",DeveloperRoleSupport:true),
            new(B,"B","connection","model-b",true,true,true,true,["low","high"],"low",DeveloperRoleSupport:true)];
        public ModelDescriptor? Get(string key) => Models.SingleOrDefault(m => m.OfferingId == key);
    }
    private sealed class Engine(string selected) : IDecisionEngine
    {
        public int Calls { get; private set; } public DecisionStatus Status { get; init; } = DecisionStatus.Ready; public bool Shadow { get; init; }
        public ValueTask<DecisionEngineStatus> StatusAsync(CancellationToken ct = default) => ValueTask.FromResult(new DecisionEngineStatus(DecisionStatus.Ready,"fixture","v1",true,Shadow));
        public ValueTask<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default)
        { Calls++; return ValueTask.FromResult(Status == DecisionStatus.Ready ? new DecisionResult(Status, selected, request.Candidates.ToDictionary(c=>c.Id,c=>c.Id==selected?1d:0d),null,"fixture","v1") : DecisionResult.Failure(Status,"fixture","v1")); }
    }
}
