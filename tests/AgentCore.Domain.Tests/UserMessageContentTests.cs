using AgentCore.Domain.Conversation;
namespace AgentCore.Domain.Tests;
public sealed class UserMessageContentTests
{
    [Fact]
    public void Normalization_coalesces_text_and_deduplicates_keys_without_using_display_names()
    {
        UserMessagePart[] parts = [new("text",Text:"One "),new("text",Text:"two "),new("invocation",InvocationKind:"skill",SkillKey:"definition:review",Label:"Review"),new("invocation",InvocationKind:"skill",SkillKey:"definition:review"),new("invocation",InvocationKind:"skill",SkillKey:"instance:review",Label:"Review")];
        var normalized = UserMessageContent.Normalize(parts)!;
        Assert.Equal(3,normalized.Count); Assert.Equal("One two ",normalized[0].Text);
        Assert.Equal(["definition:review","instance:review"],UserMessageContent.ExplicitSkills(normalized));
        parts[0] = new("text",Text:"mutated"); Assert.Equal("One two ",normalized[0].Text);
        Assert.Throws<NotSupportedException>(()=>((IList<UserMessagePart>)normalized)[0]=parts[0]);
    }
    [Fact]
    public void Foreign_fields_and_client_labels_fail_closed_and_reference_only_has_a_task()
    {
        Assert.Throws<ArgumentException>(()=>UserMessageContent.Normalize([new("reference",Reference:new("artifact",ArtifactId:Guid.NewGuid()))]));
        Assert.Throws<ArgumentException>(()=>UserMessageContent.Normalize([new("reference",Reference:new("session",SessionId:Guid.NewGuid(),ItemId:Guid.NewGuid()))]));
        Assert.Throws<ArgumentException>(()=>UserMessageContent.Normalize([new("invocation",InvocationKind:"skill",SkillKey:"definition:review",Label:"forged")],input:true));
        Assert.False(UserMessageContent.HasTask([new("invocation",InvocationKind:"skill",SkillKey:"definition:review")]));
        Assert.True(UserMessageContent.HasTask([new("reference",Reference:new("session",SessionId:Guid.NewGuid()))]));
    }
    [Fact]
    public void Skill_references_cannot_select_a_revision()
    {
        var reference = new UserResourceReference("skill", AgentInstanceId: Guid.NewGuid(), SkillKey: "instance:review", SelectedRevision: 1);
        Assert.Throws<ArgumentException>(() => UserMessageContent.Normalize([new("reference", Reference: reference)]));
        Assert.NotNull(UserMessageContent.Normalize([new("reference", Reference: reference with { SelectedRevision = null })]));
    }
    [Fact]
    public void Utf16_and_part_count_bounds_are_identical_for_plain_and_structured_unicode()
    {
        Assert.NotNull(UserMessageContent.Normalize([new("text",Text:new string('語',8000))]));
        Assert.Throws<ArgumentException>(()=>UserMessageContent.Normalize([new("text",Text:new string('a',8001))]));
        Assert.Throws<ArgumentException>(()=>UserMessageContent.Normalize(Enumerable.Range(0,129).Select(_=>new UserMessagePart("text",Text:"x")).ToArray()));
    }
}
