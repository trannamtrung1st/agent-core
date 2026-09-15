using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AgentCore.Infrastructure.Persistence;

public sealed class AgentCoreDbContextFactory : IDesignTimeDbContextFactory<AgentCoreDbContext>
{
    public AgentCoreDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AgentCoreDbContext>()
            .UseSqlite("Data Source=agent-core.design.db")
            .Options;
        return new AgentCoreDbContext(options);
    }
}
