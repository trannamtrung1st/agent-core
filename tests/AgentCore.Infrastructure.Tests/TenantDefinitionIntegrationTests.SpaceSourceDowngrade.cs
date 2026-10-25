using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace AgentCore.Infrastructure.Tests;
public sealed partial class TenantDefinitionIntegrationTests
{
    [Fact]
    public async Task Retained_Space_source_metadata_refuses_lossy_downgrade_and_reup_preserves_its_actual_owner()
    {
        await using var f=await Fixture.Create(true);
        var account=await f.Identity.BootstrapAsync("source-retention@example.test","Owner","strong-source-owner-password","Retained source","retained-source");
        var tenant=account.TenantId;var actor=account.OwnerPrincipalId;
        async Task<long> Revision()=>(await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var builtIns=new SessionNoBuiltinFallback();
        var resolver=new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam,f.Definitions,builtIns,new(f.Resources,builtIns,new NoBuiltinKnowledge(),f.Content),(ITenantCredentialBindingStore)f.Iam);
        var spaces=new TenantSpaceService(f.Iam,(ITenantSpaceStore)f.Iam,(ITenantAgentStore)f.Iam,f.Authorizer,resolver,(ITenantCredentialBindingStore)f.Iam,(ITenantCredentialStore)f.Iam,f.Ids,TimeProvider.System);
        var space=await spaces.CreateAsync(actor,tenant,f.Ids.NewId(),"Retained immutable source","","",await Revision());
        var at=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var resource=new SpaceFileResource(tenant,space.SpaceId,f.Ids.NewId(),"retained.txt",AgentDefinitionResourceKind.Reference,"text/plain",new string('a',64),1,"/home/retained.txt",1,at,at);
        var before=(await f.Iam.ReadTenantAsync(tenant))!;
        Assert.True(await ((ITenantSpaceResourceStore)f.Iam).TrySaveSpaceResourceAsync(before with{Tenant=before.Tenant with{Revision=before.Tenant.Revision+1}},before.Tenant.Revision,space.Revision,resource,0,
            new(f.Ids.NewId(),tenant,actor,"resources.manage",resource.Key,"allowed","space_resource_updated",at)));
        await using var db=f.Contexts!.CreateDbContext();var migrator=db.GetService<IMigrator>();
        foreach (var update in new[] {
            $"UPDATE SpaceFileResources SET ResourceId='{Guid.NewGuid():D}'",
            "UPDATE SpaceFileResources SET Revision=Revision+1,LogicalPath='../escape.txt'",
            "UPDATE SpaceFileResources SET Revision=Revision+1,SourcePath='/home/../escape.txt'",
            "UPDATE SpaceFileResources SET Revision=Revision+1,ContentSha256='invalid'",
            "DELETE FROM SpaceFileResources" })
        {
            await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>db.Database.ExecuteSqlRawAsync(update));
            Assert.Equal(resource,await ((ITenantSpaceResourceStore)f.Iam).FindSpaceResourceAsync(tenant,space.SpaceId,resource.ResourceId));
        }
        var error=await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(()=>migrator.MigrateAsync("20261025060000_ContextSuccessor"));
        Assert.Contains("CHECK constraint failed",error.Message,StringComparison.Ordinal);
        Assert.Equal(resource,await ((ITenantSpaceResourceStore)f.Iam).FindSpaceResourceAsync(tenant,space.SpaceId,resource.ResourceId));
        await migrator.MigrateAsync();Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(resource,await ((ITenantSpaceResourceStore)f.Iam).FindSpaceResourceAsync(tenant,space.SpaceId,resource.ResourceId));
        await db.Database.OpenConnectionAsync();await using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText="PRAGMA foreign_key_check";
        await using var violations=await command.ExecuteReaderAsync();Assert.False(await violations.ReadAsync());
    }
}
