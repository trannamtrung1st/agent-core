using System.Diagnostics;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Testing;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Infrastructure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed class DiagnosticLoggingTests
{
    private static readonly Guid DiagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
    private static readonly Guid BusinessId = Guid.Parse("019944af-00d7-7000-8000-0000000000b1");
    private static readonly Guid SessionId = Guid.Parse("019944af-00d7-7000-8000-0000000000a1");

    [Fact]
    public void Synthetic_host_uses_one_diagnostic_source_for_background_services()
    {
        using var factory = new AgentCoreApiFactory();
        var diagnostics = factory.Services.GetRequiredService<IDiagnosticIdSource>();
        Assert.IsType<SystemDiagnosticIdSource>(diagnostics);
        Assert.IsType<SystemIdGenerator>(factory.Services.GetRequiredService<IIdGenerator>());
        Assert.Same(diagnostics, factory.Services.GetRequiredService<TriggerScheduler>().DiagnosticIds);
        Assert.Same(diagnostics, factory.Services.GetRequiredService<DurableWorkIntake>().DiagnosticIds);

        var hosted = factory.Services.GetServices<IHostedService>().ToArray();
        Assert.Contains(hosted, service => service is TriggerSchedulerHostedService);
        Assert.Contains(hosted, service => service is DurableWorkIntakeHostedService);
        Assert.Contains(hosted, service => service is DurableWorkHostedService);
        Assert.Contains(hosted, service => service is ConversationExecutionHostedService);
    }

    [Fact]
    public async Task Hosted_passes_log_the_exception_and_one_diagnostic_id()
    {
        await AssertPassAsync<TriggerSchedulerHostedService>(
            "Trigger scheduler pass failed.",
            (pass, logs, diagnostics) => new TriggerSchedulerHostedService(pass, TimeProvider.System, logs, diagnostics),
            service => service.RunPassAsync(CancellationToken.None));
        await AssertPassAsync<DurableWorkHostedService>(
            "Durable work pass failed.",
            (pass, logs, diagnostics) => new DurableWorkHostedService(pass, TimeProvider.System, logs, diagnostics),
            service => service.RunPassAsync(CancellationToken.None));
        await AssertPassAsync<DurableWorkIntakeHostedService>(
            "Durable intake pass failed.",
            (pass, logs, diagnostics) => new DurableWorkIntakeHostedService(pass, TimeProvider.System, logs, diagnostics),
            service => service.RunPassAsync(CancellationToken.None));
        await AssertPassAsync<ConversationExecutionHostedService>(
            "Conversation execution pass failed.",
            (pass, logs, diagnostics) => new ConversationExecutionHostedService(pass, TimeProvider.System, logs, diagnostics),
            service => service.RunPassAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Stopping_a_hosted_pass_does_not_mint_a_diagnostic_id()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        var logs = new DiagnosticLogCapture<TriggerSchedulerHostedService>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId]);
        var service = new TriggerSchedulerHostedService(
            _ => throw new OperationCanceledException(),
            TimeProvider.System,
            logs,
            diagnostics);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunPassAsync(stopping.Token));

        Assert.Empty(logs.Entries);
        Assert.Equal(DiagnosticId, diagnostics.NewId());
    }

    private static async Task AssertPassAsync<TService>(
        string message,
        Func<Func<CancellationToken, Task>, DiagnosticLogCapture<TService>, IDiagnosticIdSource, TService> create,
        Func<TService, Task> runPass)
        where TService : class
    {
        var failure = new InvalidOperationException("api_key=hosted-secret");
        var business = new DeterministicIdGenerator([BusinessId], [SessionId]);
        var logs = new DiagnosticLogCapture<TService>();
        var diagnostics = new QueueDiagnosticIdSource([DiagnosticId]);
        var service = create(_ => throw failure, logs, diagnostics);
        using var metrics = RuntimeMetricProbe.Start();
        using var listener = DiagnosticActivity.Listen();
        using var activity = RuntimeTelemetry.Activity.StartActivity("hosted-pass");
        Assert.NotNull(activity);

        await runPass(service);

        var entry = Assert.Single(logs.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Same(failure, entry.Exception);
        Assert.Equal(DiagnosticId, entry.Properties["DiagnosticId"]);
        Assert.Equal(DiagnosticId, entry.Scope["DiagnosticId"]);
        Assert.Equal(activity.TraceId.ToHexString(), entry.Scope["TraceId"]);
        Assert.Contains(message, entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("hosted-secret", entry.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", entry.Message, StringComparison.Ordinal);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.DoesNotContain(activity.TagObjects, tag => tag.Key is "DiagnosticId" or "TraceId");
        Assert.Equal(BusinessId, business.NewId());
        foreach (var tag in metrics.Tags)
        {
            Assert.DoesNotContain(tag.Key, RuntimeMetricProbe.IdentityTagNames);
            Assert.NotEqual(DiagnosticId, tag.Value);
        }
    }
}
