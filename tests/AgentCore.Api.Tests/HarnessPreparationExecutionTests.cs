using System.Runtime.CompilerServices;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class HarnessPreparationExecutionTests
{
    private static async Task<(HarnessManagementService Service, AgentInstance Instance, Guid Preparation)> Start(AgentCoreApiFactory factory)
    {
        var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 7);
        var service = factory.Services.GetRequiredService<HarnessManagementService>();
        instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.Skills], [], []));
        instance = await service.StartAsync(instance.InstanceId, instance.Revision, "Prepare a safe procedure.");
        return (service, instance, instance.HarnessManagement!.Preparation!.PreparationId);
    }

    [Fact]
    public async Task Unexpected_provider_failure_has_server_diagnostic_and_does_not_change_active_harness()
    {
        await using var factory = new ExecutionFactory(new FailingModel());
        var (service, instance, prep) = await Start(factory);
        var error = await Assert.ThrowsAsync<AgentCoreException>(async () => await factory.Services.GetRequiredService<HarnessPreparationExecution>().RunAsync(instance.InstanceId, prep));
        Assert.NotNull(error.DiagnosticId);
        Assert.DoesNotContain("private provider payload", error.Message);
        var review = await service.ReviewAsync(instance.InstanceId);
        Assert.Equal(error.DiagnosticId, review.State.Preparation!.DiagnosticId);
        Assert.Equal(HarnessPreparationStatus.Failed, review.State.Preparation.Status);
        Assert.Equal(7, review.ActiveVersion);
    }

    [Fact]
    public async Task Cancellation_while_generating_cancels_the_candidate_without_adoption()
    {
        var model = new GatedModel();
        await using var factory = new ExecutionFactory(model);
        var (service, instance, prep) = await Start(factory);
        using var cancelled = new CancellationTokenSource();
        var running = factory.Services.GetRequiredService<HarnessPreparationExecution>().RunAsync(instance.InstanceId, prep, cancelled.Token).AsTask();
        await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await running);
        var review = await service.ReviewAsync(instance.InstanceId);
        Assert.Equal(HarnessPreparationStatus.Cancelled, review.State.Preparation!.Status);
        Assert.Equal(7, review.ActiveVersion);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RequestOperationAsync(instance.InstanceId, prep, new("skill.remove", review.Draft!.Revision, Id: "anything")));
    }

    [Fact]
    public async Task Cumulative_budget_cannot_reset_on_continuation()
    {
        await using var factory = new AgentCoreApiFactory();
        var (service, instance, prep) = await Start(factory);
        await service.CheckpointBudgetAsync(instance.InstanceId, prep, 24, 1000, 10000, default);
        await Assert.ThrowsAsync<AgentCoreException>(async () => await service.CheckpointBudgetAsync(instance.InstanceId, prep, 0, 0, 180000, default));
        await Assert.ThrowsAsync<AgentCoreException>(async () => await factory.Services.GetRequiredService<HarnessPreparationExecution>().RunAsync(instance.InstanceId, prep));
        Assert.Equal(24, (await service.ReviewAsync(instance.InstanceId)).State.Preparation!.StepsUsed);
        Assert.Equal(7, (await service.ReviewAsync(instance.InstanceId)).ActiveVersion);
    }

    private sealed class ExecutionFactory(ILanguageModel model) : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ILanguageModelResolver>();
                services.AddSingleton<ILanguageModelResolver>(new StaticLanguageModelResolver(model));
            });
        }
    }
    private sealed class FailingModel : ILanguageModel
    {
        public ModelCapabilities Capabilities => new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ModelTextDelta("");
            throw new InvalidOperationException("private provider payload");
        }
    }
    private sealed class GatedModel : ILanguageModel
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModelCapabilities Capabilities => new(true, true, Tools: true);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }
}
