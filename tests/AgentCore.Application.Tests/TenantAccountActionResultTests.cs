using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Tests;

public sealed class TenantAccountActionResultTests
{
    private static TenantApprovalRequest Request(string tool = ToolCatalog.EmailCreateDraft)
    {
        var session = Guid.NewGuid();
        var arguments = TenantAccountActionContract.Parse(session, tool, tool == ToolCatalog.DriveTrashBatch
            ? """{"accountRef":"work","authEpoch":1,"files":[{"fileId":"first","expectedVersion":"1"},{"fileId":"second","expectedVersion":"1"}]}"""
            : """{"accountRef":"work","to":["friend@example.test"],"subject":"Exact","body":"Literal"}""");
        var remote = JsonSerializer.Serialize(new[] { new PreparedAccountRemoteFile("first", "First", "application/octet-stream", 2, "1", null, ["root"], false), new PreparedAccountRemoteFile("second", "Second", "application/octet-stream", 2, "1", null, ["root"], false) });
        var action = new PreparedAccountAction(session, tool, arguments.Json, new(Guid.NewGuid(), Guid.NewGuid(), "work", 1, 1, 1, 1),
            tool == ToolCatalog.DriveTrashBatch ? CredentialPurpose.GoogleDriveAccount : CredentialPurpose.EmailAccount, arguments.Permission,
            tool == ToolCatalog.DriveTrashBatch ? remote : "[]", tool == ToolCatalog.DriveTrashBatch ? null : new("exact-draft", """{"to":["friend@example.test"],"cc":[],"bcc":[]}""", "Exact", "Literal", "sender@example.test", 1));
        var operation = TenantApprovalRules.PrepareAccountAction(Guid.NewGuid(), Guid.NewGuid(), action, 1, 1);
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), session, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), ApprovalKind.Review, operation,
            TenantApprovalStatus.Approved, 2, now, now.AddHours(1));
    }
    private static TenantOperationReceipt Receipt(TenantApprovalRequest r) => new(r.Operation.OperationId, r.RequestId, r.TenantId, Guid.NewGuid(), r.SessionId,
        r.RootActivationId, r.ExecutorPrincipalId, r.Operation.InstanceId, r.Operation.ActionHash, TenantOperationOutcome.Completed, TenantAccountEffectRules.Completed, 1, 1, DateTimeOffset.UtcNow);
    [Fact]
    public void Atomic_local_draft_fact_survives_checkpoint_compaction_but_unrecorded_provider_results_do_not()
    {
        var r = Request(); var receipt = Receipt(r);
        Assert.Equal("exact-draft", TenantAccountActionResults.Read(r, receipt, null)!.DraftId);
        var batch = Request(ToolCatalog.DriveTrashBatch);
        Assert.Null(TenantAccountActionResults.Read(batch, Receipt(batch), null));
    }
    [Theory]
    [InlineData("tenant")] [InlineData("session")] [InlineData("instance")] [InlineData("executor")] [InlineData("root")] [InlineData("request")]
    [InlineData("operation")] [InlineData("hash")] [InlineData("reason")] [InlineData("outcome")] [InlineData("pending")]
    public void Local_draft_details_require_the_exact_factual_receipt(string field)
    {
        var r = Request(); var receipt = Receipt(r);
        receipt = field switch {
            "tenant" => receipt with { TenantId = Guid.NewGuid() }, "session" => receipt with { SessionId = Guid.NewGuid() },
            "instance" => receipt with { InstanceId = Guid.NewGuid() }, "executor" => receipt with { ExecutorPrincipalId = Guid.NewGuid() },
            "root" => receipt with { RootActivationId = Guid.NewGuid() }, "request" => receipt with { RequestId = Guid.NewGuid() },
            "operation" => receipt with { OperationId = Guid.NewGuid() }, "hash" => receipt with { ActionHash = new('f', 64) },
            "reason" => receipt with { ReasonCode = "operation_completed" }, "outcome" => receipt with { Outcome = TenantOperationOutcome.Indeterminate }, _ => receipt };
        if (field == "pending") r = r with { Status = TenantApprovalStatus.Pending };
        Assert.Null(TenantAccountActionResults.Read(r, receipt, null));
    }
    [Theory] [InlineData("null")] [InlineData("reordered")] [InlineData("continued-after-refusal")] [InlineData("completed-but-uncertain")]
    public void Recorded_batch_detail_refuses_corrupt_items_or_contradictory_execution(string mode)
    {
        var r = Request(ToolCatalog.DriveTrashBatch);
        IReadOnlyList<TenantAccountItemResult> items = mode switch {
            "null" => [null!, new("second", "completed")], "reordered" => [new("second", "completed"), new("first", "completed")],
            "continued-after-refusal" => [new("first", "denied"), new("second", "completed")], _ => [new("first", "completed"), new("second", "indeterminate")] };
        Assert.Throws<ArgumentException>(() => TenantAccountEffectRules.Result(r, TenantOperationOutcome.Completed, new("completed", Items: items)));
    }
}
