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
            await instances.InsertAsync(new(owner, "secretary", 3, new("Test", "Secretary", "Test", "neutral"), AgentInstanceLifecycle.Active, now, now));
            var store = new InMemoryCredentialStore(instances);
            var service = new CredentialService(store, store, new LocalCredentialProtector(root), instances, new SystemIdGenerator(TimeProvider.System), TimeProvider.System);
            var c = await service.CreateAsync("Store", "Password", new Dictionary<string,string> { ["username"] = "owner@example.test" }, [origin], password);
            var binding = await service.BindAsync(owner, c.CredentialId, "store-admin", 1);
            var executor = new SessionToolExecutor(browser: fixture.Session, agentInstances: instances, credentials: service, configurationGate: ToolConfigurationGates.AllowAll);
            var definition = await Definition(); var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: owner, SupportsVision: true);
            async Task<string> Execute(string tool, string args, ToolExecutionAdmission? a = null) => (await executor.ExecuteAsync(definition, session, new("call-" + Guid.NewGuid(), tool, args), ToolLimits.MaxOutputBytes, admission: a ?? admission)).Text;
            var nav = await Execute(ToolCatalog.BrowserNavigate, JsonSerializer.Serialize(new { url = origin + "/credential-login?reflect=1" }));
            Assert.DoesNotContain("error", nav);
            using var doc = JsonDocument.Parse(nav); var passwordRef = doc.RootElement.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("actions").EnumerateArray().Any(a => a.GetString() == "fill_credential")).GetProperty("ref").GetString();
            var usernameRef = doc.RootElement.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Username").GetProperty("ref").GetString();
            var resolvedNonPassword = false;
            var wrongField = await fixture.Session.FillCredentialAsync(session, usernameRef!, (_, _) => { resolvedNonPassword = true; return ValueTask.FromResult(password); });
            Assert.Equal("unsupported_operation", wrongField.ErrorCode);
            Assert.False(resolvedNonPassword);
            var raw = await Execute(ToolCatalog.BrowserType, JsonSerializer.Serialize(new { @ref = passwordRef, text = "model-supplied" }));
            Assert.Contains("unsupported_operation", raw);
            var detached = await Execute(ToolCatalog.BrowserFillCredential, JsonSerializer.Serialize(new { @ref = passwordRef, credentialRef = "store-admin" }), admission with { Detached = true, TriggerKind = TriggerKind.ScheduledOccurrence });
            Assert.Contains("forbidden", detached);
            var unknown = await Execute(ToolCatalog.BrowserFillCredential, JsonSerializer.Serialize(new { @ref = passwordRef, credentialRef = "other-agent" }));
            Assert.Contains("Forbidden", unknown);
            var filled = await Execute(ToolCatalog.BrowserFillCredential, JsonSerializer.Serialize(new { @ref = passwordRef, credentialRef = "store-admin" }));
            Assert.DoesNotContain("error", filled); Assert.DoesNotContain(password, filled); Assert.Contains("redacted", filled);
            var observed = await Execute(ToolCatalog.BrowserSnapshot, "{}"); Assert.DoesNotContain(password, observed);
            var screenshot = await fixture.Session.CaptureViewportAsync(new(session));
            Assert.Null(screenshot.ErrorCode);
            Assert.NotEmpty(screenshot.Png!);
            // The reflected text has a child element; text-node masking must still cover it.
            Assert.True(screenshot.RedactionCount >= 2);
            using var filledDoc = JsonDocument.Parse(observed); var submit = filledDoc.RootElement.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "Sign in").GetProperty("ref").GetString();
            var login = await Execute(ToolCatalog.BrowserClick, JsonSerializer.Serialize(new { @ref = submit }));
            Assert.Contains("AC-CREDENTIAL-1042", login); Assert.DoesNotContain(password, login);
            var resolvedStale = false;
            var stale = await fixture.Session.FillCredentialAsync(session, passwordRef!, (_, _) => { resolvedStale = true; return ValueTask.FromResult(password); });
            Assert.Equal("stale_reference", stale.ErrorCode);
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
            var navigated = await fixture.Session.NavigateAsync(new(session, new(fixture.Session.Fixture.Origin + "/credential-login?reflect=1")));
            var reference = navigated.Observation!.Elements.Single(e => e.Actions.Contains("fill_credential")).Ref;
            var first = new string('&', 60000);
            var filled = await fixture.Session.FillCredentialAsync(session, reference, (_, _) => ValueTask.FromResult(first));
            Assert.Null(filled.ErrorCode); Assert.DoesNotContain(first, filled.Observation!.VisibleText);
            reference = filled.Observation.Elements.Single(e => e.Actions.Contains("fill_credential")).Ref;
            var denied = await fixture.Session.FillCredentialAsync(session, reference, (_, _) => ValueTask.FromResult(first + "x"));
            Assert.Equal("user_intervention_required", denied.ErrorCode);
            var observed = await fixture.Session.SnapshotAsync(session);
            Assert.DoesNotContain(first, observed.Observation!.VisibleText);
            Assert.Contains("redacted", observed.Observation.VisibleText);
            Assert.DoesNotContain("[redacted]x", observed.Observation.VisibleText);
            reference = observed.Observation.Elements.Single(e => e.Actions.Contains("fill_credential")).Ref;
            var reused = await fixture.Session.FillCredentialAsync(session, reference, (_, _) => ValueTask.FromResult(first));
            Assert.Null(reused.ErrorCode);
            Assert.Contains("redacted", reused.Observation!.VisibleText);
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
        var session = Guid.NewGuid(); var result = await fixture.Session.NavigateAsync(new(session, new Uri(fixture.Session.Fixture.Origin + path)));
        Assert.NotEqual(BrowserInterventionKind.None, result.Observation!.Intervention);
        var called = false;
        foreach (var e in result.Observation.Elements.Where(e => e.Actions.Contains("fill_credential")))
        {
            var filled = await fixture.Session.FillCredentialAsync(session, e.Ref, (_, _) => { called = true; return ValueTask.FromResult("private"); });
            Assert.Equal("user_intervention_required", filled.ErrorCode);
        }
        Assert.False(called); await fixture.Session.ReleaseAsync(session);
    }
    [Fact]
    public async Task Authenticated_owner_profile_reopens_isolates_another_owner_and_detached_login_requires_attention()
    {
        var root = Path.Combine(Path.GetTempPath(), "credential-profile-" + Guid.NewGuid().ToString("N"));
        var owner = Guid.NewGuid(); var first = Guid.NewGuid(); var next = Guid.NewGuid();
        var origin = fixture.Session.Fixture.Origin!;
        PlaywrightBrowser Browser() => new(new BrowserOptions { Enabled = true, Headless = true,
            ProfileMode = nameof(BrowserProfileMode.PersistentAgent), ProfileRoot = root,
            PolicyMode = nameof(BrowserPolicyMode.Restricted), NavigationOrigins = [origin], InteractionOrigins = [origin], FixtureEnabled = false }, null);
        var browser = Browser(); await browser.StartAsync(default);
        try
        {
            browser.BindSession(first, owner);
            var login = (await browser.NavigateAsync(new(first, new(origin + "/credential-login")))).Observation!;
            var password = login.Elements.Single(e => e.Actions.Contains("fill_credential"));
            var filled = await browser.FillCredentialAsync(first, password.Ref, (_, _) => ValueTask.FromResult("profile-password-5387"));
            Assert.Null(filled.ErrorCode);
            var signedIn = await browser.InteractAsync(new(first, "click", filled.Observation!.Elements.Single(e => e.Name == "Sign in").Ref, null));
            Assert.Contains("AC-CREDENTIAL-1042", signedIn.Observation!.VisibleText);
            await browser.ReleaseAsync(first); browser.BindSession(next, owner);
            Assert.Contains("AC-CREDENTIAL-1042", (await browser.NavigateAsync(new(next, new(origin + "/credential-login")))).Observation!.VisibleText);
            await browser.StopAsync(default); browser = Browser(); await browser.StartAsync(default);
            var restarted = Guid.NewGuid(); browser.BindSession(restarted, owner);
            Assert.Contains("AC-CREDENTIAL-1042", (await browser.NavigateAsync(new(restarted, new(origin + "/credential-login")))).Observation!.VisibleText);
            var other = Guid.NewGuid(); var instances = new InMemoryAgentInstanceStore(); var now = DateTimeOffset.UtcNow;
            await instances.InsertAsync(new(other, "secretary", 3, new("Other", "Secretary", "Test", "neutral"), AgentInstanceLifecycle.Active, now, now));
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
        return (await new FileAgentDefinitionStore(Path.Combine(dir!.FullName, "agents"), SyntheticProviderAliases.Default).GetAsync("secretary", 3))!;
    }
}
