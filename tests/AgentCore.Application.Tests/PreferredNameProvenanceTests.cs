using AgentCore.Application.Context;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class PreferredNameProvenanceTests
{
    [Fact]
    public void Memory_prompt_omits_invented_friend_and_does_not_invite_a_name()
    {
        var builder = new PromptContextBuilder();
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var seeded = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                ["preferredName"] = LocalUserProfile.ApplicationProfileValue("friend", now)
            },
            now);
        var sections = builder.BuildContext(Context(seeded));
        Assert.DoesNotContain("friend", sections.Text(ContextSectionId.Profile), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("preferredName=", sections.Text(ContextSectionId.Profile), StringComparison.Ordinal);
        Assert.Contains("language=en", sections.Text(ContextSectionId.Profile), StringComparison.Ordinal);
        Assert.Contains("No preferred user name or form of address is known.", sections.Text(ContextSectionId.Profile), StringComparison.Ordinal);
        Assert.DoesNotContain("friend", sections.Text(ContextSectionId.AgentIdentity), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Memory_prompt_keeps_a_supplied_preferred_name()
    {
        var builder = new PromptContextBuilder();
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var profile = new UserProfile(
            LocalUserProfile.Id,
            1,
            new Dictionary<string, UserProfileValue>(StringComparer.Ordinal)
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", now),
                ["preferredName"] = new UserProfileValue("Pat", UserProfileValueSource.UserSet, now)
            },
            now);
        var sections = builder.BuildContext(Context(profile));
        Assert.Contains("preferredName=Pat", sections.Text(ContextSectionId.Profile), StringComparison.Ordinal);
        Assert.DoesNotContain("Do not invent one.", sections.Text(ContextSectionId.Profile), StringComparison.Ordinal);
    }

    private static AgentContext Context(UserProfile profile)
    {
        var now = new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
        var user = new ConversationEntry(
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            1,
            Guid.Parse("019944af-0000-7000-8000-000000000010"),
            ConversationRole.User,
            "Hello",
            null,
            EntryStatus.Completed,
            SessionMode.Text,
            5,
            5,
            now);
        return new AgentContext(
            SampleDefinitions.Examiner,
            [user],
            string.Empty,
            profile,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(user.EntryId, TriggerKind.UserTurn, "Hello"));
    }

}
