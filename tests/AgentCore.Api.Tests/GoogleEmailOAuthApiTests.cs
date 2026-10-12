using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.WebUtilities;

namespace AgentCore.Api.Tests;

public sealed class GoogleEmailOAuthApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory factory;
    public GoogleEmailOAuthApiTests(AgentCoreApiFactory factory) => this.factory = factory;
    [Fact]
    public async Task Owner_start_cookie_callback_status_replay_disconnect_and_same_account_reconnect()
    {
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await owner.PostAsJsonAsync("/api/v2/admin/email-accounts", new AdminCreateEmailAccountRequest("OAuth Synthetic", new("Gmail")));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var c = (await created.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        Assert.Equal("Token", c.Kind); Assert.Null(c.EmailAccount!.ImapEndpoint); Assert.Equal("NotConnected", c.EmailAccount.ConnectionStatus);
        var path = $"/api/v2/admin/email-accounts/{c.CredentialId}";
        using var anonymous = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(path + "/oauth/start", new AdminEmailOAuthRequest(1))).StatusCode);
        var started = await owner.PostAsJsonAsync(path + "/oauth/start", new AdminEmailOAuthRequest(1));
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        var cookie = Assert.Single(started.Headers.GetValues("Set-Cookie")).Split(';')[0];
        Assert.Contains("httponly", string.Join(' ', started.Headers.GetValues("Set-Cookie")), StringComparison.OrdinalIgnoreCase);
        using var start = JsonDocument.Parse(await started.Content.ReadAsStringAsync());
        var url = start.RootElement.GetProperty("authorizationUrl").GetString()!;
        var state = QueryHelpers.ParseQuery(url[url.IndexOf('?')..])["state"].ToString();
        var callback = "/api/v2/integrations/google-email/oauth/callback?state=" + Uri.EscapeDataString(state) + "&code=synthetic-consent";
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync(callback)).StatusCode);
        anonymous.DefaultRequestHeaders.Add("Cookie", cookie);
        var finished = await anonymous.GetAsync(callback);
        Assert.Equal(HttpStatusCode.SeeOther, finished.StatusCode);
        Assert.Equal("no-store", finished.Headers.CacheControl!.ToString());
        Assert.Contains("/admin/connections/credentials?emailAccount=", finished.Headers.Location!.ToString());
        Assert.DoesNotContain(state, finished.Headers.Location.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await anonymous.GetAsync(callback)).StatusCode);
        c = (await owner.GetFromJsonAsync<AdminCredentialResponse>("/api/v2/admin/credentials/" + c.CredentialId))!;
        Assert.True(c.EmailAccount!.Synthetic); Assert.Equal("Connected", c.EmailAccount.ConnectionStatus); Assert.EndsWith("@example.test", c.EmailAccount.EmailAddress);
        var checkedAccess = await owner.PostAsJsonAsync(path + "/connection/check", new AdminEmailOAuthRequest(c.Revision)); Assert.Equal(HttpStatusCode.OK, checkedAccess.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/v2/admin/credentials/{c.CredentialId}/value", new AdminReplaceCredentialRequest(c.Revision, "injected-secret"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync(path + "/oauth/disconnect", new AdminEmailOAuthRequest(c.Revision))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.PostAsJsonAsync(path + "/oauth/disconnect", new AdminEmailOAuthRequest(c.Revision, true))).StatusCode);
        Assert.Contains("Disconnected", await owner.GetStringAsync(path + "/connection"));
        var retry = await owner.PostAsJsonAsync(path + "/oauth/start", new AdminEmailOAuthRequest(c.Revision + 1)); Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.DoesNotContain("injected-secret", await owner.GetStringAsync("/api/v2/admin/credentials"));
    }
}
