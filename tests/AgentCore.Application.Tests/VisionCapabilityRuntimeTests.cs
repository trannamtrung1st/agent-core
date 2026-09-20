using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace AgentCore.Application.Tests;

public sealed class VisionCapabilityRuntimeTests
{
    [Fact]
    public async Task Runtime_defense_rejects_processed_images_without_calling_the_model()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var inner = new ScriptedLanguageModel();
        var model = new RecordingLanguageModel(inner);
        var catalog = TestModelCatalogs.Synthetic();
        var validator = new UserTurnCapabilityValidator(attachments, catalog);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(output, attachments, processor, model, catalog, validator);
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);

        Assert.True(await runtime.SubmitUserTextAsync("Describe it.", attachmentIds: [uploaded.AttachmentId]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await output.WaitForAsync(item => item.Payload is ErrorOutput error
            && error.Code == "ModelCapabilityUnsupported", cts.Token);
        await runtime.WaitUntilIdleAsync();

        Assert.Empty(model.Requests);
        Assert.Equal(OutputActivity.Idle, runtime.Output);
        Assert.Contains(output.Items, item => item.Payload is StateChangedOutput state
            && state.OutputState == nameof(OutputActivity.ProcessingAttachments));
        Assert.DoesNotContain(output.Items, item => item.Payload is TextDeltaOutput);
    }

    [Fact]
    public async Task Vision_catalog_model_accepts_image_turn_end_to_end()
    {
        var attachments = new InMemoryAttachmentStore(TimeProvider.System);
        var processor = new AttachmentProcessor(attachments);
        var catalog = TestModelCatalogs.Real();
        var resolver = new RecordingResolver(
            new StaticLanguageModelResolver(new RecordingLanguageModel(new ScriptedLanguageModel())));
        var validator = new UserTurnCapabilityValidator(attachments, catalog);
        var output = new CapturingSessionOutput();
        await using var runtime = CreateRuntime(
            output,
            attachments,
            processor,
            new RecordingLanguageModel(new ScriptedLanguageModel()),
            catalog,
            validator,
            resolver,
            new SessionModelSelection(
                "gpt-4o-mini-2024-07-18",
                "primary-llm",
                "openai/gpt-4o-mini-2024-07-18",
                ModelSelectionSource.User,
                null));
        var uploaded = await attachments.UploadPendingAsync(
            runtime.SessionId,
            "photo.png",
            "image/png",
            new MemoryStream(PngBytes()),
            false);

        Assert.True(await runtime.SubmitUserTextAsync("Describe it.", attachmentIds: [uploaded.AttachmentId]));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await output.WaitForAsync(item => item.Payload is TextDeltaOutput, cts.Token);
        await runtime.WaitUntilIdleAsync();
        Assert.NotEmpty(resolver.Resolves);
        Assert.Equal("gpt-4o-mini-2024-07-18", resolver.Resolves[^1].Selection.CatalogKey);
    }

    private static SessionRuntime CreateRuntime(
        ISessionOutput output,
        IAttachmentStore attachments,
        IAttachmentProcessor processor,
        ILanguageModel model,
        IModelCatalog catalog,
        IUserTurnCapabilityValidator validator,
        ILanguageModelResolver? resolver = null,
        SessionModelSelection? selection = null)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 0, 0, 0, TimeSpan.Zero));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-0011-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940b842")]);
        var store = new InMemoryMemoryStore();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            SampleDefinitions.Examiner,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            time.GetUtcNow(),
            time.GetUtcNow(),
            ModelSelection: selection
            ?? new SessionModelSelection(
                "scripted-alpha",
                "primary-llm",
                "scripted-alpha",
                ModelSelectionSource.SystemDefault,
                "medium"));
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        return new SessionRuntime(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            attachments: attachments,
            processor: processor,
            modelResolver: resolver ?? new StaticLanguageModelResolver(model),
            catalog: catalog,
            turnCapabilities: validator);
    }

    private static byte[] PngBytes()
    {
        using var image = new Image<Rgba32>(2, 2);
        using var buffer = new MemoryStream();
        image.Save(buffer, new PngEncoder());
        return buffer.ToArray();
    }

    private sealed class RecordingResolver(ILanguageModelResolver inner) : ILanguageModelResolver
    {
        public List<(SessionModelSelection Selection, ModelPurpose Purpose)> Resolves { get; } = [];

        public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose)
        {
            Resolves.Add((selection, purpose));
            return inner.Resolve(selection, purpose);
        }
    }
}
