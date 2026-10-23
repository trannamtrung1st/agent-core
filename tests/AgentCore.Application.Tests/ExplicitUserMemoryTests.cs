using AgentCore.Application.Context;
using System.Runtime.CompilerServices;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ExplicitUserMemoryTests
{

    [Theory]
    [InlineData("Please remember that my project codename is Atlas.", "project codename", "Atlas")]
    [InlineData("remember P4A_LONG_FACT for later.", "P4A_LONG_FACT", "P4A_LONG_FACT")]
    [InlineData("Please remember P4A_LONG_FACT for later.", "P4A_LONG_FACT", "P4A_LONG_FACT")]
    public void TryParse_recognizes_explicit_remember_phrases(string text, string subject, string content)
    {
        Assert.True(ExplicitUserMemoryRequests.TryParse(text, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(MemoryKind.Fact, parsed!.Kind);
        Assert.Equal(subject, parsed.Subject, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(content, parsed.Content, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("What is the code word?")]
    [InlineData("Remember to call John tomorrow.")]
    public void TryParse_ignores_non_explicit_requests(string text)
    {
        Assert.False(ExplicitUserMemoryRequests.TryParse(text, out _));
    }

    [Fact]
    public void Long_generic_facts_with_shared_prefix_get_distinct_subjects()
    {
        var prefix = new string('x', MemoryLimits.MaxSubjectCharacters);
        var left = prefix + "LEFT_TAIL";
        var right = prefix + "RIGHT_TAIL";
        var leftSubject = ExplicitUserMemoryRequests.SubjectForGenericFact(left);
        var rightSubject = ExplicitUserMemoryRequests.SubjectForGenericFact(right);
        Assert.NotEqual(leftSubject, rightSubject);
        Assert.Contains('#', leftSubject);
    }

    [Fact]
    public async Task General_assistant_v9_enables_memory_without_mutating_v8()
    {
        var store = new ScenarioDefinitionStore(
            FindAgents(),
            SyntheticProviderAliases.Default);
        var v8 = await store.GetAsync("general-assistant", 8);
        var v9 = await store.GetAsync("general-assistant", 9);
        Assert.NotNull(v8);
        Assert.NotNull(v9);
        Assert.False(v8!.MemoryPolicy?.SessionMemory ?? false);
        Assert.True(v9!.MemoryPolicy?.SessionMemory);
        Assert.True(v9.MemoryPolicy?.IdentityUserPromotion);
        Assert.True(v9.MemoryPolicy?.IdentityUserRetrieval);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
