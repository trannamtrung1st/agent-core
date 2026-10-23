using AgentCore.Tests.Shared;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

[Collection(BrowserChromiumCollection.Name)]
public sealed class BrowserCredentialSinkTests(BrowserHostFixture fixture) : IClassFixture<BrowserHostFixture>
{

    [Fact]
    public async Task Native_password_sink_rejects_wrong_or_stale_targets_masks_child_reflection_and_preserves_login()
    {
        var session = Guid.NewGuid(); const string password = "native-private-password-9374";
        try
        {
            Assert.Null((await fixture.Session.ExecuteAsync(BrowserTestRequests.Navigate(session, new(fixture.Session.Fixture.Origin + "/credential-login?reflect=1")))).ErrorCode);
            var passwordTarget = (await BrowserTestQueries.Find(fixture.Session, session, "Password")).Target;
            var usernameTarget = (await BrowserTestQueries.Find(fixture.Session, session, "Username")).Target;
            var resolutions = 0;
            ValueTask<string> Resolve(string origin, CancellationToken ct) { resolutions++; return ValueTask.FromResult(password); }
            Assert.Equal("credential_target_invalid", (await fixture.Session.FillCredentialAsync(session, usernameTarget, Resolve)).ErrorCode);
            Assert.Equal(0, resolutions);
            var filled = await fixture.Session.FillCredentialAsync(session, passwordTarget, Resolve);
            Assert.Null(filled.ErrorCode); Assert.Equal(1, resolutions); Assert.DoesNotContain(password, filled.Observation!.Content!);
            var observed = await fixture.Session.ExecuteAsync(BrowserTestRequests.Inspect(session));
            Assert.DoesNotContain(password, observed.Observation!.Content!);
            var screenshot = await fixture.Session.ExecuteAsync(BrowserTestRequests.Screenshot(session));
            Assert.Null(screenshot.ErrorCode); Assert.NotEmpty(screenshot.Bytes!); Assert.True(screenshot.RedactionCount >= 2);
            var signedIn = await fixture.Session.ExecuteAsync(BrowserTestRequests.Interaction(session, BrowserOperation.Click,
                (await BrowserTestQueries.Find(fixture.Session, session, "Sign in")).Target, null));
            Assert.Null(signedIn.ErrorCode); Assert.Contains("AC-CREDENTIAL-1042", signedIn.Observation!.Content!);
            Assert.DoesNotContain(password, signedIn.Observation.Content!);
            Assert.Equal("target_missing", (await fixture.Session.FillCredentialAsync(session, passwordTarget, Resolve)).ErrorCode);
            Assert.Equal(1, resolutions);
            // The injected callback qualifies native sink behavior only; tenant authority is exercised through the HTTP/model tests.
        }
        finally { await fixture.Session.ReleaseAsync(session); }
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
    public async Task Native_profile_reopens_isolates_another_owner_and_reset_deletes_only_target()
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
            var other = Guid.NewGuid(); var otherSession = Guid.NewGuid(); browser.BindSession(otherSession, other);
            var isolated = await browser.ExecuteAsync(BrowserTestRequests.Navigate(otherSession, new(origin + "/credential-login")));
            Assert.Null(isolated.ErrorCode); Assert.DoesNotContain("AC-CREDENTIAL-1042", isolated.Observation!.Content!);
            Assert.Contains("Password", isolated.Observation.Content!);
            Assert.Equal(2, Directory.GetDirectories(root).Length);
            await browser.ResetPersistentProfileAsync(owner);
            Assert.Single(Directory.GetDirectories(root));
        }
        finally { await browser.StopAsync(default); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
