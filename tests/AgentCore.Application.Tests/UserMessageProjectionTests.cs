using AgentCore.Application.Composer;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Tests;

public sealed class UserMessageProjectionTests
{
    [Theory]
    [InlineData(false, "/say")]
    [InlineData(true, "/say · Switch back to project AI engineering")]
    public void Titles_retain_canonical_skill_name_without_changing_stored_text(bool withTask, string expected)
    {
        UserMessagePart[] parts = [new("invocation", InvocationKind: "skill", SkillKey: "definition:say", Label: "Unrelated display label")];
        if (withTask) parts = [.. parts, new("text", Text: " Switch back to project AI engineering")];
        var entry = Entry(parts);
        Assert.Equal(expected, SessionTitles.FromUserText(UserMessageProjection.ForTitle(entry)));
        Assert.Equal(UserMessageContent.DisplayText(parts), entry.Text);
    }

    [Fact]
    public void Titles_group_multiple_skills_and_render_reference_labels_with_existing_limits()
    {
        var entry = Entry([new("invocation", InvocationKind: "skill", SkillKey: "definition:research", Label: "Research"),
            new("text", Text: " "), new("invocation", InvocationKind: "skill", SkillKey: "instance:report-writing", Label: "Report writing"),
            new("text", Text: " Analyze browser alternatives")]);
        Assert.Equal("/research + /report-writing · Analyze browser alternatives", UserMessageProjection.ForTitle(entry));
        var reference = new UserResourceReference("homeFile", SpaceId: Guid.NewGuid(), Path: "/home/proposal.md");
        var review = Entry([new("text", Text: "Review "), new("reference", Reference: reference, Label: "/home/proposal.md")]);
        Assert.Equal("Review proposal.md", UserMessageProjection.ForTitle(review));
        var longTitle = Entry([new("invocation", InvocationKind: "skill", SkillKey: "definition:say"), new("text", Text: " " + new string('x', 500))]);
        Assert.Equal(SessionTitles.MaxLength, SessionTitles.FromUserText(UserMessageProjection.ForTitle(longTitle)).Length);
        var plain = Entry([new("text", Text: " /say is pasted text ")]) with { Parts = null };
        Assert.Equal("/say is pasted text", SessionTitles.FromUserText(UserMessageProjection.ForTitle(plain)));
    }

    [Fact]
    public void Semantic_views_preserve_order_use_resolved_names_and_separate_history_from_current_intent()
    {
        var id = Guid.NewGuid();
        var entry = Entry([new("text", Text: "Before "), new("invocation", InvocationKind: "skill", SkillKey: "instance:" + id, Label: "Say"),
            new("text", Text: " between "), new("reference", Reference: new("session", SessionId: Guid.NewGuid()), Label: "Project discussion"),
            new("text", Text: " after")]);
        var current = UserMessageProjection.ForModel(entry, true);
        Assert.StartsWith("Before [Explicitly invoked Skill: \"Say\"; key=instance:" + id, current);
        Assert.Contains("] between [Referenced session: \"Project discussion\"; locator=session:", current);
        Assert.EndsWith("] after", current);
        var history = UserMessageProjection.ForModel(entry, false);
        Assert.Contains("Previously invoked Skill (historical, not activated)", history);
        Assert.DoesNotContain("Explicitly invoked", history);
        var summary = UserMessageProjection.ForSummary(entry);
        Assert.Contains("Say", summary); Assert.Contains("Project discussion", summary);
        Assert.DoesNotContain(id.ToString(), summary);
        Assert.DoesNotContain("key=", summary); Assert.DoesNotContain("locator=", summary);
        Assert.StartsWith("/Say · Before", UserMessageProjection.ForTitle(entry));
    }

    private static ConversationEntry Entry(UserMessagePart[] parts) => new(Guid.NewGuid(), 1, Guid.NewGuid(),
        ConversationRole.User, UserMessageContent.DisplayText(parts), null, EntryStatus.Completed,
        SessionMode.Text, 0, 0, DateTimeOffset.UnixEpoch, Parts: UserMessageContent.Normalize(parts));
}
