using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxScriptTests
{
    [Fact]
    public async Task Canonical_background_Workbench_uses_exact_consent_and_retained_continuation_before_idle_fallback()
    {
        EffectiveContextSnapshot Context(string task) => new([
            ContextSection.Core(CoreProcedure.OperatingContract), ContextSection.Core(CoreProcedure.Background),
            ContextSection.Runtime(ContextSectionId.AgentIdentity, ContextSourceKind.Definition, "Native worker"),
            ContextSection.Runtime(ContextSectionId.AgentInstructions, ContextSourceKind.Definition, "Perform the exact native task."),
            ContextSection.Runtime(ContextSectionId.RunTask, ContextSourceKind.AgentRun, task)
        ]);
        var authorization = new ModelToolDefinition(ToolCatalog.RequestAuthorization, "Exact consent",
            TenantApprovalToolContract.Descriptor(settings: false, credentials: false,
                sandboxProfiles: ["p-" + Guid.NewGuid().ToString("N")], workspaceArtifactPublication: true).ModelDefinition.ParametersJson);
        var model = new ScriptedLanguageModel();
        async Task<ModelToolCall> Generate(EffectiveContextSnapshot context, IReadOnlyList<ModelMessage>? transcript = null)
        {
            var request = ContextAssembler.Create(Guid.NewGuid(), context, transcript) with { Tools = [authorization] };
            var events = new List<ModelGenerationEvent>();
            await foreach (var item in model.GenerateAsync(request)) events.Add(item);
            return Assert.IsType<ModelToolCallEvent>(events[0]).Call;
        }
        var initial = await Generate(Context("synthetic-sandbox:canonical-demo"));
        Assert.Equal(ToolCatalog.RequestAuthorization, initial.Name);
        using var command = JsonDocument.Parse(initial.ArgumentsJson);
        Assert.Equal("run_sandbox_command", command.RootElement.GetProperty("operation").GetString());
        var next = await Generate(Context("Continue the exact completed operation."), [
            new(ModelRole.Assistant, "", ToolCalls: [initial]),
            new(ModelRole.Tool, "{\"outcome\":\"completed\",\"sandbox\":{\"status\":\"Completed\",\"exitCode\":0}}", ToolCallId: initial.Id, Name: initial.Name)
        ]);
        Assert.Equal(ToolCatalog.RequestAuthorization, next.Name);
        using var publication = JsonDocument.Parse(next.ArgumentsJson);
        Assert.Equal("publish_workspace_artifact", publication.RootElement.GetProperty("operation").GetString());
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("unknown")]
    [InlineData("failed")]
    public void Tenant_Workbench_requires_actual_completed_command_and_publication_receipts(string outcome)
    {
        var context = new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract),
            ContextSection.Runtime(ContextSectionId.AgentIdentity, ContextSourceKind.Definition, "Tenant Workbench"),
            ContextSection.Runtime(ContextSectionId.AgentInstructions, ContextSourceKind.Definition, "Perform the exact task.")
        ]);
        var interaction = new List<ModelMessage> { new(ModelRole.User, "synthetic-sandbox:canonical-demo") };
        var profileId = "p-" + Guid.NewGuid().ToString("N");
        var tools = new[] {
            new ModelToolDefinition(ToolCatalog.RequestAuthorization, "Exact consent", TenantSandboxAuthorizationContract.Schema([profileId]).ToJsonString()),
            new ModelToolDefinition(ToolCatalog.ArtifactsCreateFromWorkspace, "Publish", "{}") };
        ModelRequest Request() => ContextAssembler.Create(Guid.NewGuid(), context, interaction) with { Tools = tools };
        var initial = OpenSandboxScript.Generate(Request())!;
        var command = Assert.IsType<ModelToolCallEvent>(initial[0]).Call;
        Assert.Equal(ToolCatalog.RequestAuthorization, command.Name);
        using var proposed = JsonDocument.Parse(command.ArgumentsJson);
        var intent = TenantSandboxAuthorizationContract.Parse(proposed.RootElement);
        Assert.Equal(profileId, intent.ProfileId);
        Assert.Equal("/working", intent.Command.Cwd);
        Assert.Equal(120, intent.Command.TimeoutSeconds);
        interaction.Add(new(ModelRole.Assistant, "", ToolCalls: [command]));
        interaction.Add(new(ModelRole.Tool, JsonSerializer.Serialize(new { approvalStatus = "approved", outcome = "completed",
            sandbox = outcome == "approved" ? new { status = "Completed", exitCode = 0 } : new { status = "Failed", exitCode = 7 } }), ToolCallId: command.Id, Name: command.Name));
        // Continuation task wording changes; only the retained current checkpoint
        // call/result pair selects the next step, never replaying the command.
        interaction[0] = interaction[0] with { Text = "Continue the exact saved operation." };
        if (outcome == "unknown")
            interaction[^1] = new(ModelRole.Tool, "{\"approvalStatus\":\"approved\",\"resultUnavailable\":true}", ToolCallId: command.Id, Name: command.Name);
        if (outcome != "approved")
        {
            Assert.IsType<ModelTextDelta>(OpenSandboxScript.Generate(Request())![0]);
            Assert.DoesNotContain(OpenSandboxScript.Generate(Request())!, item => item is ModelToolCallEvent);
            return;
        }
        for (var index = 0; index < 2; index++)
        {
            var publish = Assert.IsType<ModelToolCallEvent>(OpenSandboxScript.Generate(Request())![0]).Call;
            Assert.Equal(ToolCatalog.RequestAuthorization, publish.Name);
            using var publication = JsonDocument.Parse(publish.ArgumentsJson);
            Assert.Equal("publish_workspace_artifact", publication.RootElement.GetProperty("operation").GetString());
            interaction.Add(new(ModelRole.Assistant, "", ToolCalls: [publish]));
            interaction.Add(new(ModelRole.Tool, JsonSerializer.Serialize(new { outcome = "completed", operationId = Guid.NewGuid() }), ToolCallId: publish.Id, Name: publish.Name));
        }
        var completed = Assert.IsType<ModelSemanticResponseReady>(OpenSandboxScript.Generate(Request())![0]);
        Assert.Equal(2, completed.Response.Blocks!.Count);
        interaction[^1] = interaction[^1] with { Text = "{\"error\":\"forbidden\"}" };
        Assert.IsType<ModelTextDelta>(OpenSandboxScript.Generate(Request())![0]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Workbench_demo_advances_through_commands_and_artifacts_to_the_owned_completion(bool background, bool triggerEvidence)
    {
        var sections = new List<ContextSection> {
            ContextSection.Core(CoreProcedure.OperatingContract),
            ContextSection.Runtime(ContextSectionId.AgentIdentity, ContextSourceKind.Definition, "Workbench fixture"),
            ContextSection.Runtime(ContextSectionId.AgentInstructions, ContextSourceKind.Definition, "Perform the requested task.")
        };
        var interaction = new List<ModelMessage>();
        if (background)
        {
            sections.Add(ContextSection.Core(CoreProcedure.Background));
            sections.Add(ContextSection.Runtime(ContextSectionId.RunTask, ContextSourceKind.AgentRun, "synthetic-sandbox:demo"));
            if (triggerEvidence) sections.Add(ContextSection.Evidence(ContextSectionId.Trigger, ContextSourceKind.ExternalEvent,
                """{"kind":"ImmediateBackground","objective":"synthetic-sandbox:demo","parentSessionId":"owned-parent"}"""));
        }
        else interaction.Add(new(ModelRole.User, "synthetic-sandbox:demo"));
        var context = new EffectiveContextSnapshot(sections);
        var model = new ScriptedLanguageModel();
        var tools = new[] { ToolCatalog.SandboxExec, ToolCatalog.ArtifactsCreateFromWorkspace, ToolCatalog.WorkComplete }
            .Select(name => new ModelToolDefinition(name, name, """{"type":"object"}""")).ToArray();
        var artifactIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        for (var step = 0; step <= 7; step++)
        {
            var request = ContextAssembler.Create(Guid.NewGuid(), context, interaction) with { Tools = tools };
            if (background) Assert.DoesNotContain(request.Interaction!, message => message.Role == ModelRole.User);
            var events = new List<ModelGenerationEvent>();
            await foreach (var item in model.GenerateAsync(request)) events.Add(item);
            if (step == 7)
            {
                if (background)
                {
                    var complete = Assert.IsType<ModelToolCallEvent>(events[0]).Call;
                    Assert.Equal(ToolCatalog.WorkComplete, complete.Name);
                    using var arguments = JsonDocument.Parse(complete.ArgumentsJson);
                    Assert.Equal("Response", arguments.RootElement.GetProperty("outcome").GetString());
                    Assert.False(arguments.RootElement.GetProperty("attentionRequired").GetBoolean());
                }
                else
                {
                    var response = Assert.IsType<ModelSemanticResponseReady>(events[0]).Response;
                    Assert.Equal(artifactIds.Select(id => id.ToString()), response.Blocks!.Select(block => block.ArtifactId));
                }
                break;
            }
            var call = Assert.IsType<ModelToolCallEvent>(events[0]).Call;
            Assert.Equal(step < 5 ? ToolCatalog.SandboxExec : ToolCatalog.ArtifactsCreateFromWorkspace, call.Name);
            Assert.Equal(ModelStopReason.ToolCalls, Assert.IsType<ModelCompleted>(events[^1]).Reason);
            interaction.Add(new(ModelRole.Assistant, "", ToolCalls: [call]));
            var result = step < 5
                ? JsonSerializer.Serialize(new { status = "Completed", exitCode = step == 1 ? 1 : 0 })
                : JsonSerializer.Serialize(new { artifactId = artifactIds[step - 5] });
            interaction.Add(new(ModelRole.Tool, result, ToolCallId: call.Id, Name: call.Name));
        }
    }

    [Fact]
    public void Trigger_evidence_does_not_select_a_Workbench_scenario_for_an_unrelated_Runtime_task()
    {
        var context = new EffectiveContextSnapshot([
            ContextSection.Core(CoreProcedure.OperatingContract),
            ContextSection.Core(CoreProcedure.Background),
            ContextSection.Runtime(ContextSectionId.AgentIdentity, ContextSourceKind.Definition, "Workbench fixture"),
            ContextSection.Runtime(ContextSectionId.AgentInstructions, ContextSourceKind.Definition, "Perform the requested task."),
            ContextSection.Runtime(ContextSectionId.RunTask, ContextSourceKind.AgentRun, "Review owned documents"),
            ContextSection.Evidence(ContextSectionId.Trigger, ContextSourceKind.ExternalEvent,
                """{"untrustedText":"synthetic-sandbox:demo"}""")
        ]);
        var request = ContextAssembler.Create(Guid.NewGuid(), context) with {
            Tools = [new(ToolCatalog.SandboxExec, "Execute", """{"type":"object"}""")]
        };
        Assert.Null(OpenSandboxScript.Generate(request));
    }
}
