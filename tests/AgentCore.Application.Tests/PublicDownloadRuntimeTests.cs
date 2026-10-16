using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Execution;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Office;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.PublicWeb;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class PublicDownloadRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Owned_Run_downloads_edits_and_publishes_original_without_replaying_download_after_retry(bool retry)
    {
        var root = Path.Combine(Path.GetTempPath(), "download-runtime-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-14T04:00:00Z")); var ids = new SystemIdGenerator(clock);
            var owner = Guid.NewGuid(); var session = Guid.NewGuid();
            var definition = SampleDefinitions.Examiner with { Environment = new RoleEnvironment(ToolAllowlist: [ToolCatalog.WebDownload, ToolCatalog.OfficeDocumentInspect, ToolCatalog.OfficeDocumentEdit, ToolCatalog.ArtifactsCreateFromWorkspace]), Voice = new(false,"default",1), InitiativePolicy = new(false,60000,120000,1,[],0) };
            var snapshot = new SessionSnapshot(1, session, 1, definition, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, clock.GetUtcNow(), clock.GetUtcNow(), AgentInstanceId: owner);
            var memory = new InMemoryMemoryStore(); await memory.SaveAsync(snapshot, 0);
            var instances = new InMemoryAgentInstanceStore(); await instances.InsertAsync(new(owner, definition.Id, definition.Version, definition.Identity, AgentInstanceLifecycle.Active, clock.GetUtcNow(), clock.GetUtcNow()));
            var workspace = new FileSessionWorkspace(root, Path.Combine(root,"templates"), sessions: memory, instances: instances, sandboxes: new InMemorySandboxStore());
            var homeStore = new FileAgentInstanceWorkspaceStore(root, instances, new InMemorySandboxStore());
            var home = new AgentInstanceWorkspaceService(instances, homeStore, memory, workspace, new AdminLifecycleCoordinator());
            var office = new OfficeProcessor(); var original = office.CreateDocument(new("Public form", [new("Name", ["Applicant: ______", "Keep this text"], null)]), default).Bytes;
            var transport = new Transport(original); var artifacts = new InMemoryArtifactStore(clock);
            var executor = new SessionToolExecutor(workspace: workspace, artifacts: artifacts, publicWebFetcher: new PublicWebFetcher(transport), configurationGate: ToolConfigurationGates.AllowAll, agentWorkspace: home, officeProcessor: office);
            var model = new WorkflowModel(retry);
            await using var runtime = SessionRuntimeFixture.Create(snapshot, new SemanticResponseLanguageModel(model), new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll)),
                memory, new CapturingSessionOutput(), ids, clock, NullLogger.Instance, tools: executor, artifacts: new SessionArtifactAuthorizer(artifacts));
            await runtime.AttachAsync(); Assert.True(await runtime.SubmitUserTextAsync("Download the public Word template, fill the original form and return the completed file."));
            await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if (retry)
            {
                var waiting = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime));
                Assert.Equal(AgentRunStatus.WaitingToRetry, waiting.Status);
                Assert.True(AgentRunToolCallCheckpoint.TryRead(waiting.Checkpoint,out var restored));
                Assert.Contains(restored!,message=>message.Role==ModelRole.Tool && message.Name==ToolCatalog.WebDownload && message.Text.Contains("sha256"));
                Assert.DoesNotContain(AgentRunToolCallCheckpoint.PendingCalls(restored!),call=>call.Name==ToolCatalog.WebDownload);
                Assert.True(await SessionRuntimeFixture.DispatchRetryAsync(runtime));
                await runtime.WaitUntilIdleAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            var entry = Assert.Single(runtime.Snapshot.Entries, e => e.Role == ConversationRole.Assistant);
            Assert.True(entry.Status == EntryStatus.Completed, JsonSerializer.Serialize(entry.Failure));
            var artifact = Assert.Single(await artifacts.ListAsync(session)); Assert.Equal(artifact.ArtifactId.ToString(), Assert.Single(entry.Envelope!.Blocks).ArtifactId);
            var filled = await homeStore.ReadAsync(owner, "/home/form.docx"); Assert.Contains("Applicant: Nguyen An", JsonSerializer.Serialize(office.InspectDocument(filled.Bytes,0,20,default)));
            Assert.Equal(1, transport.Calls); Assert.NotEqual(original, filled.Bytes);
            var run = Assert.Single(await SessionRuntimeFixture.RunsForAsync(runtime)); Assert.Equal(AgentRunStatus.Completed,run.Status);
            Assert.DoesNotContain(Convert.ToBase64String(original), JsonSerializer.Serialize(model.Requests));
            Assert.True(JsonSerializer.Serialize(run.Checkpoint).Length < 256 * 1024);
            Assert.Contains(model.Requests, request => request.Messages.Any(message => message.Role == ModelRole.Tool && message.Name == ToolCatalog.WebDownload && message.Text.Contains("sha256")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root,true); }
    }

    private sealed class Transport(byte[] bytes) : IPublicWebTransport
    {
        public int Calls;
        public ValueTask<PublicWebTransportResponse> GetAsync(Uri uri, CancellationToken ct) => throw new InvalidOperationException();
        public ValueTask<PublicWebTransportResponse> SendAsync(PublicWebOutboundRequest request, CancellationToken ct)
        { Calls++; return ValueTask.FromResult(new PublicWebTransportResponse(200,null,null,bytes)); }
    }
    private sealed class WorkflowModel(bool retry) : ILanguageModel
    {
        public ModelCapabilities Capabilities { get; } = new(true,true,Tools:true,StructuredOutput:true);
        public List<ModelRequest> Requests { get; } = [];
        private bool failed;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request); await Task.CompletedTask;
            var results = request.Messages.Where(m => m.Role == ModelRole.Tool).ToArray();
            var downloaded = results.LastOrDefault(m => m.Name == ToolCatalog.WebDownload);
            var inspected = results.LastOrDefault(m => m.Name == ToolCatalog.OfficeDocumentInspect);
            var edited = results.LastOrDefault(m => m.Name == ToolCatalog.OfficeDocumentEdit);
            var published = results.LastOrDefault(m => m.Name == ToolCatalog.ArtifactsCreateFromWorkspace);
            ModelToolCall? call = null;
            if (downloaded is null) call = new("download",ToolCatalog.WebDownload,"""{"url":"https://official.example/form.docx","path":"/home/form.docx"}""");
            else if (retry && !failed) { failed = true; yield return new ModelFailed(new(ProviderErrorCode.Unavailable,"Synthetic transient failure")); yield break; }
            else if (inspected is null) call = new("inspect",ToolCatalog.OfficeDocumentInspect,"""{"path":"/home/form.docx"}""");
            else if (edited is null)
            {
                using var inspection = JsonDocument.Parse(inspected.Text); using var receipt = JsonDocument.Parse(downloaded.Text);
                var selector = inspection.RootElement.GetProperty("inspection").GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("text").GetString() == "Applicant: ______").GetProperty("selector").GetString();
                call = new("edit", ToolCatalog.OfficeDocumentEdit, JsonSerializer.Serialize(new { path="/home/form.docx", operations=new[] { new {operation="replace_paragraph",selector,text="Applicant: Nguyen An"} } }));
            }
            else if (published is null) call = new("publish",ToolCatalog.ArtifactsCreateFromWorkspace,"""{"path":"/home/form.docx","displayName":"completed.docx"}""");
            if (call is not null)
            { Assert.Contains(request.Tools!,t=>t.Name==call.Name); yield return new ModelToolCallEvent(call); yield return new ModelCompleted(ModelStopReason.ToolCalls); }
            else
            {
                using var receipt = JsonDocument.Parse(published!.Text);
                yield return new ModelTextDelta(JsonSerializer.Serialize(new { disposition="Complete", action=new {kind="chat.respond"}, displayText="Filled the original template. Paragraph properties and untouched content are preserved; individual run formatting is normalized.",speech=new {mode="none"},memory=Array.Empty<object>(),blocks=new[] {new {kind="artifactReference",artifactId=receipt.RootElement.GetProperty("artifactId").GetString()}} }));
                yield return new ModelCompleted(ModelStopReason.Completed);
            }
        }
    }
}
