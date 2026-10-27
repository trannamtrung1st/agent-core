using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
namespace AgentCore.Infrastructure.Persistence.Migrations;
[DbContext(typeof(AgentCoreDbContext))]
partial class AgentCoreDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder) => TaskDelegation.Build(modelBuilder);
}
