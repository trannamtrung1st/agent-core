using System.Net;
using System.Net.Http.Json;
using AgentCore.Contracts.Http;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class EmailAccountApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory factory;
    public EmailAccountApiTests(AgentCoreApiFactory factory) => this.factory = factory;
    [Fact]
    public async Task Owner_creates_typed_account_tests_separately_edits_and_rotates_without_readback()
    {
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var profile = new EmailAccountProfileDto("Gmail", "demo@example.test", "demo@example.test", "demo@example.test",
            new("imap.gmail.com", 993, "ImplicitTls"), new("smtp.gmail.com", 465, "ImplicitTls"));
        var create = await owner.PostAsJsonAsync("/api/v2/admin/email-accounts", new AdminCreateEmailAccountRequest("Demo email", profile, "protected-app-password-73821"));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.DoesNotContain("protected-app-password-73821", await create.Content.ReadAsStringAsync());
        var account = (await create.Content.ReadFromJsonAsync<AdminCredentialResponse>())!;
        Assert.Equal("EmailAccount", account.Purpose); Assert.Equal("Password", account.Kind); Assert.Empty(account.AllowedOrigins);
        foreach (var protocol in new[] { "imap", "smtp" })
        {
            var tested = await owner.PostAsJsonAsync($"/api/v2/admin/email-accounts/{account.CredentialId}/test", new AdminTestEmailAccountRequest(1, protocol));
            Assert.Equal(HttpStatusCode.OK, tested.StatusCode); Assert.Contains("synthetic", await tested.Content.ReadAsStringAsync());
        }
        var update = await owner.PatchAsJsonAsync($"/api/v2/admin/email-accounts/{account.CredentialId}", new AdminUpdateEmailAccountRequest(1, "Updated mail", "Active", profile));
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await owner.PostAsJsonAsync($"/api/v2/admin/email-accounts/{account.CredentialId}/test", new AdminTestEmailAccountRequest(1, "imap"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PatchAsJsonAsync($"/api/v2/admin/email-accounts/{account.CredentialId}", new AdminUpdateEmailAccountRequest(1, "Stale", "Active", profile))).StatusCode);
        var rotate = await owner.PutAsJsonAsync($"/api/v2/admin/credentials/{account.CredentialId}/value", new AdminReplaceCredentialRequest(2, "replacement-app-password-34984"));
        Assert.Equal(HttpStatusCode.OK, rotate.StatusCode); Assert.DoesNotContain("replacement-app-password-34984", await rotate.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PatchAsJsonAsync($"/api/v2/admin/credentials/{account.CredentialId}", new AdminUpdateCredentialRequest(3, "Mail", "Active", null, ["https://mail.google.com"]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PostAsJsonAsync("/api/v2/admin/email-accounts", new AdminCreateEmailAccountRequest("Invalid", profile with { SmtpEndpoint = new("smtp.gmail.com", 25, "None") }, "secret"))).StatusCode);
    }
    [Fact]
    public async Task Owner_can_inspect_uncertain_and_immutable_completed_receipts()
    {
        var store = factory.Services.GetRequiredService<IEmailStore>();
        var id = "core-draft-" + Guid.NewGuid().ToString("N"); var account = Guid.NewGuid();
        var draft = new EmailDraftSnapshot(id, ["to@example.test"], [], [], "Safe subject", "Safe body", account, "mail", "from@example.test");
        await store.CreateDraftAsync(new(Guid.NewGuid(), Guid.NewGuid(), draft, DateTimeOffset.UtcNow));
        await store.TryClaimSendAsync(new(id, account, 1, Guid.NewGuid(), "exact-action", null, DateTimeOffset.UtcNow));
        using var owner = TestOwnerCapability.CreateOwnerClient(factory);
        var path = "/api/v2/admin/email-send-operations/" + id;
        Assert.Contains("Indeterminate", await owner.GetStringAsync(path));
        await store.CompleteSendAsync(id, new(EmailSendOutcome.Sent, "provider-evidence", null, null));
        Assert.Contains("provider-evidence", await owner.GetStringAsync(path));
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
    }
    [Fact]
    public async Task Account_and_selection_routes_require_owner_capability()
    {
        using var client = factory.CreateClient();
        foreach (var path in new[] { "/api/v2/admin/email-accounts", $"/api/v2/admin/email-accounts/{Guid.NewGuid()}/test", $"/api/v2/sessions/{Guid.NewGuid()}/email-account-request/decision" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(path, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/api/v2/sessions/{Guid.NewGuid()}/email-account-request")).StatusCode);
    }
}
