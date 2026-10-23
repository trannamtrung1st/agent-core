using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Memory;

namespace AgentCore.Application.Tests;

public sealed class MemoryContentRulesTests
{
    private static MemoryAdmissionContext Context => new(" agent_inferred ", ["UNHEARD_PRIVATE_TAIL"], new HashSet<string> { "Trusted   Project" });

    [Theory] [InlineData(MemoryKind.Fact)] [InlineData(MemoryKind.Preference)] [InlineData(MemoryKind.Goal)] [InlineData(MemoryKind.Decision)] [InlineData(MemoryKind.OpenLoop)]
    public void Shared_content_rules_normalize_without_an_owner_or_store(MemoryKind kind)
    {
        var value = MemoryContentRules.ValidateContent(kind, "  Project\t preference ", "  Keep the dashboard qualifier.  ", [], Context);
        Assert.Equal("Project preference", value.Subject); Assert.Equal("project preference", value.SubjectKey);
        Assert.Equal("Keep the dashboard qualifier.", value.Content); Assert.Equal("agent_inferred", value.Source);
    }

    [Theory] [InlineData("language", "English")] [InlineData("PreferredName", "Pat")] [InlineData("locale", "en")]
    [InlineData("timezone", "UTC")] [InlineData("name", "Pat")] [InlineData("role", "Admin")]
    [InlineData("description", "Replacement")] [InlineData("tone", "Replacement")]
    [InlineData("token", "sk-abcdefghijklmnopqrstuvwxyz")]
    [InlineData("credential", "BEGIN RSA PRIVATE KEY")]
    [InlineData("credential", "AKIA0123456789ABCDEF")]
    [InlineData("Tail", "UNHEARD_PRIVATE_TAIL")]
    [InlineData("UNHEARD_PRIVATE_TAIL", "A body")]
    [InlineData(" trusted   project ", "Replacement")]
    public void Shared_content_rules_reject_sensitive_forbidden_and_trusted_fields(string subject, string content)
    {
        var error = Assert.Throws<AgentCoreException>(() => MemoryContentRules.ValidateContent(MemoryKind.Fact, subject, content, [], Context));
        Assert.Equal("MemoryRejected", error.Code);
        Assert.True(error.Message is "Memory content was rejected." or "Memory cannot replace trusted profile or identity state.");
    }

    [Theory] [InlineData("kind")] [InlineData("subject-empty")] [InlineData("subject-long")]
    [InlineData("content-empty")] [InlineData("content-long")] [InlineData("source-empty")] [InlineData("source-long")] [InlineData("entries")]
    public void Shared_content_rules_keep_all_admission_bounds(string boundary)
    {
        var context = Context with { Source = boundary == "source-empty" ? " " : boundary == "source-long" ? new('s', MemoryLimits.MaxSourceLabelCharacters + 1) : "fixture" };
        var subject = boundary == "subject-empty" ? " " : boundary == "subject-long" ? new('s', MemoryLimits.MaxSubjectCharacters + 1) : "Project";
        var body = boundary == "content-empty" ? " " : boundary == "content-long" ? new('c', MemoryLimits.MaxContentCharacters + 1) : "Fact";
        Assert.Throws<AgentCoreException>(() => MemoryContentRules.ValidateContent(boundary == "kind" ? (MemoryKind)99 : MemoryKind.Fact,
            subject, body, boundary == "entries" ? Enumerable.Range(0, MemoryLimits.MaxSourceEntryIds + 1).Select(_ => Guid.NewGuid()).ToArray() : [], context));
    }

    [Fact]
    public void Shared_sensitive_detector_keeps_encoded_blob_boundary()
    {
        Assert.False(MemoryContentRules.ContainsSensitive(new('A', 79)));
        Assert.True(MemoryContentRules.ContainsSensitive(new('A', 80)));
    }
}
