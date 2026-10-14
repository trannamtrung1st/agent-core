using AgentCore.Domain.Conversation;
namespace AgentCore.Domain.Tests;
public sealed class DriveBatchApprovalBoundsTests
{
    private static AgentRunApproval Approval(string tool, string args)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, tool, args, new string('a', 64), "Review exact files", now.AddMinutes(1), AgentRunApprovalDecision.Pending, null, false, 1, now);
    }
    [Fact]
    public void Closed_batch_contract_preserves_bounded_formatted_arguments_without_expanding_old_tools()
    {
        var args = "{" + new string(' ', 9000) + "\"accountRef\":\"drive\",\"authEpoch\":1,\"files\":[{\"fileId\":\"one\",\"expectedVersion\":\"1\"}]}";
        Assert.Equal(args, Approval("drive.trash_batch", args).PreparedActionJson);
        Assert.Throws<ArgumentException>(() => Approval("drive.trash", args));
        Assert.Throws<ArgumentException>(() => Approval("drive.trash_batch", args + new string(' ', 8000)));
    }
}
