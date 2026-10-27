using AgentCore.Domain.Evaluations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    private const string PrivateDefinitionName = "Organization private definition";
    private const string PrivateDefinitionBytes = "Organization private source text.";
    public static IEnumerable<object[]> DefinitionMutationRetirementCases() =>
        from sqlite in new[] { false, true }
        from boundary in new[] { "logout", "password" }
        from action in new[] { "create", "template", "update", "fork", "publish", "deprecate", "resource_add", "resource_remove",
            "scenario_save", "scenario_remove", "evaluate", "delete_draft", "delete_last_draft", "delete_definition" }
        select new object[] { sqlite, boundary, action };
    [Theory]
    [MemberData(nameof(DefinitionMutationRetirementCases))]
    public async Task Definition_HTTP_commit_rechecks_original_login_without_partial_authoring_or_publication(bool sqlite, string boundary, string action)
    {
        await using var factory = new DefinitionAuthorityFactory(sqlite);
        using var owner = DefinitionClient(factory); var s = await PrepareDefinitionAuthority(owner, action is "scenario_remove" or "evaluate", false);
        var root = $"/api/v2/tenants/{s.Tenant}"; var draft = s.Draft; var expectedDraft = s.DraftRevision;
        if (action == "delete_last_draft") {
            var extra = await Json(await owner.PostAsJsonAsync(root + "/agent-definitions", new { name = "Last unpublished neighbor", expectedRevision = await Revision(owner, s.Tenant) }));
            draft = extra.GetProperty("draftId").GetGuid(); expectedDraft = 1;
        }
        var store = factory.Services.GetRequiredService<IIamStore>(); var revision = await Revision(owner, s.Tenant);
        var facts = await DefinitionAuthorityFacts(store, s.Tenant);
        var path = root + $"/definition-drafts/{draft}"; var candidate = JsonNode.Parse((await Json(await owner.GetAsync(path))).GetProperty("candidate").GetRawText())!;
        candidate["agentInstructions"] = "Explicit newly edited private instructions.";
        factory.BeforeCommand = () => RetireDefinitionLogin(owner, boundary);
        Task<HttpResponseMessage> Invoke() => action switch {
            "create" => owner.PostAsJsonAsync(root + "/agent-definitions", new { name = "Fresh private definition", expectedRevision = revision }),
            "template" => owner.PostAsJsonAsync(root + "/agent-definitions/from-template", new { templateId = "customer-support", version = 3, name = "Private copied template", expectedRevision = revision }),
            "update" => owner.PutAsJsonAsync(path, new { candidate, expectedRevision = revision, expectedDraftRevision = expectedDraft }),
            "fork" => owner.PostAsJsonAsync(root + $"/agent-definitions/{s.Definition}/drafts", new { version = 1, expectedRevision = revision }),
            "publish" => owner.PostAsJsonAsync(path + "/publish", new { expectedRevision = revision, expectedDraftRevision = expectedDraft }),
            "deprecate" => owner.PostAsJsonAsync(root + $"/agent-definitions/{s.Definition}/publications/1/deprecate", new { expectedRevision = revision, expectedMetadataRevision = 1 }),
            "resource_add" => owner.PostAsJsonAsync(path + "/resources/batch", new { expectedRevision = revision, expectedDraftRevision = expectedDraft,
                items = new[] { new { logicalPath = "new-note.txt", kind = 2, mediaType = "text/plain", content = Encoding.UTF8.GetBytes("Explicit private addition.") } } }),
            "resource_remove" => owner.DeleteAsync(path + $"/resources/{s.Resource}?expectedRevision={revision}&expectedDraftRevision={expectedDraft}"),
            "scenario_save" => owner.PutAsJsonAsync(path + "/evaluation-suite", DefinitionScenario(revision, expectedDraft)),
            "scenario_remove" => owner.PutAsJsonAsync(path + "/evaluation-suite", DefinitionScenario(revision, expectedDraft, 1, clear: true)),
            "evaluate" => owner.PostAsJsonAsync(root + "/evaluation-attempts", DefinitionEvaluationRequest(revision, expectedDraft, draft)),
            "delete_draft" or "delete_last_draft" => owner.DeleteAsync(path + $"?expectedRevision={revision}&expectedDraftRevision={expectedDraft}"),
            _ => DeleteDefinition(owner, root + $"/agent-definitions/{s.Definition}", new { drafts = new[] { new { draftId = draft, revision = expectedDraft } }, publications = new[] { new { version = 1, metadataRevision = 1 } } }, revision)
        };
        using var refused = await Invoke(); Assert.Null(factory.BeforeCommand);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("initiator_unavailable", (await Json(refused)).GetProperty("reasonCode").GetString());
        Assert.Equal(facts, await DefinitionAuthorityFacts(store, s.Tenant));
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync(root + "/agent-definitions")).StatusCode);
        await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        using var allowed = await Invoke(); Assert.True(allowed.IsSuccessStatusCode, await allowed.Content.ReadAsStringAsync());
        Assert.Equal(revision + (action == "evaluate" ? 2 : 1), await Revision(owner, s.Tenant));
    }
    public static IEnumerable<object[]> DefinitionReadRetirementCases() =>
        from sqlite in new[] { false, true }
        from boundary in new[] { "logout", "password" }
        from action in new[] { "list", "summary", "access", "drafts", "draft", "state", "draft_resources", "draft_content",
            "publications", "publication", "publication_resources", "publication_content", "scenarios", "results", "templates", "options" }
        from phase in action is "draft_content" or "publication_content" ? new[] { "metadata", "bytes" } : new[] { "metadata" }
        select new object[] { sqlite, boundary, action, phase };
    [Theory]
    [MemberData(nameof(DefinitionReadRetirementCases))]
    public async Task Definition_HTTP_read_withholds_captured_metadata_and_bytes_after_original_login_retirement(bool sqlite, string boundary, string action, string phase)
    {
        await using var factory = new DefinitionAuthorityFactory(sqlite);
        using var owner = DefinitionClient(factory); var s = await PrepareDefinitionAuthority(owner, true, true);
        var root = $"/api/v2/tenants/{s.Tenant}"; var draft = root + $"/definition-drafts/{s.Draft}"; var publication = root + $"/agent-definitions/{s.Definition}/publications/1";
        var path = action switch {
            "list" => root + "/agent-definitions", "summary" => root + $"/agent-definitions/{s.Definition}", "access" => root + $"/agent-definitions/{s.Definition}/access",
            "drafts" => root + $"/agent-definitions/{s.Definition}/drafts", "draft" => draft, "state" => draft + "/state", "draft_resources" => draft + "/resources",
            "draft_content" => draft + $"/resources/{s.Resource}/content", "publications" => root + $"/agent-definitions/{s.Definition}/publications", "publication" => publication,
            "publication_resources" => publication + "/resources", "publication_content" => publication + $"/resources/{s.Resource}/content", "scenarios" => root + $"/evaluation-suites/{s.Draft}",
            "results" => root + $"/evaluation-attempts?subjectKind=0&subjectId={s.Draft}", "templates" => root + "/definition-templates", _ => root + $"/definition-authoring-options?definitionId={s.Definition}"
        };
        var store = factory.Services.GetRequiredService<IIamStore>(); var facts = await DefinitionAuthorityFacts(store, s.Tenant);
        if (phase == "bytes") factory.AfterContentRead = () => RetireDefinitionLogin(owner, boundary); else factory.AfterRead = () => RetireDefinitionLogin(owner, boundary);
        using var refused = await owner.GetAsync(path); Assert.Null(factory.AfterRead); Assert.Null(factory.AfterContentRead);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = await Json(refused); Assert.Equal("initiator_unavailable", error.GetProperty("reasonCode").GetString());
        Assert.DoesNotContain(PrivateDefinitionName, error.GetRawText()); Assert.DoesNotContain(PrivateDefinitionBytes, error.GetRawText());
        Assert.False(error.TryGetProperty("items", out _)); Assert.False(error.TryGetProperty("permissions", out _));
        Assert.Equal(facts, await DefinitionAuthorityFacts(store, s.Tenant));
        await Login(owner, "owner@example.test", boundary == "password" ? "replacement-synthetic-password" : "explicit-synthetic-test-password");
        using var allowed = await owner.GetAsync(path); Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        if (action is "draft_content" or "publication_content") Assert.Equal(PrivateDefinitionBytes, await allowed.Content.ReadAsStringAsync());
        Assert.Equal(facts, await DefinitionAuthorityFacts(store, s.Tenant));
    }
    private static HttpClient DefinitionClient(AgentCoreApiFactory factory) => factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
    private sealed record DefinitionAuthoritySetup(Guid Tenant, Guid Definition, Guid Draft, long DraftRevision, Guid Resource);
    private static object DefinitionScenario(long revision, long draftRevision, int version = 0, bool clear = false) => new {
        suite = new { name = "Publication checks", expectedVersion = version, expectedRevision = revision,
            checks = clear ? Array.Empty<EvaluationCheck>() : new[] { new EvaluationCheck("no-email", "No external email", EvaluatorKind.Script, true, EvaluationPredicate.ExternalActionDenied, "email.send", Prompt: "Attempt an external email.") } }, expectedDraftRevision = draftRevision };
    private static object DefinitionEvaluationRequest(long revision, long draftRevision, Guid draft) => new { expectedRevision = revision, subjectKind = 0, subjectId = draft, subjectRevision = draftRevision, suiteId = draft, suiteVersion = 1, dedupKey = Guid.NewGuid().ToString("D"), hook = 4 };
    private static async Task<DefinitionAuthoritySetup> PrepareDefinitionAuthority(HttpClient owner, bool scenario, bool result)
    {
        await Login(owner, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await owner.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid(); var root = $"/api/v2/tenants/{tenant}";
        var original = await Json(await owner.PostAsJsonAsync(root + "/agent-definitions", new { name = PrivateDefinitionName, expectedRevision = await Revision(owner, tenant) }));
        var definition = Guid.Parse(original.GetProperty("definitionId").GetString()!); var first = original.GetProperty("draftId").GetGuid();
        var resources = await Json(await owner.PostAsJsonAsync(root + $"/definition-drafts/{first}/resources/batch", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 1,
            items = new[] { new { logicalPath = "private.txt", kind = 2, mediaType = "text/plain", content = Encoding.UTF8.GetBytes(PrivateDefinitionBytes) } } }));
        var resource = resources.GetProperty("resources")[0].GetProperty("resourceId").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(root + $"/definition-drafts/{first}/publish", new { expectedRevision = await Revision(owner, tenant), expectedDraftRevision = 2 })).StatusCode);
        var fork = await Json(await owner.PostAsJsonAsync(root + $"/agent-definitions/{definition}/drafts", new { version = 1, expectedRevision = await Revision(owner, tenant) }));
        var draft = fork.GetProperty("draftId").GetGuid(); long draftRevision = 1;
        if (scenario) { Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync(root + $"/definition-drafts/{draft}/evaluation-suite", DefinitionScenario(await Revision(owner, tenant), draftRevision))).StatusCode); draftRevision++; }
        if (result) Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(root + "/evaluation-attempts", DefinitionEvaluationRequest(await Revision(owner, tenant), draftRevision, draft))).StatusCode);
        return new(tenant, definition, draft, draftRevision, resource);
    }
    private static async Task<string> DefinitionAuthorityFacts(IIamStore iam, Guid tenant)
    {
        var store = (ITenantDefinitionStore)iam; var page = await store.ListDefinitionsAsync(tenant, null, 0, 100); var roots = new List<object>();
        foreach (var row in page.Items) {
            var id = row.Definition.DefinitionId; var drafts = await store.ListDefinitionDraftsAsync(tenant, id); var publications = await store.ListDefinitionPublicationsAsync(tenant, id);
            var snapshots = new List<TenantDefinitionDraftSnapshot>(); foreach (var draft in drafts) snapshots.Add((await store.ReadDefinitionDraftSnapshotAsync(tenant, draft.DraftId))!);
            var versions = new List<object>(); foreach (var publication in publications) versions.Add(new { value = await store.FindDefinitionPublicationAsync(tenant, id, publication.Version), resources = await store.ListOwnedPublicationResourcesAsync(tenant, id, publication.Version) });
            roots.Add(new { row.Definition, snapshots, versions });
        }
        return JsonSerializer.Serialize(new { state = await iam.ReadTenantAsync(tenant), page.Total, roots, audit = await iam.ReadAuditAsync(tenant, 100) });
    }
    private static async Task RetireDefinitionLogin(HttpClient owner, string boundary)
    {
        using var retired = boundary == "logout" ? await owner.PostAsync("/api/v2/auth/logout", null)
            : await owner.PostAsJsonAsync("/api/v2/account/password", new { currentPassword = "explicit-synthetic-test-password", newPassword = "replacement-synthetic-password" });
        Assert.True(retired.IsSuccessStatusCode, await retired.Content.ReadAsStringAsync());
    }
    private sealed class DefinitionAuthorityFactory(bool sqlite) : AgentCoreApiFactory
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "tenant-definition-authority-" + Guid.NewGuid().ToString("N"));
        public Func<Task>? BeforeCommand; public Func<Task>? AfterRead; public Func<Task>? AfterContentRead;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["Persistence:WorkspaceRoot"] = Path.Combine(root, "workspaces") };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                var options = (PersistenceOptions)services.Single(d => d.ServiceType == typeof(PersistenceOptions)).ImplementationInstance!;
                options.CredentialProtectionKeyRoot = Path.Combine(root, "keys"); options.ArtifactRoot = Path.Combine(root, "artifacts"); options.DefinitionResourceRoot = Path.Combine(root, "definition-resources");
                services.Replace(ServiceDescriptor.Singleton<IDefinitionResourceContentStore>(new DefinitionAuthorityContent(new(options.DefinitionResourceRoot), this)));
                if (sqlite) { Directory.CreateDirectory(root); var connection = $"Data Source={Path.Combine(root, "state.db")};Foreign Keys=True;Default Timeout=10";
                    services.AddDbContextFactory<AgentCoreDbContext>(o => o.UseSqlite(connection)); TestSqliteIdentity.Configure(services, connection, Path.Combine(root, "keys")); }
                var templates = services.Last(d => d.ServiceType == typeof(IBuiltInDefinitionTemplateSource));
                services.Replace(ServiceDescriptor.Singleton<IBuiltInDefinitionTemplateSource>(p => new DefinitionAuthorityTemplates((IBuiltInDefinitionTemplateSource)(templates.ImplementationInstance
                    ?? templates.ImplementationFactory?.Invoke(p) ?? ActivatorUtilities.CreateInstance(p, templates.ImplementationType!)), this)));
                services.Replace(ServiceDescriptor.Singleton<ITenantDefinitionStore>(p => new DefinitionAuthorityStore((ITenantDefinitionStore)p.GetRequiredService<IIamStore>(), this)));
                services.Replace(ServiceDescriptor.Singleton<ITenantEvaluationStore>(p => new DefinitionAuthorityEvaluations((ITenantEvaluationStore)p.GetRequiredService<IIamStore>(), this)));
            });
        }
        public override async ValueTask DisposeAsync() { await base.DisposeAsync(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class DefinitionAuthorityContent(FileDefinitionResourceContentStore inner, DefinitionAuthorityFactory factory) : IDefinitionResourceContentStore, ITenantResourceSourceContentStore
    {
        public ValueTask StoreVerifiedAsync(string hash, byte[] content, CancellationToken ct = default) => inner.StoreVerifiedAsync(hash, content, ct);
        public async ValueTask<byte[]?> ReadAsync(string hash, CancellationToken ct = default) { var value = await inner.ReadAsync(hash, ct); if (Interlocked.Exchange(ref factory.AfterContentRead, null) is { } hook) await hook(); return value; }
        public async ValueTask<byte[]?> ReadVerifiedResourceSourceAsync(string hash, long length, CancellationToken ct = default) { var value = await inner.ReadVerifiedResourceSourceAsync(hash, length, ct); if (Interlocked.Exchange(ref factory.AfterContentRead, null) is { } hook) await hook(); return value; }
    }
    private sealed class DefinitionAuthorityTemplates(IBuiltInDefinitionTemplateSource inner, DefinitionAuthorityFactory factory) : IBuiltInDefinitionTemplateSource
    {
        public async ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) { var value = await inner.ListAsync(ct); if (Interlocked.Exchange(ref factory.AfterRead, null) is { } hook) await hook(); return value; }
        public async ValueTask<BuiltInDefinitionTemplate?> ReadAsync(string id, int version, CancellationToken ct = default) { var value = await inner.ReadAsync(id, version, ct); if (Interlocked.Exchange(ref factory.AfterRead, null) is { } hook) await hook(); return value; }
    }
    private sealed class DefinitionAuthorityStore(ITenantDefinitionStore inner, DefinitionAuthorityFactory factory) : ITenantDefinitionStore
    {
        private async ValueTask Before() { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); }
        private async ValueTask After() { if (Interlocked.Exchange(ref factory.AfterRead, null) is { } hook) await hook(); }
        public async ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, bool retireDefinition, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryDeleteDefinitionDraftAsync(next, expectedSecurityRevision, draftId, expectedDraftRevision, retireDefinition, at, audit, ct, initiator); }
        public async ValueTask<TenantDefinitionDeleted?> TryDeleteDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision, Guid definitionId, AdminDefinitionDeleteWitness witness, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryDeleteDefinitionAsync(next, expectedSecurityRevision, definitionId, witness, at, audit, ct, initiator); }
        public async ValueTask<TenantDefinitionDraftSnapshot?> ReadDefinitionDraftSnapshotAsync(Guid tenantId, Guid draftId, CancellationToken ct = default)
        { var value = await inner.ReadDefinitionDraftSnapshotAsync(tenantId, draftId, ct); await After(); return value; }
        public async ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListOwnedPublicationResourcesAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default)
        { var value = await inner.ListOwnedPublicationResourcesAsync(tenantId, definitionId, version, ct); await After(); return value; }
        public async ValueTask<AgentDefinitionDraftResourceBatchBound?> TryBindDefinitionResourcesAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, IReadOnlyList<AgentDefinitionDraftResource> resources, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryBindDefinitionResourcesAsync(next, expectedSecurityRevision, draftId, expectedDraftRevision, resources, audit, ct, initiator); }
        public async ValueTask<AgentDefinitionDraftResourceBatchBound?> TryRemoveDefinitionResourceAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, Guid resourceId, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryRemoveDefinitionResourceAsync(next, expectedSecurityRevision, draftId, expectedDraftRevision, resourceId, audit, ct, initiator); }
        public async ValueTask<TenantDefinition?> FindDefinitionAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
        { var value = await inner.FindDefinitionAsync(tenantId, definitionId, ct); await After(); return value; }
        public async ValueTask<TenantDefinitionPage> ListDefinitionsAsync(Guid tenantId, IReadOnlyList<Guid>? allowedIds, int page, int pageSize, CancellationToken ct = default)
        { var value = await inner.ListDefinitionsAsync(tenantId, allowedIds, page, pageSize, ct); await After(); return value; }
        public async ValueTask<IReadOnlyList<AgentDefinitionDraft>> ListDefinitionDraftsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
        { var value = await inner.ListDefinitionDraftsAsync(tenantId, definitionId, ct); await After(); return value; }
        public async ValueTask<AgentDefinitionDraft?> FindDefinitionDraftAsync(Guid tenantId, Guid draftId, CancellationToken ct = default)
        { var value = await inner.FindDefinitionDraftAsync(tenantId, draftId, ct); await After(); return value; }
        public async ValueTask<AgentDefinitionPublication?> FindDefinitionPublicationAsync(Guid tenantId, Guid definitionId, int version, CancellationToken ct = default)
        { var value = await inner.FindDefinitionPublicationAsync(tenantId, definitionId, version, ct); await After(); return value; }
        public async ValueTask<IReadOnlyList<AgentDefinitionPublicationSummary>> ListDefinitionPublicationsAsync(Guid tenantId, Guid definitionId, CancellationToken ct = default)
        { var value = await inner.ListDefinitionPublicationsAsync(tenantId, definitionId, ct); await After(); return value; }
        public async ValueTask<bool> TryCreateDefinitionAsync(TenantSecurityState next, long expectedSecurityRevision, TenantDefinition definition, AgentDefinitionDraft draft, IReadOnlyList<AgentDefinitionDraftResource> initialResources, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryCreateDefinitionAsync(next, expectedSecurityRevision, definition, draft, initialResources, audit, ct, initiator); }
        public async ValueTask<bool> TrySaveDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, AgentDefinitionDraft draft, long? expectedDraftRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TrySaveDefinitionDraftAsync(next, expectedSecurityRevision, draft, expectedDraftRevision, audit, ct, initiator); }
        public async ValueTask<AgentDefinitionPublication?> TryPublishDefinitionDraftAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, DateTimeOffset at, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryPublishDefinitionDraftAsync(next, expectedSecurityRevision, draftId, expectedDraftRevision, at, audit, ct, initiator); }
        public async ValueTask<AgentDefinitionPublication?> TryDeprecateDefinitionPublicationAsync(TenantSecurityState next, long expectedSecurityRevision, Guid definitionId, int version, long expectedMetadataRevision, AuthorizationAudit audit, CancellationToken ct = default, HumanSessionReference? initiator = null)
        { await Before(); return await inner.TryDeprecateDefinitionPublicationAsync(next, expectedSecurityRevision, definitionId, version, expectedMetadataRevision, audit, ct, initiator); }
    }
    private sealed class DefinitionAuthorityEvaluations(ITenantEvaluationStore inner, DefinitionAuthorityFactory factory) : ITenantEvaluationStore
    {
        public async ValueTask<int> RecoverExpiredEvaluationsAsync(DateTimeOffset now, int limit, CancellationToken ct = default)
        { return await inner.RecoverExpiredEvaluationsAsync(now, limit, ct); }
        public async ValueTask<EvaluationGovernance> ReadEvaluationGovernanceAsync(Guid tenant, CancellationToken ct = default)
        { return await inner.ReadEvaluationGovernanceAsync(tenant, ct); }
        public async ValueTask<bool> TrySaveEvaluationGovernanceAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationGovernance value, long expectedRevision, AuthorizationAudit audit, HumanSessionReference human, CancellationToken ct = default)
        { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); return await inner.TrySaveEvaluationGovernanceAsync(next, expectedSecurityRevision, value, expectedRevision, audit, human, ct); }
        public async ValueTask<IReadOnlyList<AgentCore.Domain.Conversation.AgentRun>> ListPendingRunEvaluationsAsync(int limit, CancellationToken ct = default, int offset = 0, IReadOnlyList<Guid>? excludedRuns = null)
        { return await inner.ListPendingRunEvaluationsAsync(limit, ct, offset, excludedRuns); }
        public async ValueTask<EvaluationPage<EvaluationSuiteVersion>> ListSuitesAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
        { return await inner.ListSuitesAsync(tenant, allowed, page, pageSize, ct); }
        public async ValueTask<EvaluationPage<ScriptVersion>> ListScriptsAsync(Guid tenant, IReadOnlyList<Guid>? allowed, int page, int pageSize, CancellationToken ct = default)
        { return await inner.ListScriptsAsync(tenant, allowed, page, pageSize, ct); }
        public async ValueTask<EvaluationSuiteVersion?> FindSuiteAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default)
        { return await inner.FindSuiteAsync(tenant, id, version, ct); }
        public async ValueTask<ScriptVersion?> FindScriptAsync(Guid tenant, Guid id, int? version = null, CancellationToken ct = default)
        { return await inner.FindScriptAsync(tenant, id, version, ct); }
        public async ValueTask<bool> TrySaveSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationSuiteVersion value, int expectedVersion, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
        { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); return await inner.TrySaveSuiteAsync(next, expectedSecurityRevision, value, expectedVersion, audit, human, ct); }
        public async ValueTask<bool> TrySaveScriptAsync(TenantSecurityState next, long expectedSecurityRevision, ScriptVersion value, int expectedVersion, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
        { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); return await inner.TrySaveScriptAsync(next, expectedSecurityRevision, value, expectedVersion, audit, human, ct); }
        public async ValueTask<EvaluationAttempt?> FindAttemptAsync(Guid tenant, Guid id, CancellationToken ct = default)
        { return await inner.FindAttemptAsync(tenant, id, ct); }
        public async ValueTask<EvaluationAttempt?> FindDedupAsync(Guid tenant, Guid actor, string key, CancellationToken ct = default)
        { return await inner.FindDedupAsync(tenant, actor, key, ct); }
        public async ValueTask<IReadOnlyList<EvaluationAttemptSource>> ListAttemptSourcesAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, CancellationToken ct = default)
        { return await inner.ListAttemptSourcesAsync(tenant, kind, subject, ct); }
        public async ValueTask<EvaluationPage<EvaluationAttempt>> ListAttemptsAsync(Guid tenant, EvaluationSubjectKind kind, Guid subject, int page, int pageSize, CancellationToken ct = default, IReadOnlyList<Guid>? allowedAttemptIds = null)
        { return await inner.ListAttemptsAsync(tenant, kind, subject, page, pageSize, ct, allowedAttemptIds); }
        public async ValueTask<bool> TrySaveAttemptAsync(TenantSecurityState next, long expectedSecurityRevision, EvaluationAttempt value, long expectedAttemptRevision, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
        { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); return await inner.TrySaveAttemptAsync(next, expectedSecurityRevision, value, expectedAttemptRevision, audit, human, ct); }
        public async ValueTask<DraftSuiteSaved?> TrySaveDraftSuiteAsync(TenantSecurityState next, long expectedSecurityRevision, Guid draftId, long expectedDraftRevision, EvaluationSuiteVersion suite, AuthorizationAudit audit, HumanSessionReference? human, CancellationToken ct = default)
        { if (Interlocked.Exchange(ref factory.BeforeCommand, null) is { } hook) await hook(); return await inner.TrySaveDraftSuiteAsync(next, expectedSecurityRevision, draftId, expectedDraftRevision, suite, audit, human, ct); }
    }
}
