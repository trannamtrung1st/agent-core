using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SessionCatalogTests
{
    [Fact]
    public void Empty_user_text_keeps_default_title()
    {
        Assert.Equal(SessionTitles.Default, SessionTitles.FromUserText("   \n  "));
    }

    [Fact]
    public void First_message_title_collapses_whitespace()
    {
        Assert.Equal("Hello there", SessionTitles.FromUserText("Hello\n there  "));
    }

}
