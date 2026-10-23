using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Security;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shipped_template_fork_owns_approved_knowledge_and_publishes_without_global_resource_borrowing(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var catalog = await f.Service.ListTemplatesAsync(owner.OwnerPrincipalId, owner.TenantId);
        Assert.DoesNotContain(catalog, item => item.Id == "approval-demo");
        var retired = await Assert.ThrowsAsync<AccessDeniedException>(() => f.Service.ForkTemplateAsync(owner.OwnerPrincipalId,
            owner.TenantId, "approval-demo", 1, "Retired approval demo", 1).AsTask());
        Assert.Equal("definition_unavailable", retired.ReasonCode);
        var template = catalog.Single(d => d.Id == "customer-support" && d.Version == 3);
        var source = (await Fixture.ShippedTemplates().ReadAsync(template.Id, template.Version))!;
        var draft = await f.Service.ForkTemplateAsync(owner.OwnerPrincipalId, owner.TenantId, template.Id, template.Version, "Private support", 1);
        Assert.NotEqual(template.Id, draft.DefinitionId); Assert.True(Guid.TryParseExact(draft.DefinitionId, "D", out var id));
        Assert.Equal(DefinitionDraftSourceKind.ForkBuiltIn, draft.SourceKind); Assert.Equal(template.Id, draft.SourceDefinitionId); Assert.Equal(3, draft.SourceVersion);
        Assert.Equal("Private support", draft.Candidate.Identity.Name);
        var snapshot = await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId);
        var binding = Assert.Single(snapshot.Resources); var approved = Assert.Single(source.Resources);
        Assert.Equal(approved.LogicalPath, binding.LogicalPath); Assert.Equal(AgentDefinitionResourceKind.Knowledge, binding.Kind);
        Assert.Equal(approved.Content, (await f.Service.ReadDraftResourceAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, binding.ResourceId)).Content);
        Assert.Null(await f.Admin.GetDraftAsync(draft.DraftId));
        Assert.Empty(await f.Resources.ListDraftResourcesAsync(draft.DraftId));
        var updated = await f.Service.UpdateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId,
            draft.Candidate with { AgentInstructions = "Only this tenant's new instructions." }, 2, 1);
        Assert.Equal(template.Id, updated.SourceDefinitionId);
        var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 3, 2);
        Assert.Equal(1, publication.Version); Assert.Equal(draft.DefinitionId, publication.Payload.Id);
        Assert.Null(await f.Admin.GetPublicationAsync(draft.DefinitionId, 1));
        Assert.Equal(approved.Content, (await f.Service.ReadPublicationResourceAsync(owner.OwnerPrincipalId, owner.TenantId, id, 1, binding.ResourceId)).Content);
        var another = await f.Service.ForkTemplateAsync(owner.OwnerPrincipalId, owner.TenantId, template.Id, template.Version, null, 4);
        Assert.NotEqual(draft.DefinitionId, another.DefinitionId);
        var anotherBinding = Assert.Single((await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, another.DraftId)).Resources);
        Assert.NotEqual(binding.ResourceId, anotherBinding.ResourceId);
        Assert.Equal(binding.ContentSha256, anotherBinding.ContentSha256);
        var unchanged = (await Fixture.ShippedTemplates().ReadAsync(template.Id, template.Version))!.Definition;
        Assert.Equal(template.Identity, unchanged.Identity); Assert.Equal(template.AgentInstructions, unchanged.AgentInstructions);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(template.Id, (await reopened.FindDefinitionDraftAsync(owner.TenantId, another.DraftId))!.SourceDefinitionId);
            Assert.Equal(binding.ContentSha256, Assert.Single(await reopened.ListOwnedPublicationResourcesAsync(owner.TenantId, id, 1)).ContentSha256);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Template_workspace_and_skill_dependencies_are_copied_once_and_source_provenance_is_immutable(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); await using var files = await TemplateFiles.Create();
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var service = f.WithTemplates(files.Source);
        var draft = await service.ForkTemplateAsync(owner.OwnerPrincipalId, owner.TenantId, files.Definition.Id, 1, null, 1);
        var snapshot = await service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId);
        Assert.Equal(2, snapshot.Resources.Count);
        var welcome = snapshot.Resources.Single(r => r.Kind == AgentDefinitionResourceKind.Template);
        Assert.Equal("notes/welcome.txt", welcome.LogicalPath);
        Assert.Equal("Original workspace bytes", System.Text.Encoding.UTF8.GetString((await service.ReadDraftResourceAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, welcome.ResourceId)).Content));
        await File.WriteAllTextAsync(files.Welcome, "Changed shipped file after capture.");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var next = state with { Tenant = state.Tenant with { Revision = 3 } };
        foreach (var forged in new[] { draft with { SourceDefinitionId = "another-template" }, draft with { SourceVersion = 99 }, draft with { SourceKind = DefinitionDraftSourceKind.New } })
            Assert.False(await f.Definitions.TrySaveDefinitionDraftAsync(next, 2, forged with { Revision = 2 }, 1, f.Audit(owner.TenantId, owner.OwnerPrincipalId, Guid.Parse(draft.DefinitionId))));
        var publication = await service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 2, 1);
        Assert.Equal("Original workspace bytes", System.Text.Encoding.UTF8.GetString((await service.ReadPublicationResourceAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, welcome.ResourceId)).Content));
        Assert.Equal("notes", publication.Payload.Environment!.WorkspacePolicy.TemplateId);
        Assert.Equal("Changed shipped file after capture.", await File.ReadAllTextAsync(files.Welcome));
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "secret")]
    [InlineData(true, "secret")]
    [InlineData(false, "file-link")]
    [InlineData(true, "file-link")]
    [InlineData(false, "directory-link")]
    [InlineData(true, "directory-link")]
    [InlineData(false, "knowledge-link")]
    [InlineData(true, "knowledge-link")]
    [InlineData(false, "oversized")]
    [InlineData(true, "oversized")]
    [InlineData(false, "unapproved-skill")]
    [InlineData(true, "unapproved-skill")]
    public async Task Invalid_approved_template_content_never_leaves_partial_root_draft_bindings_or_audit(bool sqlite, string fault)
    {
        await using var f = await Fixture.Create(sqlite); await using var files = await TemplateFiles.Create();
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        if (fault == "missing") File.Delete(files.Welcome);
        if (fault == "secret") await File.WriteAllTextAsync(files.Welcome, "OPENAI_API_KEY=must-never-be-copied");
        if (fault == "file-link") { File.Delete(files.Welcome); File.CreateSymbolicLink(files.Welcome, files.Knowledge); }
        if (fault == "directory-link") { Directory.Delete(Path.GetDirectoryName(files.Welcome)!, true); Directory.CreateSymbolicLink(Path.GetDirectoryName(files.Welcome)!, Path.GetDirectoryName(files.Knowledge)!); }
        if (fault == "knowledge-link") { File.Delete(files.Knowledge); File.CreateSymbolicLink(files.Knowledge, files.Welcome); }
        if (fault == "oversized") { await using var file = File.OpenWrite(files.Welcome); file.SetLength(AgentResourceLimits.MaxItemBytes + 1); }
        if (fault == "unapproved-skill") files.Definition = files.Definition with { Skills = [files.Definition.SkillList[0] with { ResourcePaths = ["private-configuration.json"] }] };
        var count = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAnyAsync<AgentCoreException>(() => f.WithTemplates(files.Source).ForkTemplateAsync(owner.OwnerPrincipalId, owner.TenantId, files.Definition.Id, 1, null, 1).AsTask());
        Assert.Equal(0, (await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId)).Total);
        Assert.Equal(1, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        Assert.Equal(count, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        if (f.Contexts is { } contexts)
        {
            await using var db = contexts.CreateDbContext();
            Assert.False(await db.AgentDefinitionDrafts.IgnoreQueryFilters().AnyAsync());
            Assert.False(await db.AgentDefinitionDraftResources.IgnoreQueryFilters().AnyAsync());
        }
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public async Task Prepared_template_fork_loses_security_account_or_cancellation_changes_without_owned_rows(bool sqlite, int mode)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var account = await f.Identity.RegisterAsync("author@example.test", "Author", "strong-author-password");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var invitation = await f.Access.InviteAsync(owner.OwnerPrincipalId, owner.TenantId, account.LoginKey, state.Roles.Single(r => r.Key == "contributor").RoleId, 1);
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, owner.TenantId, invitation.Invitation.InvitationId, invitation.Token);
        state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var paused = new PausedTemplate(Fixture.ShippedTemplates()); using var cancellation = new CancellationTokenSource();
        var pending = f.WithTemplates(paused).ForkTemplateAsync(user.PrincipalId, owner.TenantId, "customer-support", 3, "Must not commit", state.Tenant.Revision, cancellation.Token).AsTask();
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (mode == 0) await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Keep this draft", state.Tenant.Revision);
        if (mode == 1)
        {
            var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
            await new AccountAdministrationService(f.Iam, f.Authorizer, f.Tokens, f.Ids, TimeProvider.System).SetStatusAsync(system.PrincipalId, account.AccountId, IdentityStatus.Suspended);
        }
        if (mode == 2) cancellation.Cancel();
        paused.Release.TrySetResult(true);
        if (mode == 0) await Assert.ThrowsAsync<SecurityConflictException>(() => pending);
        if (mode == 1) Assert.Equal("account_unavailable", (await Assert.ThrowsAsync<AccessDeniedException>(() => pending)).ReasonCode);
        if (mode == 2) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var inventory = await f.Service.ListAsync(owner.OwnerPrincipalId, owner.TenantId);
        Assert.Equal(mode == 0 ? 1 : 0, inventory.Total);
        Assert.DoesNotContain(inventory.Items, d => d.DisplayName == "Must not commit");
        Assert.DoesNotContain(await f.Iam.ReadAuditAsync(owner.TenantId, 200), a => a.Action == "agent-definitions.fork-template");
    }

    private sealed class TemplateFiles : IAsyncDisposable
    {
        private string Root { get; } = Path.Combine(Path.GetTempPath(), "template-fork-" + Guid.NewGuid().ToString("N"));
        public AgentDefinition Definition { get; set; } = null!;
        public string Welcome => Path.Combine(Root, "templates", "notes", "welcome.txt");
        public string Knowledge => Path.Combine(Root, "agents", "knowledge", "support-order-policy.md");
        public IBuiltInDefinitionTemplateSource Source => new FileBuiltInDefinitionTemplateSource(new SingleTemplate(Definition), Path.Combine(Root, "agents"), Path.Combine(Root, "templates"));
        public static async Task<TemplateFiles> Create()
        {
            var f = new TemplateFiles();
            var source = (await Fixture.ShippedTemplates().ReadAsync("customer-support", 3))!;
            f.Definition = source.Definition with { Id = "shipped-test", Version = 1,
                Environment = source.Definition.Environment! with { Workspace = new("notes") },
                Skills = [new("approved-files", "Approved files", "Use approved files.", "Read the supplied workspace note.", SkillProjection.OnDemand, false, [], ["notes/welcome.txt"])] };
            Directory.CreateDirectory(Path.GetDirectoryName(f.Welcome)!); Directory.CreateDirectory(Path.GetDirectoryName(f.Knowledge)!);
            await File.WriteAllBytesAsync(f.Knowledge, source.Resources.Single().Content);
            await File.WriteAllTextAsync(f.Welcome, "Original workspace bytes");
            var excluded = Path.Combine(Path.GetDirectoryName(f.Welcome)!, "harness"); Directory.CreateDirectory(excluded);
            await File.WriteAllTextAsync(Path.Combine(excluded, "excluded.txt"), "OPENAI_API_KEY=not-an-approved-workspace-file");
            return f;
        }
        public ValueTask DisposeAsync() { if (Directory.Exists(Root)) Directory.Delete(Root, true); return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_template_bindings_verify_all_bytes_before_committing_any_owned_graph(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var state = (await f.Iam.ReadTenantAsync(owner.TenantId))!;
        var id = f.Ids.NewId(); var now = DateTimeOffset.UtcNow;
        var candidate = AgentDefinitionStarter.Create(id.ToString("D"), SyntheticProviderAliases.Default);
        var draft = new AgentDefinitionDraft(f.Ids.NewId(), candidate.DefinitionId, 1, candidate, DefinitionDraftSourceKind.ForkBuiltIn, 3, now, now, "customer-support");
        var first = "First approved bytes"u8.ToArray(); var second = "Second approved bytes"u8.ToArray();
        var firstHash = DefinitionResourceContentHasher.ComputeSha256Hex(first); var secondHash = DefinitionResourceContentHasher.ComputeSha256Hex(second);
        await f.Content.StoreVerifiedAsync(firstHash, first);
        var resources = new[] { new AgentDefinitionDraftResource(f.Ids.NewId(), draft.DraftId, "first.txt", AgentDefinitionResourceKind.Reference, "text/plain", firstHash, first.Length, now),
            new AgentDefinitionDraftResource(f.Ids.NewId(), draft.DraftId, "second.txt", AgentDefinitionResourceKind.Reference, "text/plain", secondHash, second.Length, now) };
        var next = state with { Tenant = state.Tenant with { Revision = 2 } }; var before = (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count;
        await Assert.ThrowsAsync<AgentCoreException>(() => f.Definitions.TryCreateDefinitionAsync(next, 1, new(owner.TenantId, id, now), draft, resources, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)).AsTask());
        Assert.Null(await f.Definitions.FindDefinitionAsync(owner.TenantId, id)); Assert.Null(await f.Definitions.FindDefinitionDraftAsync(owner.TenantId, draft.DraftId));
        Assert.Equal(1, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision); Assert.Equal(before, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
        await f.Content.StoreVerifiedAsync(secondHash, second);
        Assert.True(await f.Definitions.TryCreateDefinitionAsync(next, 1, new(owner.TenantId, id, now), draft, resources, f.Audit(owner.TenantId, owner.OwnerPrincipalId, id)));
        Assert.Equal(2, (await f.Service.ReadDraftStateAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId)).Resources.Count);
        Assert.Equal(before + 1, (await f.Iam.ReadAuditAsync(owner.TenantId, 200)).Count);
    }
    private sealed class SingleTemplate(AgentDefinition definition) : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([definition]);
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(id == definition.Id && (version is null || version == definition.Version) ? definition : null);
    }
    private sealed class PausedTemplate(IBuiltInDefinitionTemplateSource actual) : IBuiltInDefinitionTemplateSource
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => actual.ListAsync(ct);
        public async ValueTask<BuiltInDefinitionTemplate?> ReadAsync(string templateId, int version, CancellationToken ct = default)
        { var source = await actual.ReadAsync(templateId, version, ct); Entered.TrySetResult(true); await Release.Task.WaitAsync(ct); return source; }
    }
}
