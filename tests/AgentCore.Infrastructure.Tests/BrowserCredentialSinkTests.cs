using AgentCore.Tests.Shared;
using System.Text.Json;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserCredentialSinkTests(BrowserHostFixture fixture) : IClassFixture<BrowserHostFixture>
{
    [Fact]
    public async Task Existing_password_uses_current_binding_reflects_safely_and_unbind_preserves_login()
    {
        var origin = fixture.Session.Fixture.Origin!; var session = Guid.NewGuid(); var owner = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "browser-credential-" + Guid.NewGuid().ToString("N"));
        const string password = "browser-private-password-9374";
        try
        {
            var instances = new InMemoryAgentInstanceStore(); var now = DateTimeOffset.UtcNow;
            await instances.InsertAsync(new(owner, "secretary", 8, new("Test", "Secretary", "Test", "neutral"), AgentInstanceLifecycle.Active, now, now));
            var store = new InMemoryCredentialStore(instances);
            var service = new CredentialService(store, store, new LocalCredentialProtector(root), instances, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            var c = await service.CreateAsync("Store", "Password", new Dictionary<string,string> { ["username"] = "owner@example.test" }, [origin], password);
            var binding = await service.BindAsync(owner, c.CredentialId, "store-admin", 1);
            var executor = new SessionToolExecutor(browser: fixture.Session, agentInstances: instances, credentials: service, configurationGate: ToolConfigurationGates.AllowAll);
            var definition = await Definition(); var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner, SupportsVision: true);
            async Task<string> Execute(string tool, string args, ToolExecutionAdmission? a = null) => (await executor.ExecuteAsync(definition, session, new("call-" + Guid.NewGuid(), tool, args), ToolLimits.MaxOutputBytes, admission: a ?? admission)).Text;
            var nav = await Execute(ToolCatalog.BrowserNavigate, JsonSerializer.Serialize(new { url = origin + "/credential-login?reflect=1" }));
            Assert.DoesNotContain("error", nav);
            var passwordRef = (await BrowserTestQueries.Find(fixture.Session, session, "Password")).Target;
            var usernameRef = (await BrowserTestQueries.Find(fixture.Session, session, "Username")).Target;
            var resolvedNonPassword = false;
            var wrongField = await fixture.Session.FillCredentialAsync(session, usernameRef!, (_, _) => { resolvedNonPassword = true; return ValueTask.FromResult(password); });
            Assert.Equal("unsupported_operation", wrongField.ErrorCode);
            Assert.False(resolvedNonPassword);
            var raw = await Execute(ToolCatalog.BrowserType, JsonSerializer.Serialize(new { target = passwordRef, text = "model-supplied" }));
            Assert.Contains("forbidden", raw);
            var detached = await Execute(ToolCatalog.BrowserFillCredential, JsonSerializer.Serialize(new { target = passwordRef, credentialRef = "store-admin" }), admission with { Detached = true, TriggerKind = TriggerKind.ScheduledOccurrence });
            Assert.Contains("forbidden", detached);
            var unknown = await Execute(ToolCatalog.BrowserFillCredential, JsonSerializer.Serialize(new { target = passwordRef, credentialRef = "other-agent" }));
            Assert.Contains("Forbidden", unknown);
            var filled = await Execute(ToolCatalog.BrowserFillCredential, JsonSerializer.Serialize(new { target = passwordRef, credentialRef = "store-admin" }));
            Assert.DoesNotContain("error", filled); Assert.DoesNotContain(password, filled); Assert.Contains("redacted", filled);
            var observed = await Execute(ToolCatalog.BrowserSnapshot, "{}"); Assert.DoesNotContain(password, observed);
            var screenshot = await fixture.Session.ExecuteAsync(BrowserTestRequests.Screenshot(session));
            Assert.Null(screenshot.ErrorCode);
            Assert.NotEmpty(screenshot.Bytes!);
            // The reflected text has a child element; text-node masking must still cover it.
            Assert.True(screenshot.RedactionCount >= 2);
            var submit = (await BrowserTestQueries.Find(fixture.Session, session, "Sign in")).Target;
            var login = await Execute(ToolCatalog.BrowserClick, JsonSerializer.Serialize(new { target = submit }));
            Assert.Contains("AC-CREDENTIAL-1042", login); Assert.DoesNotContain(password, login);
            var resolvedStale = false;
            var stale = await fixture.Session.FillCredentialAsync(session, passwordRef!, (_, _) => { resolvedStale = true; return ValueTask.FromResult(password); });
            Assert.Equal("target_missing", stale.ErrorCode);
            Assert.False(resolvedStale);
            // Unbind denies future protected use without logging out the browser.
            await service.UnbindAsync(owner, binding.BindingId, 1, 1); Assert.Empty(await service.SafeMetadataAsync(owner));
            Assert.DoesNotContain(password, await Execute(ToolCatalog.BrowserSnapshot, "{}"));
            var listed = await executor.ExecuteAsync(definition, session, new("list", ToolCatalog.CredentialsList, "{}"), ToolLimits.MaxOutputBytes, admission: admission);
            Assert.Contains("\"items\":[]", listed.Text); Assert.DoesNotContain(password, listed.Text);
        }
        finally { await fixture.Session.ReleaseAsync(session); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Redaction_capacity_denies_new_password_effect_but_allows_reuse_and_masks_prior_reflection()
    {
        var session = Guid.NewGuid();
        try
        {
            var navigated = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(session, new(fixture.Session.Fixture.Origin + "/credential-login?reflect=1")));
            var reference = (await BrowserTestQueries.Find(fixture.Session, session, "Password")).Target;
            var first = new string('&', 60000);
            var filled = await fixture.Session.FillCredentialAsync(session, reference, (_, _) => ValueTask.FromResult(first));
            Assert.Null(filled.ErrorCode); Assert.DoesNotContain(first, filled.Observation!.Content!);
            reference = (await BrowserTestQueries.Find(fixture.Session, session, "Password")).Target;
            var denied = await fixture.Session.FillCredentialAsync(session, reference, (_, _) => ValueTask.FromResult(first + "x"));
            Assert.Equal("user_intervention_required", denied.ErrorCode);
            var observed = await fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(session));
            Assert.DoesNotContain(first, observed.Observation!.Content!);
            Assert.Contains("redacted", observed.Observation.Content!);
            Assert.DoesNotContain("[redacted]x", observed.Observation.Content!);
            reference = (await BrowserTestQueries.Find(fixture.Session, session, "Password")).Target;
            var reused = await fixture.Session.FillCredentialAsync(session, reference, (_, _) => ValueTask.FromResult(first));
            Assert.Null(reused.ErrorCode);
            Assert.Contains("redacted", reused.Observation!.Content!);
        }
        finally { await fixture.Session.ReleaseAsync(session); }
    }

    [Theory]
    [InlineData("/signup")]
    [InlineData("/challenge")]
    [InlineData("/credential-login?mode=new")]
    [InlineData("/credential-login?mode=change")]
    [InlineData("/credential-login?mode=reset")]
    public async Task Registration_and_verification_do_not_resolve_protected_material(string path)
    {
        var session = Guid.NewGuid(); var result = await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(session, new Uri(fixture.Session.Fixture.Origin + path)));
        Assert.NotEqual(BrowserInterventionKind.None, result.Observation!.Intervention);
        var called = false;
        if (path == "/challenge")
        {
            // CAPTCHA pages have no password target; inject one in this test-owned page
            // to exercise the sink's intervention gate rather than an empty enumeration.
            await fixture.Session.ContextFor(session)!.Pages[0].EvaluateAsync(
                "() => document.body.insertAdjacentHTML('beforeend','<label>Password<input type=password autocomplete=current-password></label>')");
        }
        var password = await BrowserTestQueries.Find(fixture.Session, session, "Password");
        var filled = await fixture.Session.FillCredentialAsync(session, password.Target,
            (_, _) => { called = true; return ValueTask.FromResult("private"); });
        Assert.Equal("user_intervention_required", filled.ErrorCode);
        Assert.False(called); await fixture.Session.ReleaseAsync(session);
    }
    [Fact]
    public async Task Authenticated_owner_profile_reopens_isolates_another_owner_and_detached_login_requires_attention()
    {
        var root = Path.Combine(Path.GetTempPath(), "credential-profile-" + Guid.NewGuid().ToString("N"));
        var owner = Guid.NewGuid(); var first = Guid.NewGuid(); var next = Guid.NewGuid();
        var origin = fixture.Session.Fixture.Origin!;
        NativePlaywrightBrowser Browser() => new(new BrowserOptions { Enabled = true, Headless = true,
            ProfileMode = nameof(BrowserProfileMode.PersistentAgent), ProfileRoot = root,
            PolicyMode = nameof(BrowserPolicyMode.Restricted), NavigationOrigins = [origin], InteractionOrigins = [origin], FixtureEnabled = false }, null);
        var browser = Browser(); await browser.StartAsync(default);
        try
        {
            browser.BindSession(first, owner);
            var login = (await browser.ExecuteAsync(BrowserTestRequests.Navigate(first, new(origin + "/credential-login")))).Observation!;
            var password = (await BrowserTestQueries.Find(browser, first, "Password"));
            var filled = await browser.FillCredentialAsync(first, password.Target, (_, _) => ValueTask.FromResult("profile-password-5387"));
            Assert.Null(filled.ErrorCode);
            var signedIn = await browser.ExecuteAsync(BrowserTestRequests.Interaction(first, BrowserOperation.Click, (await BrowserTestQueries.Find(browser, first, "Sign in")).Target, null));
            Assert.Contains("AC-CREDENTIAL-1042", signedIn.Observation!.Content!);
            await browser.ReleaseAsync(first); browser.BindSession(next, owner);
            Assert.Contains("AC-CREDENTIAL-1042", (await browser.ExecuteAsync(BrowserTestRequests.Navigate(next, new(origin + "/credential-login")))).Observation!.Content!);
            await browser.StopAsync(default); browser = Browser(); await browser.StartAsync(default);
            var restarted = Guid.NewGuid(); browser.BindSession(restarted, owner);
            Assert.Contains("AC-CREDENTIAL-1042", (await browser.ExecuteAsync(BrowserTestRequests.Navigate(restarted, new(origin + "/credential-login")))).Observation!.Content!);
            var other = Guid.NewGuid(); var instances = new InMemoryAgentInstanceStore(); var now = DateTimeOffset.UtcNow;
            await instances.InsertAsync(new(other, "secretary", 8, new("Other", "Secretary", "Test", "neutral"), AgentInstanceLifecycle.Active, now, now));
            var executor = new SessionToolExecutor(browser: browser, agentInstances: instances, configurationGate: ToolConfigurationGates.AllowAll);
            var attention = await executor.ExecuteAsync(await Definition(), Guid.NewGuid(), new("login-wall", ToolCatalog.BrowserNavigate, JsonSerializer.Serialize(new { url = origin + "/credential-login" })), ToolLimits.MaxOutputBytes,
                admission: new(true, TriggerKind.ApplicationEvent, AgentInstanceId: other));
            Assert.Contains("user_intervention_required", attention.Text);
            Assert.Equal("authentication", JsonDocument.Parse(attention.Text).RootElement.GetProperty("kind").GetString());
            Assert.DoesNotContain("AC-CREDENTIAL-1042", attention.Text);
            Assert.Equal(2, Directory.GetDirectories(root).Length);
            await browser.ResetPersistentProfileAsync(owner);
            Assert.Single(Directory.GetDirectories(root));
        }
        finally { await browser.StopAsync(default); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static async Task<AgentDefinition> Definition()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory); while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentCore.sln"))) dir = dir.Parent;
        return (await new FileAgentDefinitionStore(Path.Combine(dir!.FullName, "agents"), SyntheticProviderAliases.Default).GetAsync("secretary", 8))!;
    }
}
