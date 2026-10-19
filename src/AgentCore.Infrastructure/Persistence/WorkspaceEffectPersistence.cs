using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Persistence;

internal static class WorkspaceEffectPersistence
{
    internal static void Configure(ModelBuilder model) => model.Entity<AgentRunRecord>().ToTable("AgentRuns", t => {
        t.HasTrigger("TR_AgentRuns_DirectWorkspace_Insert");
        t.HasTrigger("TR_AgentRuns_DirectWorkspace_Update");
    });
}
