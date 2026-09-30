using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Memory;
using AgentCore.Application.Testing;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class MemoryProposalAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceA = Guid.Parse("019944af-0030-7000-8000-0000000000a1");
    private static readonly Guid InstanceB = Guid.Parse("019944af-0030-7000-8000-0000000000a2");
    private static readonly Guid SessionA = Guid.Parse("019944af-0030-7000-8000-0000000000b1");
    private static readonly Guid SessionB = Guid.Parse("019944af-0030-7000-8000-0000000000b2");
    private static readonly Guid ProfileId = LocalUserProfile.Id;

    [Fact]
    public async Task Natural_wording_without_remember_stores_the_agent_proposal()
    {
        Assert.False(ExplicitUserMemoryRequests.TryParse("I prefer concise answers.", out _));
        var memories = Service();
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(memoryTurns:
            [
                [new MemoryProposal(
                    MemoryProposalOperation.Upsert,
                    MemoryKind.Preference,
                    "answer length",
                    "concise",
                    null,
                    MemoryProposalSource.AgentInferred)]
            ]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "I prefer concise answers.",
            Guid.Parse("019944af-0030-7000-8000-000000000011")));
        await runtime.WaitUntilIdleAsync();

        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        var item = Assert.Single(stored);
        Assert.Equal(MemoryKind.Preference, item.Kind);
        Assert.Equal("concise", item.Content);
        Assert.Equal("agent_inferred", item.Provenance.Source);
        var user = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        Assert.Equal(user.EntryId, Assert.Single(item.Provenance.SourceEntryIds));
        AssertReplyOmitsReceipt(AssistantText(runtime));
        var saved = AssistantReceipt(runtime);
        Assert.Equal(MemoryReceipt.Silent, saved.Presentation);
        Assert.Equal("stored", saved.Outcome);
        Assert.Equal("answer length", saved.Subject);
    }

    [Fact]
    public async Task Wording_outside_the_retired_parser_still_stores_a_proposal()
    {
        const string text = "Remind yourself that I don't want verbose explanations.";
        Assert.False(ExplicitUserMemoryRequests.TryParse(text, out _));
        var memories = Service();
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(memoryTurns:
            [
                [Proposal(MemoryKind.Preference, "explanation length", "not verbose", MemoryProposalSource.UserExplicit)]
            ]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(text, Guid.Parse("019944af-0030-7000-8000-000000000012")));
        await runtime.WaitUntilIdleAsync();
        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("user_explicit"));
        Assert.Equal("not verbose", Assert.Single(stored).Content);
        Assert.Equal("user_explicit", stored[0].Provenance.Source);
        AssertReplyOmitsReceipt(AssistantText(runtime));
        var receipt = AssistantReceipt(runtime);
        Assert.Equal(MemoryReceipt.Indicator, receipt.Presentation);
        Assert.Equal("Remembered", PublicMemoryReceipt.From(receipt)!.Label);
    }

    [Fact]
    public async Task Same_instance_and_profile_recalls_promoted_memory_in_a_new_session()
    {
        var memories = Service();
        var definition = Enabled();
        await using (var runtime = await RuntimeAsync(
            memories,
            definition,
            new ScriptedLanguageModel(memoryTurns: [[Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred)]])))
        {
            Assert.True(await runtime.SubmitPersistedUserTextAsync(
                "We decided the project codename is Atlas.",
                Guid.Parse("019944af-0030-7000-8000-000000000013")));
            await runtime.WaitUntilIdleAsync();
        }

        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(Profile(), 0);
        await sessions.SaveAsync(Snapshot(SessionB, definition), 0);
        var recall = new RecordingModel(new ScriptedLanguageModel());
        await using var second = Runtime((await sessions.LoadAsync(SessionB))!, sessions, memories, recall);
        await second.AttachAsync();
        Assert.True(await second.SubmitPersistedUserTextAsync(
            "What is the project codename?",
            Guid.Parse("019944af-0030-7000-8000-000000000014")));
        await second.WaitUntilIdleAsync();
        var learned = recall.LastRequest!.Messages.Single(message =>
            message.Text.StartsWith(SessionMemoryPrompt.LearnedDataLabel, StringComparison.Ordinal)).Text;
        Assert.Contains("Atlas", learned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Correction_updates_the_same_subject_without_a_memory_id()
    {
        var memories = Service();
        var definition = Enabled();
        var logger = NullLogger.Instance;
        Assert.Equal(
            MemoryAdmissionStatus.Stored,
            await MemoryAdmission.AdmitOneAsync(memories, definition, SessionA, InstanceA, Profile(), [], Guid.NewGuid(), Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred), logger));
        Assert.Equal(
            MemoryAdmissionStatus.Updated,
            await MemoryAdmission.AdmitOneAsync(memories, definition, SessionA, InstanceA, Profile(), [], Guid.NewGuid(), Proposal(MemoryKind.Fact, "project codename", "Borealis", MemoryProposalSource.AgentInferred), logger));
        var identity = await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, ProfileId),
            new MemorySearchQuery("codename", null),
            retrievalAllowed: true,
            Admission("agent_inferred"));
        Assert.Contains(identity, item => item.Content == "Borealis");
        Assert.DoesNotContain(identity, item => item.Content == "Atlas");
    }

    [Fact]
    public async Task Repeating_the_same_proposal_is_already_stored()
    {
        var memories = Service();
        var proposal = Proposal(MemoryKind.Fact, "editor", "Rider", MemoryProposalSource.AgentInferred);
        var logger = NullLogger.Instance;
        Assert.Equal(MemoryAdmissionStatus.Stored, await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, logger));
        Assert.Equal(MemoryAdmissionStatus.AlreadyStored, await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, logger));
        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        Assert.Single(stored);
    }

    [Fact]
    public async Task Disabled_memory_rejects_the_proposal_and_does_not_claim_a_save()
    {
        var memories = Service();
        var definition = SampleDefinitions.Support with { MemoryPolicy = MemoryPolicy.Disabled };
        await using var runtime = await RuntimeAsync(
            memories,
            definition,
            new ScriptedLanguageModel(memoryTurns: [[Proposal(MemoryKind.Fact, "editor", "Rider", MemoryProposalSource.AgentInferred)]]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "My favorite editor is Rider.",
            Guid.Parse("019944af-0030-7000-8000-000000000015")));
        await runtime.WaitUntilIdleAsync();
        AssertReplyOmitsReceipt(AssistantText(runtime));
        var rejected = AssistantReceipt(runtime);
        Assert.Equal(MemoryReceipt.Silent, rejected.Presentation);
        Assert.Equal("unavailable", rejected.Outcome);
        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
    }

    [Fact]
    public async Task Session_scope_hint_stays_inside_the_session()
    {
        var memories = Service();
        var proposal = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Fact,
            "scratch note",
            "today only",
            MemoryScopeHint.Session,
            MemoryProposalSource.AgentInferred);
        Assert.Equal(
            MemoryAdmissionStatus.StoredSessionOnly,
            await MemoryAdmission.AdmitOneAsync(memories, Enabled(), SessionA, InstanceA, Profile(), [], Guid.NewGuid(), proposal, NullLogger.Instance));
        Assert.Single(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
        Assert.Empty(await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("agent_inferred")));
    }

    [Fact]
    public async Task Sensitive_content_is_rejected_and_the_receipt_is_the_outcome()
    {
        var memories = Service();
        var proposal = Proposal(MemoryKind.Fact, "token", "sk-abcdefghijklmnopqrst", MemoryProposalSource.UserExplicit);
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(
                ["Understood."],
                memoryTurns: [[proposal]]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "Keep this token: sk-abcdefghijklmnopqrst",
            Guid.Parse("019944af-0030-7000-8000-000000000016")));
        await runtime.WaitUntilIdleAsync();

        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("user_explicit")));
        var text = AssistantText(runtime);
        Assert.StartsWith("Understood.", text, StringComparison.Ordinal);
        AssertReplyOmitsReceipt(text);
        var rejected = AssistantReceipt(runtime);
        Assert.Equal(MemoryReceipt.Explicit, rejected.Presentation);
        Assert.Equal("Not saved: token.", PublicMemoryReceipt.From(rejected)!.Label);
        var projected = PublicHistory.FromEntry(runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant));
        Assert.Equal("Understood.", projected.Text);
        Assert.Equal("Not saved: token.", Assert.Single(projected.MemoryReceipts!).Label);
    }

    [Fact]
    public async Task Custom_speech_stays_free_of_the_memory_receipt()
    {
        var memories = Service();
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(
                ["Noted.[[speech:Here is the result.]]"],
                memoryTurns: [[Proposal(MemoryKind.Fact, "token", "sk-abcdefghijklmnopqrst", MemoryProposalSource.UserExplicit)]]));
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "Store the token.",
            Guid.Parse("019944af-0030-7000-8000-000000000017")));
        await runtime.WaitUntilIdleAsync();

        var assistant = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Contains("Here is the result.", assistant.Envelope?.SpeechText, StringComparison.Ordinal);
        AssertReplyOmitsReceipt(assistant.Text);
        Assert.DoesNotContain("Memory was not saved", assistant.Envelope?.SpeechText, StringComparison.Ordinal);
        Assert.Equal(MemoryReceipt.Explicit, Assert.Single(assistant.Envelope!.MemoryReceipts!).Presentation);
        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("user_explicit")));
    }

    [Fact]
    public async Task Failed_response_does_not_admit_staged_memory()
    {
        var memories = Service();
        await using var runtime = await RuntimeAsync(memories, Enabled(), new FailingAfterReadyModel());
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "Remember the project codename Atlas.",
            Guid.Parse("019944af-0030-7000-8000-000000000018")));
        await runtime.WaitUntilIdleAsync();

        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
        Assert.DoesNotContain("Memory saved", AssistantText(runtime), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelled_response_discards_staged_memory()
    {
        var memories = Service();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = new GatedReadyModel(gate);
        await using var runtime = await RuntimeAsync(memories, Enabled(), model);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "Remember the project codename Atlas.",
            Guid.Parse("019944af-0030-7000-8000-000000000019")));
        await model.Parked.WaitAsync(TimeSpan.FromSeconds(5));
        await runtime.CancelActiveResponseAsync();
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
    }

    [Fact]
    public async Task Later_semantic_response_replaces_the_staged_proposal()
    {
        var memories = Service();
        await using var runtime = await RuntimeAsync(memories, Enabled(), new ReplacingSemanticModel());
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "The project codename changed.",
            Guid.Parse("019944af-0030-7000-8000-00000000001a")));
        await runtime.WaitUntilIdleAsync();

        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        var item = Assert.Single(stored);
        Assert.Equal("Borealis", item.Content);
        AssertSilentStored(runtime, "project codename");
    }

    [Fact]
    public async Task Repeated_semantic_output_admits_the_proposal_once()
    {
        var memories = Service();
        await using var runtime = await RuntimeAsync(memories, Enabled(), new RepeatedSemanticModel());
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "My editor is Rider.",
            Guid.Parse("019944af-0030-7000-8000-00000000001b")));
        await runtime.WaitUntilIdleAsync();

        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        Assert.Equal("Rider", Assert.Single(stored).Content);
        AssertSilentStored(runtime, "editor");
        Assert.DoesNotContain("already saved", AssistantText(runtime), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_from_a_new_session_removes_promoted_memory()
    {
        var memories = Service();
        var definition = Enabled();
        Assert.Equal(
            MemoryAdmissionStatus.Stored,
            await MemoryAdmission.AdmitOneAsync(
                memories,
                definition,
                SessionA,
                InstanceA,
                Profile(),
                [],
                Guid.NewGuid(),
                Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred),
                NullLogger.Instance));
        var status = await MemoryAdmission.AdmitOneAsync(
            memories,
            definition,
            SessionB,
            InstanceA,
            Profile(),
            [],
            Guid.NewGuid(),
            new MemoryProposal(
                MemoryProposalOperation.Delete,
                MemoryKind.Fact,
                "project codename",
                string.Empty,
                null,
                MemoryProposalSource.UserExplicit),
            NullLogger.Instance);
        Assert.Equal(MemoryAdmissionStatus.Deleted, status);
        Assert.Empty(await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, ProfileId),
            new MemorySearchQuery("codename", null),
            retrievalAllowed: true,
            Admission("agent_inferred")));
    }

    [Fact]
    public async Task User_scope_correction_updates_the_existing_user_item()
    {
        var memories = Service();
        var definition = SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(SessionMemory: true, UserPromotion: true, UserRetrieval: true)
        };
        var logger = NullLogger.Instance;
        Assert.Equal(
            MemoryAdmissionStatus.Stored,
            await MemoryAdmission.AdmitOneAsync(
                memories,
                definition,
                SessionA,
                InstanceA,
                Profile(),
                [],
                Guid.NewGuid(),
                new MemoryProposal(MemoryProposalOperation.Upsert, MemoryKind.Fact, "project codename", "Atlas", MemoryScopeHint.User, MemoryProposalSource.AgentInferred),
                logger));
        Assert.Equal(
            MemoryAdmissionStatus.Updated,
            await MemoryAdmission.AdmitOneAsync(
                memories,
                definition,
                SessionA,
                InstanceA,
                Profile(),
                [],
                Guid.NewGuid(),
                new MemoryProposal(MemoryProposalOperation.Upsert, MemoryKind.Fact, "project codename", "Borealis", MemoryScopeHint.User, MemoryProposalSource.AgentInferred),
                logger));
        var user = await memories.SearchUserAsync(
            new TrustedUserOwner(ProfileId),
            new MemorySearchQuery("codename", null),
            retrievalAllowed: true,
            Admission("agent_inferred"));
        Assert.Equal("Borealis", Assert.Single(user).Content);
    }

    [Fact]
    public async Task Proactive_response_does_not_attach_an_older_user_entry()
    {
        var memories = Service();
        var proposal = Proposal(MemoryKind.Fact, "shipment note", "left the dock", MemoryProposalSource.AgentInferred);
        await using var runtime = await RuntimeAsync(
            memories,
            Enabled(),
            new ScriptedLanguageModel(memoryTurns: [[], [proposal]]),
            new SpeakBrain());
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "Where is the order?",
            Guid.Parse("019944af-0030-7000-8000-00000000001c")));
        await runtime.WaitUntilIdleAsync();
        await runtime.SubmitEnvironmentAsync(new EnvironmentEvent(
            Guid.Parse("019944af-0030-7000-8000-00000000001d"),
            "order_status_changed",
            new Dictionary<string, string> { ["orderReference"] = "1001", ["status"] = "shipped" }));
        await runtime.WaitUntilIdleAsync();

        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        var item = Assert.Single(stored);
        Assert.Equal("left the dock", item.Content);
        Assert.Empty(item.Provenance.SourceEntryIds);
    }

    [Fact]
    public async Task Native_structured_output_saves_the_proposal()
    {
        var memories = Service();
        var proposal = Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred);
        var model = new FixedCapabilitiesModel(
            new ScriptedLanguageModel(memoryTurns: [[proposal]]),
            new ModelCapabilities(true, true, Tools: true, StructuredOutput: true));
        await using var runtime = await RuntimeAsync(memories, Enabled(), model);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "The project codename is Atlas.",
            Guid.Parse("019944af-0030-7000-8000-000000000021")));
        await runtime.WaitUntilIdleAsync();
        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        Assert.Equal("Atlas", Assert.Single(stored).Content);
        AssertSilentStored(runtime, "project codename");
    }

    [Fact]
    public async Task Function_channel_normalizes_a_proposal_and_saves_it()
    {
        var memories = Service();
        var model = new ResponseFunctionModel();
        await using var runtime = await RuntimeAsync(memories, Enabled(), model);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "The project codename is Atlas.",
            Guid.Parse("019944af-0030-7000-8000-000000000022")));
        await runtime.WaitUntilIdleAsync();
        var stored = await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"));
        Assert.Equal("Atlas", Assert.Single(stored).Content);
        AssertSilentStored(runtime, "project codename");
        Assert.Contains("can propose new learned memory", model.LastRequest!.Messages.Single(message => message.Text.Contains("Memory capability:", StringComparison.Ordinal)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plain_text_marker_still_saves_without_a_reliable_channel()
    {
        var memories = Service();
        var model = new FixedCapabilitiesModel(
            new ScriptedLanguageModel(memoryTurns: [[Proposal(MemoryKind.Fact, "editor", "Rider", MemoryProposalSource.AgentInferred)]]),
            new ModelCapabilities(true, true, Tools: false, StructuredOutput: false));
        await using var runtime = await RuntimeAsync(memories, Enabled(), model);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "My editor is Rider.",
            Guid.Parse("019944af-0030-7000-8000-000000000023")));
        await runtime.WaitUntilIdleAsync();
        Assert.Equal("Rider", Assert.Single(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred"))).Content);
        AssertSilentStored(runtime, "editor");
    }

    [Fact]
    public async Task Plain_text_without_a_marker_does_not_imply_a_save()
    {
        var memories = Service();
        var recorded = new RecordingModel(new ScriptedLanguageModel(["Sure, I'll remember that."]));
        var model = new FixedCapabilitiesModel(
            recorded,
            new ModelCapabilities(true, true, Tools: false, StructuredOutput: false));
        await using var runtime = await RuntimeAsync(memories, Enabled(), model);
        Assert.True(await runtime.SubmitPersistedUserTextAsync(
            "Remember that I prefer tea.",
            Guid.Parse("019944af-0030-7000-8000-000000000024")));
        await runtime.WaitUntilIdleAsync();
        Assert.Empty(await memories.SearchAsync(new TrustedMemoryOwner(SessionA), new MemorySearchQuery(null, null), Admission("agent_inferred")));
        var text = AssistantText(runtime);
        Assert.Contains("Sure, I'll remember that.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory saved", text, StringComparison.Ordinal);
        var capability = recorded.LastRequest!.Messages.Single(message => message.Text.Contains("Memory capability:", StringComparison.Ordinal)).Text;
        Assert.Contains("no reliable channel", capability, StringComparison.Ordinal);
        Assert.Contains("Do not claim that information was saved", capability, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_marker_drops_application_and_admin_sources()
    {
        var parsed = MemoryProposalCodec.ParseMarkerPayload(
            """
            [{"operation":"upsert","kind":"fact","subject":"rank","content":"hidden","source":"admin"},{"operation":"upsert","kind":"fact","subject":"tone","content":"warm","source":"userExplicit"},{"operation":"upsert","kind":"fact","subject":"channel","content":"app","source":"application"}]
            """);
        var kept = Assert.Single(parsed);
        Assert.Equal("tone", kept.Subject);
        Assert.Equal(MemoryProposalSource.UserExplicit, kept.Source);
    }

    [Fact]
    public async Task Another_instance_or_profile_cannot_read_the_promoted_memory()
    {
        var memories = Service();
        await MemoryAdmission.AdmitOneAsync(
            memories,
            Enabled(),
            SessionA,
            InstanceA,
            Profile(),
            [],
            Guid.NewGuid(),
            Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred),
            NullLogger.Instance);
        var otherInstance = await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceB, ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("agent_inferred"));
        Assert.Empty(otherInstance);
        var otherProfile = new UserProfile(Guid.Parse("019944af-0030-7000-8000-0000000000c9"), 1, new Dictionary<string, UserProfileValue>(), Now);
        Assert.Empty(await memories.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, otherProfile.ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("agent_inferred")));
    }

    [Fact]
    public async Task User_promotion_receipt_records_all_covered_scopes()
    {
        var memories = Service();
        var definition = SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(SessionMemory: true, IdentityUserPromotion: true, UserPromotion: true, UserRetrieval: true)
        };
        var results = await MemoryAdmission.AdmitAsync(
            memories,
            definition,
            SessionA,
            InstanceA,
            Profile(),
            [],
            Guid.NewGuid(),
            [
                new MemoryProposal(
                    MemoryProposalOperation.Upsert,
                    MemoryKind.Fact,
                    "project codename",
                    "Atlas",
                    MemoryScopeHint.User,
                    MemoryProposalSource.UserExplicit)
            ],
            NullLogger.Instance);
        var result = Assert.Single(results);
        Assert.Equal(MemoryAdmissionStatus.Stored, result.Status);
        Assert.Equal(["session", "identityUser", "user"], result.AffectedScopes);
        var receipt = MemoryReceiptProjection.FromResult(result);
        Assert.Equal(["session", "identityUser", "user"], receipt.Scopes);
    }

    [Fact]
    public async Task Partial_user_promotion_keeps_outcome_coherent_with_scopes()
    {
        var inner = Service();
        var memories = new StructuredMemoryServiceIntercept(inner) { BlockIdentityPromotion = true };
        var definition = SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(SessionMemory: true, IdentityUserPromotion: true, UserPromotion: true, UserRetrieval: true)
        };
        var result = Assert.Single(await MemoryAdmission.AdmitAsync(
            memories,
            definition,
            SessionA,
            InstanceA,
            Profile(),
            [],
            Guid.NewGuid(),
            [
                new MemoryProposal(
                    MemoryProposalOperation.Upsert,
                    MemoryKind.Fact,
                    "project codename",
                    "Atlas",
                    MemoryScopeHint.User,
                    MemoryProposalSource.UserExplicit)
            ],
            NullLogger.Instance));
        Assert.Equal(MemoryAdmissionStatus.Stored, result.Status);
        Assert.Equal(["session", "user"], result.AffectedScopes);
        var receipt = MemoryReceiptProjection.FromResult(result);
        Assert.Equal("stored", receipt.Outcome);
        Assert.Equal(["session", "user"], receipt.Scopes);
        Assert.Empty(await inner.SearchIdentityUserAsync(
            new TrustedIdentityUserOwner(InstanceA, ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("user_explicit")));
        Assert.Single(await inner.SearchUserAsync(
            new TrustedUserOwner(ProfileId),
            new MemorySearchQuery(null, null),
            retrievalAllowed: true,
            Admission("user_explicit")));
    }

    [Fact]
    public async Task Application_command_uses_the_same_admission_seam()
    {
        var memories = Service();
        var proposal = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Decision,
            "production database",
            "PostgreSQL",
            null,
            MemoryProposalSource.Application);
        var status = await MemoryAdmission.AdmitOneAsync(
            memories,
            Enabled(),
            SessionA,
            InstanceA,
            Profile(),
            [],
            Guid.Empty,
            proposal,
            NullLogger.Instance);
        Assert.Equal(MemoryAdmissionStatus.Stored, status);
        var stored = Assert.Single(await memories.SearchAsync(
            new TrustedMemoryOwner(SessionA),
            new MemorySearchQuery(null, null),
            Admission("application")));
        Assert.Equal("application", stored.Provenance.Source);
        Assert.Equal("PostgreSQL", stored.Content);
    }

    private static string AssistantText(SessionRuntime runtime) =>
        runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Text;

    private static MemoryReceipt AssistantReceipt(SessionRuntime runtime) =>
        Assert.Single(runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant).Envelope!.MemoryReceipts!);

    private static void AssertReplyOmitsReceipt(string text)
    {
        Assert.DoesNotContain("Memory saved", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory was not saved", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory updated", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory removed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Memory already", text, StringComparison.Ordinal);
    }

    private static void AssertSilentStored(SessionRuntime runtime, string subject)
    {
        AssertReplyOmitsReceipt(AssistantText(runtime));
        var receipt = AssistantReceipt(runtime);
        Assert.Equal(MemoryReceipt.Silent, receipt.Presentation);
        Assert.Equal("stored", receipt.Outcome);
        Assert.Equal(subject, receipt.Subject);
        Assert.Null(PublicHistory.FromEntry(runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant)).MemoryReceipts);
    }

    private static MemoryProposal Proposal(MemoryKind kind, string subject, string content, MemoryProposalSource source) =>
        new(MemoryProposalOperation.Upsert, kind, subject, content, null, source);

    private static MemoryAdmissionContext Admission(string source) =>
        new(source, [], new HashSet<string>(StringComparer.Ordinal));

    private static StructuredMemoryService Service() =>
        new(new InMemoryStructuredMemoryStore(), new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0031-7000-8000-{index:D12}")),
            [Guid.Parse("019944af-0030-7000-8000-0000000000ff")]), new FakeTimeProvider(Now));

    private static async Task<SessionRuntime> RuntimeAsync(
        StructuredMemoryService memories,
        AgentDefinition definition,
        ILanguageModel model,
        IAgentBrain? brain = null)
    {
        var sessions = new InMemoryMemoryStore();
        await sessions.SaveProfileAsync(Profile(), 0);
        await sessions.SaveAsync(Snapshot(SessionA, definition), 0);
        var runtime = Runtime((await sessions.LoadAsync(SessionA))!, sessions, memories, model, brain);
        await runtime.AttachAsync();
        return runtime;
    }

    private static SessionRuntime Runtime(
        SessionSnapshot snapshot,
        IMemoryStore sessions,
        IStructuredMemoryService memories,
        ILanguageModel model,
        IAgentBrain? brain = null) =>
        new(
            snapshot,
            model,
            brain ?? new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            new DeterministicIdGenerator(
                Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0032-7000-8000-{index:D12}")),
                [Guid.Parse("019944af-0030-7000-8000-0000000000fe")]),
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            structuredMemory: memories);

    private static AgentDefinition Enabled() =>
        SampleDefinitions.Support with
        {
            MemoryPolicy = new MemoryPolicy(
                SessionMemory: true,
                IdentityUserPromotion: true,
                IdentityUserRetrieval: true)
        };

    private static UserProfile Profile() =>
        new(ProfileId, 1, new Dictionary<string, UserProfileValue>(), Now);

    private static SessionSnapshot Snapshot(Guid sessionId, AgentDefinition definition) =>
        new(
            1,
            sessionId,
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: sessionId == SessionB ? InstanceA : InstanceA);

    private sealed class FixedCapabilitiesModel(ILanguageModel inner, ModelCapabilities capabilities) : ILanguageModel
    {
        public ModelCapabilities Capabilities => capabilities;

        public IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default) =>
            inner.GenerateAsync(request, cancellationToken);
    }

    private sealed class ResponseFunctionModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true, StructuredOutput: false);

        public ModelRequest? LastRequest { get; private set; }

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await Task.Yield();
            var json = """
                {"displayText":"Noted.","speech":{"mode":"same","text":null},"blocks":[],"memory":[{"operation":"upsert","kind":"fact","subject":"project codename","content":"Atlas","scopeHint":null,"source":"agentInferred"}]}
                """;
            yield return new ModelToolCallEvent(new ModelToolCall("call-1", AssistantResponseSchema.ResponseFunctionName, json));
            yield return new ModelCompleted(ModelStopReason.ToolCalls);
        }
    }

    private sealed class SpeakBrain : IAgentBrain
    {
        public ValueTask<AgentDecision> DecideAsync(
            AgentContext context,
            Guid responseId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDecision>(new Speak(new PromptContextBuilder().Build(context, responseId)));
    }

    private sealed class FailingAfterReadyModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return Ready("Noted.", Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred));
            yield return new ModelFailed(new ProviderFailure(ProviderErrorCode.Unavailable, "Synthetic failure."));
        }
    }

    private sealed class GatedReadyModel(TaskCompletionSource gate) : ILanguageModel
    {
        private readonly TaskCompletionSource _parked = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Parked => _parked.Task;

        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Ready("Noted.", Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred));
            _parked.TrySetResult();
            try
            {
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class ReplacingSemanticModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return Ready("First.", Proposal(MemoryKind.Fact, "project codename", "Atlas", MemoryProposalSource.AgentInferred));
            yield return Ready("Second.", Proposal(MemoryKind.Fact, "project codename", "Borealis", MemoryProposalSource.AgentInferred));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private sealed class RepeatedSemanticModel : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true, true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            var proposal = Proposal(MemoryKind.Fact, "editor", "Rider", MemoryProposalSource.AgentInferred);
            yield return Ready("Noted.", proposal);
            yield return Ready("Noted.", proposal);
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
    }

    private static ModelSemanticResponseReady Ready(string text, MemoryProposal proposal) =>
        new(new ModelSemanticResponse(
            text,
            new ModelSpeechProjection(ModelSpeechMode.Same, null),
            [],
            [proposal]));

    private sealed class RecordingModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelRequest? LastRequest { get; private set; }

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }
}
