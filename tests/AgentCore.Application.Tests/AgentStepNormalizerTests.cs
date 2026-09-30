using AgentCore.Application.Execution;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Tests;

public sealed class AgentStepNormalizerTests
{
    [Fact]
    public void Semantic_response_maps_to_complete_chat_action_and_sidecar_memory()
    {
        var proposal = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Fact,
            "editor",
            "Rider",
            MemoryScopeHint.User,
            MemoryProposalSource.AgentInferred);
        var semantic = new ModelSemanticResponse(
            "Shown",
            new ModelSpeechProjection(ModelSpeechMode.Custom, "Spoken"),
            [new ModelResponseBlock(ModelResponseBlockKind.Markdown, "**Hi**")],
            [proposal]);

        var accepted = Assert.IsType<AgentStepAccepted>(
            AgentStepNormalizer.Normalize(AgentStepNormalizer.FromSemanticResponse(semantic)));
        var step = accepted.Step;

        Assert.Equal(AgentStepDisposition.Complete, step.Disposition);
        var chat = Assert.IsType<ChatRespondAction>(Assert.Single(step.Actions));
        Assert.Equal("Shown", chat.DisplayText);
        Assert.Equal(ModelSpeechMode.Custom, chat.Speech.Mode);
        Assert.Equal("Spoken", chat.Speech.Text);
        Assert.Equal("**Hi**", Assert.Single(chat.Blocks).Text);
        Assert.Same(proposal, Assert.Single(step.MemoryProposals));
        Assert.DoesNotContain(step.Actions, action => action is not ChatRespondAction);
    }

    [Fact]
    public void Empty_actions_are_a_valid_step_without_chat()
    {
        var accepted = Assert.IsType<AgentStepAccepted>(
            AgentStepNormalizer.Normalize(new AgentStepCandidate(nameof(AgentStepDisposition.Wait), [])));
        Assert.Equal(AgentStepDisposition.Wait, accepted.Step.Disposition);
        Assert.Empty(accepted.Step.Actions);
        Assert.Empty(accepted.Step.MemoryProposals);
    }

    [Theory]
    [InlineData("Frobnicate")]
    [InlineData("complete")]
    [InlineData("1")]
    public void Unknown_disposition_fails_closed(string disposition)
    {
        var rejected = Reject(new AgentStepCandidate(disposition, []));
        Assert.Equal(AgentStepRejectionCategory.UnknownDisposition, rejected.Category);
        Assert.Equal(ProviderErrorCode.InvalidResponse, rejected.Code);
        Assert.Equal("unknownDisposition", rejected.FailureReason);
    }

    [Fact]
    public void Unknown_action_fails_closed()
    {
        var rejected = Reject(Candidate(kind: "teams.reply"));
        Assert.Equal(AgentStepRejectionCategory.UnknownAction, rejected.Category);
        Assert.Equal("unknownAction", rejected.FailureReason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_disposition_is_malformed(string? disposition)
    {
        var rejected = Reject(new AgentStepCandidate(disposition, []));
        Assert.Equal(AgentStepRejectionCategory.MalformedPayload, rejected.Category);
        Assert.Equal("malformedPayload", rejected.FailureReason);
    }

    [Fact]
    public void Null_candidate_null_actions_duplicate_chat_and_bad_payload_are_malformed()
    {
        Assert.Equal(AgentStepRejectionCategory.MalformedPayload, Reject(null).Category);
        Assert.Equal(
            AgentStepRejectionCategory.MalformedPayload,
            Reject(new AgentStepCandidate(nameof(AgentStepDisposition.Complete), null)).Category);
        Assert.Equal(
            AgentStepRejectionCategory.MalformedPayload,
            Reject(new AgentStepCandidate(
                nameof(AgentStepDisposition.Complete),
                [Chat(), Chat()])).Category);
        Assert.Equal(
            AgentStepRejectionCategory.MalformedPayload,
            Reject(Candidate(omitSpeech: true)).Category);
        Assert.Equal(
            AgentStepRejectionCategory.MalformedPayload,
            Reject(Candidate(speech: new ModelSpeechProjection((ModelSpeechMode)99, null))).Category);
        Assert.Equal(
            AgentStepRejectionCategory.MalformedPayload,
            Reject(Candidate(blocks: [new ModelResponseBlock((ModelResponseBlockKind)99)])).Category);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("destination")]
    [InlineData("profile")]
    [InlineData("tenant")]
    [InlineData("recipient")]
    public void Model_supplied_destination_fails_closed_without_a_step(string field)
    {
        var action = field switch
        {
            "session" => Chat(sessionId: "other-session"),
            "destination" => Chat(destination: "other-session"),
            "profile" => Chat(profileId: "profile-2"),
            "tenant" => Chat(tenant: "tenant-2"),
            _ => Chat(recipient: "user-2")
        };
        var rejected = Reject(new AgentStepCandidate(nameof(AgentStepDisposition.Complete), [action]));
        Assert.Equal(AgentStepRejectionCategory.ModelSuppliedDestination, rejected.Category);
        Assert.Equal(ProviderErrorCode.InvalidResponse, rejected.Code);
        Assert.Equal("modelSuppliedDestination", rejected.FailureReason);
    }

    [Fact]
    public void Destination_on_an_unknown_action_is_still_a_destination_failure()
    {
        var rejected = Reject(new AgentStepCandidate(
            nameof(AgentStepDisposition.Complete),
            [new AgentActionCandidate("teams.reply", SessionId: "other-session")]));
        Assert.Equal(AgentStepRejectionCategory.ModelSuppliedDestination, rejected.Category);
    }

    [Fact]
    public void Normalized_step_cannot_carry_reasoning()
    {
        var accepted = Assert.IsType<AgentStepAccepted>(
            AgentStepNormalizer.Normalize(AgentStepNormalizer.FromSemanticResponse(Semantic("Shown"))));
        var names = typeof(AgentStep).GetProperties().Select(property => property.Name);
        Assert.DoesNotContain(names, name => name.Contains("Reason", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(ChatRespondAction).GetProperties().Select(property => property.Name), name =>
            name.Contains("Reason", StringComparison.OrdinalIgnoreCase)
            || name is "SessionId" or "Destination" or "ProfileId" or "Tenant" or "Recipient");
        var chat = Assert.IsType<ChatRespondAction>(Assert.Single(accepted.Step.Actions));
        Assert.Equal("Shown", chat.DisplayText);
        Assert.DoesNotContain("secret-thought", chat.DisplayText, StringComparison.Ordinal);
    }

    [Fact]
    public void Chat_respond_is_not_a_provider_tool()
    {
        Assert.False(ToolRegistry.TryGet(AgentStepNormalizer.ChatRespondKind, out _));
        Assert.False(ToolRegistry.TryGet("agentcore.wait", out _));
        Assert.False(ToolRegistry.TryGet("agentcore.complete", out _));
        Assert.False(ToolRegistry.TryGet("agentcore.continue", out _));
        Assert.DoesNotContain(
            ToolRegistry.AllKnownNames(),
            name => string.Equals(name, AgentStepNormalizer.ChatRespondKind, StringComparison.Ordinal));
    }

    private static AgentStepRejection Reject(AgentStepCandidate? candidate) =>
        Assert.IsType<AgentStepRejected>(AgentStepNormalizer.Normalize(candidate)).Rejection;

    private static AgentStepCandidate Candidate(
        string kind = AgentStepNormalizer.ChatRespondKind,
        ModelSpeechProjection? speech = null,
        IReadOnlyList<ModelResponseBlock>? blocks = null,
        bool omitSpeech = false) =>
        new(
            nameof(AgentStepDisposition.Complete),
            [
                new AgentActionCandidate(
                    kind,
                    "Shown",
                    omitSpeech ? null : speech ?? new ModelSpeechProjection(ModelSpeechMode.Same, null),
                    blocks ?? [])
            ]);

    private static AgentActionCandidate Chat(
        string? sessionId = null,
        string? destination = null,
        string? profileId = null,
        string? tenant = null,
        string? recipient = null) =>
        new(
            AgentStepNormalizer.ChatRespondKind,
            "Shown",
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [],
            sessionId,
            destination,
            profileId,
            tenant,
            recipient);

    private static ModelSemanticResponse Semantic(string text) =>
        new(text, new ModelSpeechProjection(ModelSpeechMode.Same, null), []);
}
