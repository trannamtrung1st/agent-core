using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Email;
namespace AgentCore.Application.Tests;

public sealed class ToolActionPreparationTests
{
    [Fact]
    public async Task Detached_email_send_approval_matches_live_preparation_hash()
    {
        var provider = new SyntheticEmailProvider();
        var executor = new SessionToolExecutor(
            RoleKnowledgeService.FromApprovedCatalog(new EmptyKnowledgeCatalog(), TimeProvider.System),
            emailProvider: provider);
        var draft = await provider.CreateDraftAsync(
            new EmailCreateDraftRequest(
                ["user@example.com"],
                [],
                [],
                "Subject",
                "Body"),
            CancellationToken.None);
        using var args = JsonDocument.Parse($$"""{"draftId":"{{draft.Draft.DraftId}}"}""");
        var call = new ModelToolCall("send", ToolCatalog.EmailSend, args.RootElement.GetRawText());
        var live = await executor.PrepareEmailSendApprovalAsync(args.RootElement);
        var detached = await ToolActionPreparation.PrepareApprovalAsync(executor, call, args.RootElement);
        Assert.NotNull(live.Preparation);
        Assert.NotNull(detached.Preparation);
        Assert.Equal(live.Preparation.ActionHash, detached.Preparation!.ActionHash);
        Assert.Contains("user@example.com", detached.Preparation.Preview, StringComparison.Ordinal);
    }

    private sealed class EmptyKnowledgeCatalog : IApprovedKnowledgeCatalog
    {
        public ValueTask<string?> ReadContentAsync(string identity, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<string?>(null);
    }
}
