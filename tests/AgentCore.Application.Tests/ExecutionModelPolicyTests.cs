using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Application.Tests;

public sealed class ExecutionModelPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Precedence_is_trigger_override_then_unattended_default_then_conversation_default()
    {
        var catalog = Catalog();
        var definition = await LoadAsync("customer-support", 2);
        var instance = Instance(definition, "dca30000-0000-4000-8000-000000000003", null);
        var unattended = ExecutionModelPolicy.Resolve(catalog, definition, instance, registration: null);
        Assert.True(unattended.Accepted);
        Assert.Equal("dca30000-0000-4000-8000-000000000003", unattended.Pin!.OfferingId);
        Assert.Equal(ExecutionModelSource.UnattendedDefault, unattended.Pin.Source);
        Assert.Null(unattended.Pin.ReasoningEffort);

        var overridden = ExecutionModelPolicy.Resolve(
            catalog,
            definition,
            instance,
            Registration("dca30000-0000-4000-8000-000000000001", "low", requiresVision: false));
        Assert.True(overridden.Accepted);
        Assert.Equal("dca30000-0000-4000-8000-000000000001", overridden.Pin!.OfferingId);
        Assert.Equal("low", overridden.Pin.ReasoningEffort);
        Assert.Equal(ExecutionModelSource.TriggerOverride, overridden.Pin.Source);

        var conversation = ExecutionModelPolicy.Resolve(catalog, definition, Instance(definition, null, null), registration: null);
        Assert.True(conversation.Accepted);
        Assert.Equal("dca30000-0000-4000-8000-000000000001", conversation.Pin!.OfferingId);
        Assert.Equal("medium", conversation.Pin.ReasoningEffort);
        Assert.Equal(ExecutionModelSource.ConversationDefault, conversation.Pin.Source);
    }

    [Fact]
    public async Task Unknown_override_does_not_fall_back_to_the_unattended_or_catalog_default()
    {
        var definition = await LoadAsync("customer-support", 2);
        var decision = ExecutionModelPolicy.Resolve(
            Catalog(),
            definition,
            Instance(definition, "dca30000-0000-4000-8000-000000000003", null),
            Registration("missing-model", null, requiresVision: false));
        Assert.Null(decision.Pin);
        Assert.Equal(ExecutionModelPolicy.UnavailableCode, decision.FailureCode);
    }

    [Fact]
    public async Task Requires_vision_rejects_a_text_only_model_and_keeps_the_pin()
    {
        var definition = await LoadAsync("customer-support", 2);
        var decision = ExecutionModelPolicy.Resolve(
            Catalog(),
            definition,
            Instance(definition, "dca30000-0000-4000-8000-000000000001", "medium"),
            Registration(null, null, requiresVision: true));
        Assert.Equal("dca30000-0000-4000-8000-000000000001", decision.Pin!.OfferingId);
        Assert.Equal(ExecutionModelPolicy.CapabilityCode, decision.FailureCode);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public async Task Browser_work_rejects_a_model_without_tools()
    {
        var definition = await LoadAsync("general-assistant", 12);
        var decision = ExecutionModelPolicy.Resolve(
            Catalog(),
            definition,
            Instance(definition, "text-only", null),
            registration: null);
        Assert.Equal("text-only", decision.Pin!.ExternalModelId);
        Assert.Equal(ExecutionModelPolicy.CapabilityCode, decision.FailureCode);
    }

    [Fact]
    public async Task Text_only_model_with_tools_can_run_browser_work_without_vision()
    {
        var definition = await LoadAsync("general-assistant", 12);
        var decision = ExecutionModelPolicy.Resolve(
            Catalog(),
            definition,
            Instance(definition, "dca30000-0000-4000-8000-000000000001", "medium"),
            registration: null);
        Assert.True(decision.Accepted);
        Assert.Equal("dca30000-0000-4000-8000-000000000001", decision.Pin!.OfferingId);
    }

    [Fact]
    public void Routing_update_keeps_the_occurrence_pin()
    {
        var pin = new ExecutionModelPin("dca30000-0000-4000-8000-000000000003", "dca20000-0000-4000-8000-000000000001", "dca30000-0000-4000-8000-000000000003", null, ExecutionModelSource.UnattendedDefault);
        var owner = new TriggerOwner(Guid.NewGuid(), Guid.NewGuid());
        var occurrence = new TriggerOccurrence(
            Guid.NewGuid(),
            "applicationEvent:pin",
            null,
            owner,
            TriggerSourceKind.ApplicationEvent,
            null,
            Now,
            Now,
            "{}",
            Guid.NewGuid(),
            null,
            OccurrenceRoutingDisposition.Pending,
            null,
            0,
            null,
            null,
            null,
            modelPin: pin);
        var routed = occurrence.WithRouting(
            OccurrenceRoutingDisposition.AwaitingDurableWork,
            "No compatible runtime.",
            1,
            Now,
            null,
            null);
        Assert.Equal(pin.OfferingId, routed.ModelPin!.OfferingId);
        Assert.Equal(pin.Source, routed.ModelPin.Source);
    }

    private static IModelCatalog Catalog() =>
        new ConfigurationModelCatalog(
            "dca30000-0000-4000-8000-000000000001",
            [
                new ModelDescriptor(
                    "dca30000-0000-4000-8000-000000000001",
                    "Scripted Alpha",
                    "dca20000-0000-4000-8000-000000000001",
                    "dca30000-0000-4000-8000-000000000001",
                    Tools: true,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: true,
                    ["low", "medium", "high"],
                    "medium", DeveloperRoleSupport: true),
                new ModelDescriptor(
                    "dca30000-0000-4000-8000-000000000003",
                    "Scripted Vision",
                    "dca20000-0000-4000-8000-000000000001",
                    "dca30000-0000-4000-8000-000000000003",
                    Tools: true,
                    Vision: true,
                    StructuredOutput: false,
                    Reasoning: false,
                    [],
                    null, DeveloperRoleSupport: true),
                new ModelDescriptor(
                    "text-only",
                    "Text only",
                    "dca20000-0000-4000-8000-000000000001",
                    "text-only",
                    Tools: false,
                    Vision: false,
                    StructuredOutput: false,
                    Reasoning: false,
                    [],
                    null, DeveloperRoleSupport: true)
            ]);

    private static AgentInstance Instance(AgentDefinition definition, string? offeringId, string? effort) =>
        new(
            Guid.Parse("019944af-00e1-7000-8000-000000000001"),
            definition.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            Now,
            Now,
            UnattendedOfferingId: offeringId,
            UnattendedReasoningEffort: effort);

    private static Automation Registration(
        string? offeringId,
        string? effort,
        bool requiresVision) =>
        new(
            Guid.Parse("019944af-00e1-7000-8000-000000000002"),
            new TriggerOwner(Guid.Parse("019944af-00e1-7000-8000-000000000001"), Guid.Parse("019944af-00e1-7000-8000-000000000003")),
            AutomationStatus.Active,
            "Review the order",
            new OneShotSchedule(Now, "UTC", null, null),
            Now,
            null,
            0,
            1,
            1,
            new TriggerProvenance(TriggerAuthorizationOrigin.CurrentUserTurn, null, null, Now, Now),
            null,
            offeringId,
            effort,
            requiresVision);

    private static async Task<AgentDefinition> LoadAsync(string id, int version)
    {
        var store = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync(id, version).ConfigureAwait(false))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
