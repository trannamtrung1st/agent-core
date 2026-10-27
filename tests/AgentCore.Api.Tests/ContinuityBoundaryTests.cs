using AgentCore.Application.Execution;
using AgentCore.Application.Context;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Application.Work;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Experience;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ContinuityBoundaryTests
{

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deleted_experience_wins_over_completion_with_memory_and_sqlite_parity(bool sqlite)
    {
        IExperienceStore store;
        await using var host = new ExperienceHost(Path.Combine(Path.GetTempPath(), $"experience-race-{Guid.NewGuid():N}.db"));
        _ = host.CreateClient();
        store = sqlite ? host.Services.GetRequiredService<IExperienceStore>() : new InMemoryExperienceStore();
        var id = Guid.NewGuid(); var recordId = Guid.NewGuid();
        var record = new AgentExperience(recordId, id, LocalUserProfile.Id, ExperienceSourceKind.Session, Guid.NewGuid(), 2,
            DateTimeOffset.UtcNow, "general-assistant", 35, recordId, new("synthetic-default", "synthetic", "synthetic", null), DateTimeOffset.UtcNow);
        await store.AdmitAsync(record); await store.ResetAsync(id);
        await store.CompleteAsync(id, recordId, new("Late output", [], [], [], [], [], [], []));
        var retained = (await store.GetAsync(id, recordId))!;
        Assert.Equal(ExperienceVisibility.Deleted, retained.Visibility); Assert.Null(retained.Content);
        Assert.Equal(ExperienceVisibility.Deleted, (await store.AdmitAsync(record)).Visibility);
        await Assert.ThrowsAsync<AgentCoreException>(() => store.SetVisibilityAsync(id, recordId, retained.Revision, ExperienceVisibility.Eligible).AsTask());
    }
}
