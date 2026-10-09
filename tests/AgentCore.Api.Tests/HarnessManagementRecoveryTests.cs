using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using AgentCore.Infrastructure.Definitions;

namespace AgentCore.Api.Tests;

public sealed class HarnessManagementRecoveryTests
{
    [Fact]
    public async Task Chat_adoption_failure_preserves_active_version_and_fresh_change_survives_sqlite_reopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "p97-chat-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Guid id; int published;
        try
        {
            await using (var factory = new HarnessSqliteFactory(root))
            {
                var services = factory.Services;
                var service = services.GetRequiredService<HarnessManagementService>();
                var instance = await services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
                id = instance.InstanceId;
                instance = await service.ConfigureAsync(id, instance.Revision, new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], ["knowledge:support-order-policy"], []));
                var contexts = services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
                await using (var db = await contexts.CreateDbContextAsync())
                    await db.Database.ExecuteSqlRawAsync("""
                        CREATE TRIGGER P97ChatFail BEFORE UPDATE OF ActiveVersion ON AgentInstances
                        WHEN NEW.ActiveVersion != OLD.ActiveVersion
                        BEGIN SELECT RAISE(ABORT, 'private injected fault'); END;
                        """);
                var pinned = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", 21))!;
                var args = System.Text.Json.JsonSerializer.Serialize(new { expectedVersion = 21, policyRevision = instance.HarnessManagement!.PolicyRevision,
                    id = "chat-recovered", content = "Durable order policy", source = "conversation:user", expected = "Retain policy", observed = "Owner supplied role knowledge" });
                var call = new AgentCore.Application.Ports.ModelToolCall("chat", "harness.knowledge.upsert", args);
                var executor = services.GetRequiredService<AgentCore.Application.Tools.SessionToolExecutor>();
                var admission = new AgentCore.Application.Tools.ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: id, OwnerTurnText: "Durable order policy");
                var failed = await executor.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: admission);
                Assert.DoesNotContain("private injected fault", failed.Text);
                Assert.DoesNotContain("\"saved\":true", failed.Text);
                var review = await service.ReviewAsync(id);
                Assert.Equal(21, review.ActiveVersion);
                Assert.Equal(HarnessPreparationStatus.Failed, review.State.Preparation!.Status);
                await using (var db = await contexts.CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER P97ChatFail;");
                var recovered = await executor.ExecuteAsync(pinned, Guid.NewGuid(), call, 100000, admission: admission);
                Assert.Contains("\"saved\":true", recovered.Text);
                published = (await service.ReviewAsync(id)).ActiveVersion;
            }
            await using var reopened = new HarnessSqliteFactory(root);
            var durable = await reopened.Services.GetRequiredService<HarnessManagementService>().ReviewAsync(id);
            Assert.Equal(published, durable.ActiveVersion);
            Assert.Equal(HarnessPreparationStatus.Published, durable.State.Preparation!.Status);
            var definition = (await reopened.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync("general-assistant", published))!;
            Assert.Contains(definition.Environment!.KnowledgeList, k => k.Identity == "chat-recovered");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Failed_adoption_transaction_preserves_active_version_and_preparation_then_fresh_verification_recovers()
    {
        var root = Path.Combine(Path.GetTempPath(), "p97-adoption-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var factory = new HarnessSqliteFactory(root);
            var instance = await factory.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
            var service = factory.Services.GetRequiredService<HarnessManagementService>();
            instance = await service.ConfigureAsync(instance.InstanceId, instance.Revision,
                new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources], ["knowledge:support-order-policy"], []));
            instance = await service.StartAsync(instance.InstanceId, instance.Revision, "Prepare a safe review procedure.");
            var prepId = instance.HarnessManagement!.Preparation!.PreparationId;
            var review = await HarnessLegacyFixture.RunAsync(factory.Services, instance.InstanceId, prepId);
            Assert.Equal(HarnessPreparationStatus.Ready, review.State.Preparation!.Status);
            var contexts = factory.Services.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
            await using (var db = await contexts.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE TRIGGER P97FailAdoption BEFORE UPDATE OF ActiveVersion ON AgentInstances
                    WHEN NEW.ActiveVersion != OLD.ActiveVersion
                    BEGIN SELECT RAISE(ABORT, 'injected adoption failure'); END;
                    """);
            await Assert.ThrowsAsync<AgentCoreException>(async () => await service.PromoteAsync(instance.InstanceId, review.InstanceRevision, review.Draft!.Revision));
            var unchanged = await service.ReviewAsync(instance.InstanceId);
            Assert.Equal(21, unchanged.ActiveVersion);
            Assert.Equal(review.InstanceRevision, unchanged.InstanceRevision);
            Assert.Equal(HarnessPreparationStatus.Ready, unchanged.State.Preparation!.Status);
            Assert.Null(unchanged.State.Preparation.PublishedVersion);
            await using (var db = await contexts.CreateDbContextAsync())
                await db.Database.ExecuteSqlRawAsync("DROP TRIGGER P97FailAdoption;");
            review = await HarnessLegacyFixture.RunAsync(factory.Services, instance.InstanceId, prepId);
            instance = await service.PromoteAsync(instance.InstanceId, review.InstanceRevision, review.Draft!.Revision);
            Assert.True(instance.ActiveVersion > 17);
            Assert.Equal(HarnessPreparationStatus.Published, instance.HarnessManagement!.Preparation!.Status);
            Assert.Equal(instance.ActiveVersion, instance.HarnessManagement.Preparation.PublishedVersion);
            Assert.Equal(review.InstanceRevision + 1, instance.Revision);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Sqlite_reopen_preserves_exact_pending_approval_candidate_and_audit_then_freeze_requires_another_fork()
    {
        var root = Path.Combine(Path.GetTempPath(), "p97-reopen-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Guid id;
        Guid preparationId;
        Guid draftId;
        Guid approvalId;
        string actionHash;
        long revision;
        try
        {
            await using (var first = new HarnessSqliteFactory(root))
            {
                Assert.IsType<SqliteAgentInstanceStore>(first.Services.GetRequiredService<IAgentInstanceStore>());
                var instance = await first.Services.GetRequiredService<AdminAgentInstanceService>().CreateManagedAsync("general-assistant", 21);
                id = instance.InstanceId;
                var service = first.Services.GetRequiredService<HarnessManagementService>();
                instance = await service.ConfigureAsync(id, instance.Revision,
                    new(HarnessManagementMode.Managed, [HarnessManagementScope.KnowledgeResources, HarnessManagementScope.ToolSelection],
                        ["knowledge:support-order-policy"], ["web.fetch"]));
                instance = await service.StartAsync(id, instance.Revision, "Prepare operations review.");
                preparationId = instance.HarnessManagement!.Preparation!.PreparationId;
                var review = await HarnessLegacyFixture.RunAsync(first.Services, id, preparationId);
                draftId = review.Draft!.DraftId;
                var approval = Assert.Single(review.State.Preparation!.Approvals);
                approvalId = approval.ApprovalId;
                actionHash = approval.ActionHash;
                revision = review.InstanceRevision;
            }
            await using (var reopened = new HarnessSqliteFactory(root))
            {
                var service = reopened.Services.GetRequiredService<HarnessManagementService>();
                var review = await service.ReviewAsync(id);
                Assert.Equal(draftId, review.Draft!.DraftId);
                Assert.Equal("Pending", Assert.Single(review.State.Preparation!.Approvals).Status);
                Assert.Contains(review.Draft.Candidate.Environment!.KnowledgeList, s => s.Identity == "preparation-reference");
                var instance = await service.DecideApprovalAsync(id, revision, approvalId, actionHash, true);
                review = await HarnessLegacyFixture.RunAsync(reopened.Services, id, preparationId);
                Assert.Equal(HarnessPreparationStatus.Ready, review.State.Preparation!.Status);
                instance = await service.PromoteAsync(id, review.InstanceRevision, review.Draft!.Revision);
                var publishedVersion = instance.ActiveVersion;
                var published = await reopened.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, publishedVersion);
                instance = await service.ConfigureAsync(id, instance.Revision, instance.HarnessManagement!.Policy with { Mode = HarnessManagementMode.Disabled, Frozen = true });
                Assert.Equal(HarnessPreparationStatus.Published, instance.HarnessManagement!.Preparation!.Status);
                await Assert.ThrowsAsync<AgentCoreException>(async () => await service.RequestOperationAsync(id, preparationId,
                    new("knowledge.remove", review.Draft.Revision, Id: "operations.review")));
                instance = await service.ConfigureAsync(id, instance.Revision, instance.HarnessManagement.Policy with { Mode = HarnessManagementMode.Managed, Frozen = false });
                instance = await service.StartAsync(id, instance.Revision, "Improve the next candidate.");
                Assert.NotEqual(draftId, instance.HarnessManagement!.Preparation!.DraftId);
                Assert.Equal(publishedVersion, instance.ActiveVersion);
                var same = await reopened.Services.GetRequiredService<IAgentDefinitionStore>().GetAsync(instance.DefinitionId, publishedVersion);
                Assert.Equal(published!.SystemInstructions, same!.SystemInstructions);
                Assert.Equal(published.SkillList.Select(s => s.Id), same.SkillList.Select(s => s.Id));
                var history = await reopened.Services.GetRequiredService<IAdminEventStore>().ListAsync(new(TargetType: "agent.instance", TargetId: id.ToString("D")));
                Assert.Contains(history, e => e.ActorKind == AdminEventActorKind.Agent && e.SummaryJson.Contains("CandidateCommitted"));
                Assert.Contains(history, e => e.SummaryJson.Contains("Approved"));
                Assert.Contains(history, e => e.SummaryJson.Contains("Frozen"));
                Assert.DoesNotContain(history, e => e.SummaryJson.Contains("customer messages"));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

internal sealed class HarnessSqliteFactory(string root) : AgentCoreApiFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.AddDbContextFactory<AgentCoreDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "synthetic.db")}"));
            services.RemoveAll<IAgentInstanceStore>();
            services.AddSingleton<IAgentInstanceStore>(provider =>
            {
                var contexts = provider.GetRequiredService<IDbContextFactory<AgentCoreDbContext>>();
                using var db = contexts.CreateDbContext();
                db.Database.Migrate();
                return new SqliteAgentInstanceStore(contexts, provider.GetRequiredService<IIdGenerator>());
            });
            services.RemoveAll<IDefinitionResourceContentStore>();
            services.AddSingleton<IDefinitionResourceContentStore>(new FileDefinitionResourceContentStore(Path.Combine(root, "resources")));
            services.RemoveAll<IAgentDefinitionAdminStore>();
            services.AddSingleton<IAgentDefinitionAdminStore, SqliteAgentDefinitionAdminStore>();
            services.RemoveAll<IAgentDefinitionResourceAdminStore>();
            services.AddSingleton<IAgentDefinitionResourceAdminStore, SqliteAgentDefinitionResourceAdminStore>();
            services.RemoveAll<IDefinitionDraftEvaluationStore>();
            services.AddSingleton<IDefinitionDraftEvaluationStore, SqliteDefinitionDraftEvaluationStore>();
            services.RemoveAll<IAdminEventStore>();
            services.AddSingleton<IAdminEventStore, SqliteAdminEventStore>();
        });
    }

    protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?>
    {
        ["Persistence:Provider"] = "Sqlite", ["Persistence:ConnectionString"] = $"Data Source={Path.Combine(root, "synthetic.db")}",
        ["Persistence:DefinitionResourceRoot"] = Path.Combine(root, "resources"),
        ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces"), ["Persistence:AttachmentRoot"] = Path.Combine(root, "attachments"),
        ["Persistence:ArtifactRoot"] = Path.Combine(root, "artifacts")
    };
}
