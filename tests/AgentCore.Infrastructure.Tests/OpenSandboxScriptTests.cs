using System.Text.Json;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Infrastructure.Tests;

public sealed class OpenSandboxScriptTests
{
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
