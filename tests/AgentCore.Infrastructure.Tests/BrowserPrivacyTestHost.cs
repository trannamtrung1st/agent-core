using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

internal sealed class BrowserPrivacyTestHost
{
    public IIamStore Iam { get; private init; } = null!;
    public BrowserPrivacyService Runtime { get; private init; } = null!;
    public IdentityService Identity { get; private init; } = null!;
    public HumanSessionReference Human { get; private init; } = null!;
    public IIdGenerator Ids { get; } = new SystemIdGenerator(TimeProvider.System);
    public BrowserScreenshotPolicy Effective => Runtime.Effective;
    public SystemBrowserPrivacyService Service(ISystemBrowserPrivacyStore? store = null) => new(Iam, store ?? (ISystemBrowserPrivacyStore)Iam, Runtime, Identity, new(Iam), TimeProvider.System, Ids);
    public ValueTask ActivateAtStartupAsync() => Runtime.ActivateAtStartupAsync();
    public ValueTask<BrowserPrivacyView> ReadAsync() => Service().ReadAsync(Human);
    public ValueTask<BrowserPrivacyView> SaveAsync(long revision, string mode, IReadOnlyList<string> unmasked, IReadOnlyList<string> graphics, bool acknowledge) => Service().SaveAsync(Human, new(revision, mode, unmasked, graphics, acknowledge));
    public static async Task<BrowserPrivacyTestHost> Create(BrowserPrivacyAuthority authority, IDbContextFactory<AgentCoreDbContext>? contexts = null)
    {
        var ids = new SystemIdGenerator(TimeProvider.System); IIamStore iam; IBrowserPrivacyStore backing;
        if (contexts is not null) { backing = new SqliteBrowserPrivacyStore(contexts); iam = new SqliteIamStore(contexts, clock: TimeProvider.System, browserPrivacyAuthority: authority); }
        else {
            var instances = new InMemoryAgentInstanceStore(); var privacy = new InMemoryBrowserPrivacyStore(); backing = privacy;
            iam = new InMemoryIamStore(instances, new InMemoryAgentDefinitionAdminStore(ids), new InMemoryMemoryStore(instances), browserPrivacy: privacy, browserPrivacyAuthority: authority);
        }
        var identity = new IdentityService(iam, new AccountPasswordHasher(), new IdentityTokens(), ids, TimeProvider.System);
        if (!await iam.HasBootstrapAsync()) await identity.BootstrapAsync("privacy-operator@example.test", "Privacy operator", "strong-privacy-password", "Private organization", "private-privacy");
        var login = (await identity.SignInAsync("privacy-operator@example.test", "strong-privacy-password"))!;
        var principal = (await iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == login.Account.AccountId);
        return new() { Iam = iam, Identity = identity, Runtime = new(backing, authority), Human = new(login.Account.AccountId, login.Session.SessionId, principal.PrincipalId) };
    }
}
