using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Context;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class AutomationSemanticAuthorizationTests
{


    [Theory(Timeout = 60000)]
    [InlineData(TriggerCommandAuthorizationDecision.Allow)]
    [InlineData(TriggerCommandAuthorizationDecision.Deny)]
    public async Task Current_timezone_clarification_supplies_bounded_history_as_evidence_without_granting_authority(TriggerCommandAuthorizationDecision decision)
    {
        var verifier = new Verifier(decision);
        var model = new ProposingModel { AskTimeZoneFirst = true };
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-clarification-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new Output());
        await runtime.AttachAsync();
        const string task = "Every day at 9am, review learned experience quietly in background.";
        Assert.True(await runtime.SubmitUserTextAsync(task));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(verifier.Requests);
        Assert.True(await runtime.SubmitUserTextAsync("Dùng múi giờ Việt Nam."));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        var request = Assert.Single(verifier.Requests);
        Assert.Equal("Dùng múi giờ Việt Nam.", request.CurrentUserText);
        using var history = JsonDocument.Parse(request.ConversationEvidenceJson!);
        Assert.Contains(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == task);
        // This fixture sends no display receipt, so unseen assistant prose cannot enter Evidence.
        Assert.DoesNotContain(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == "Which timezone should I use?");
        Assert.DoesNotContain(history.RootElement.EnumerateArray(), entry => entry.GetProperty("text").GetString() == request.CurrentUserText);
        Assert.True(request.ConversationEvidenceJson!.Length <= 12_000);
        var retained = await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null);
        Assert.Equal(decision == TriggerCommandAuthorizationDecision.Allow ? 1 : 0, retained.Count);
    }

    [Fact(Timeout = 60000)]
    public async Task Explicit_disabled_creation_persists_the_state_that_was_semantically_verified()
    {
        var model = new ProposingModel { Arguments = "{\"instructions\":\"Review experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"Asia/Ho_Chi_Minh\",\"executionTarget\":\"backgroundSession\",\"enabled\":false}" };
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.Allow);
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-disabled-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new Output());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Create a disabled daily review at 9 Vietnam time"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(Assert.Single(verifier.Requests).ProposedAction.GetProperty("proposedEnabled").GetBoolean());
        var created = Assert.Single(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
        Assert.Equal(AutomationStatus.Disabled, created.Status);
        Assert.Null(created.NextOccurrenceAtUtc);
    }

    [Fact(Timeout = 60000)]
    public async Task Unavailable_structured_timezone_is_a_schedule_error_before_semantic_verification()
    {
        var model = new ProposingModel { Arguments = "{\"instructions\":\"Review experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"America/NoSuchZone\",\"executionTarget\":\"backgroundSession\"}" };
        var verifier = new Verifier(TriggerCommandAuthorizationDecision.Allow);
        await using var host = Host(Path.Combine(Path.GetTempPath(), $"semantic-zone-{Guid.NewGuid():N}.db"), model, verifier);
        var services = host.Services;
        var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 24);
        var session = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(instance.InstanceId, SessionMode.Text);
        await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(session, new Output());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync("Review experience daily at 9 in this timezone"));
        await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(verifier.Requests);
        Assert.Equal("schedule_validation_failed", Assert.Single(model.Results).GetProperty("error").GetString());
        Assert.Empty(await services.GetRequiredService<ITriggerStore>().ListAsync(new(instance.InstanceId, LocalUserProfile.Id), null));
    }

    private static ExperienceHost Host(string db, ILanguageModel model, ITriggerCommandAuthorizer verifier) => new(db, model, configure: services =>
    { services.RemoveAll<ITriggerCommandAuthorizer>(); services.AddSingleton(verifier); });

    private sealed class Verifier(TriggerCommandAuthorizationDecision decision) : ITriggerCommandAuthorizer
    {
        public List<AutomationAuthorizationRequest> Requests { get; } = [];
        public ValueTask<TriggerCommandAuthorizationVerdict> AuthorizeAsync(AutomationAuthorizationRequest proposed, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Requests.Add(proposed); return ValueTask.FromResult(new TriggerCommandAuthorizationVerdict(decision)); }
    }

    private sealed class Output : ISessionOutput
    {
        public ConcurrentQueue<SessionOutput> Items { get; } = new();
        public ValueTask PublishAsync(SessionOutput output, CancellationToken ct = default)
        { Items.Enqueue(output); return ValueTask.CompletedTask; }
    }

    private sealed class ProposingModel(string toolName = ToolCatalog.AutomationCreate) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true,true,Tools:true,StructuredOutput:true);
        public List<JsonElement> Results { get; } = [];
        public string? Arguments { get; set; }
        public bool AskTimeZoneFirst { get; set; }
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask; ct.ThrowIfCancellationRequested();
            var transcript = request.Interaction ?? request.Messages;
            var user = transcript.ToList().FindLastIndex(m => m.Role == ModelRole.User);
            if (AskTimeZoneFirst)
            {
                AskTimeZoneFirst = false;
                yield return new ModelSemanticResponseReady(new("Which timezone should I use?", new(ModelSpeechMode.None, null), []));
                yield return new ModelCompleted(ModelStopReason.Completed);
                yield break;
            }
            var result = transcript.Skip(user+1).LastOrDefault(m => m.Role == ModelRole.Tool && m.Name == toolName);
            if (result is not null)
            {
                using var json = JsonDocument.Parse(result.Text); Results.Add(json.RootElement.Clone());
                yield return new ModelSemanticResponseReady(new("Automation result received.",new(ModelSpeechMode.None,null),[]));
                yield return new ModelCompleted(ModelStopReason.Completed); yield break;
            }
            if (request.Tools?.Any(t => t.Name == toolName) != true)
                yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), ToolCatalog.CapabilitiesLoad, JsonSerializer.Serialize(new { query = toolName, limit = 1 })));
            else yield return new ModelToolCallEvent(new(Guid.NewGuid().ToString("N"), toolName,
                Arguments ??
                "{\"instructions\":\"Review learned experience\",\"kind\":\"daily\",\"localTime\":\"09:00\",\"timeZone\":\"Asia/Ho_Chi_Minh\",\"executionTarget\":\"backgroundSession\",\"reportBack\":false}"));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }
}
