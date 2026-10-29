using AgentCore.Application.Decisions;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using Microsoft.Extensions.Time.Testing;

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
    [Fact]
    public async Task Model_and_effort_share_one_budget_and_never_probe_status_between_decisions()
    {
        var clock = new FakeTimeProvider();
        var engine = new Engine(B) { BeforeResult = () => clock.Advance(TimeSpan.FromMilliseconds(100)), TimeoutMs = 150 };
        var error = await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(() =>
            new AutoModelRouter(engine, clock).ResolveAsync(new Catalog(), ModelPreference.Auto, null, false, true, "session", Signals).AsTask());
        Assert.Equal("ModelRoutingUnavailable", error.Code); Assert.Contains("Timeout", error.Message);
        Assert.Equal(2, engine.Calls); Assert.Equal(0, engine.StatusCalls);
    }
    [Fact]
    public async Task Caller_cancellation_is_preserved_and_shadow_policy_is_local()
    {
        var engine = new Engine(B) { WaitForCancellation = true };
        using var cancellation = new CancellationTokenSource();
        var pending = new AutoModelRouter(engine).ResolveAsync(new Catalog(), new("auto"), null, false, true, "session", Signals, cancellation.Token).AsTask();
        await engine.Entered.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, engine.StatusCalls);
    }
    [Fact]
    public async Task Exhausted_budget_still_allows_only_the_explicit_deterministic_default()
    {
        var clock = new FakeTimeProvider();
        var engine = new Engine(B) { BeforeResult = () => clock.Advance(TimeSpan.FromMilliseconds(100)), TimeoutMs = 50 };
        var selected = await new AutoModelRouter(engine, clock).ResolveAsync(new Catalog { RoutingPolicy = new(true, true) },
            ModelPreference.Auto, null, false, true, "session", Signals);
        Assert.Equal(A, selected.OfferingId); Assert.Equal("configuredDefault", selected.Decision!.Basis);
        Assert.Equal("Timeout", selected.Decision.DiagnosticCode);
        Assert.Equal(1, engine.Calls); Assert.Equal(0, engine.StatusCalls);
    }
    [Fact]
    public async Task Late_timer_callback_cannot_admit_a_ranking_result_after_the_shared_deadline()
    {
        var clock = new DelayedTimerClock();
        var engine = new Engine(B) { BeforeResult = () => clock.Advance(TimeSpan.FromMilliseconds(100)), TimeoutMs = 50 };
        var selected = await new AutoModelRouter(engine, clock).ResolveAsync(new Catalog { RoutingPolicy = new(true, true) },
            ModelPreference.Auto, null, false, true, "session", Signals);
        Assert.Equal(A, selected.OfferingId); Assert.Equal("configuredDefault", selected.Decision!.Basis);
        Assert.Equal("Timeout", selected.Decision.DiagnosticCode); Assert.Equal(1, engine.Calls);
    }

    private sealed class DelayedTimerClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        // Reproduce a callback waiting in a busy scheduler while elapsed time advances.
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new DelayedTimer();
        private sealed class DelayedTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
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
        public Action? BeforeResult { get; init; } public bool WaitForCancellation { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TimeoutMs { get; init; } = 1000; public int StatusCalls;
        public DecisionExecutionPolicy Policy => new(Shadow, TimeoutMs);
        public int Calls { get; private set; } public DecisionStatus Status { get; init; } = DecisionStatus.Ready; public bool Shadow { get; init; }
        public ValueTask<DecisionEngineStatus> StatusAsync(CancellationToken ct = default)
        { StatusCalls++; return ValueTask.FromResult(new DecisionEngineStatus(DecisionStatus.Ready,"fixture","v1",true,Shadow)); }
        public async ValueTask<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken ct = default)
        {
            Calls++; Entered.TrySetResult();
            BeforeResult?.Invoke();
            if (WaitForCancellation) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            var winner = request.Candidates.Any(c => c.Id == selected) ? selected : request.Candidates[^1].Id;
            return Status == DecisionStatus.Ready ? new DecisionResult(Status, winner, request.Candidates.ToDictionary(c=>c.Id,c=>c.Id==winner?1d:0d),null,"fixture","v1") : DecisionResult.Failure(Status,"fixture","v1");
        }
    }
}
