using System.Net;
using System.Net.Http.Json;
using System.Text;
using AgentCore.Api.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed partial class TenantDefinitionAccessTests
{
    [Fact]
    public async Task Real_kestrel_rejects_declared_and_chunked_oversized_resource_bodies_without_mutation()
    {
        await using var factory = new NativeResourceApiFactory(); factory.UseKestrel(0);
        using var client = factory.CreateClient();
        await Login(client, "owner@example.test", "explicit-synthetic-test-password");
        var tenant = (await Json(await client.GetAsync("/api/v2/account/tenants"))).GetProperty("items")[0].GetProperty("tenantId").GetGuid();
        var draft = await Json(await client.PostAsJsonAsync($"/api/v2/tenants/{tenant}/agent-definitions", new { name = "Transport limit", expectedRevision = await Revision(client, tenant) }));
        var path = $"/api/v2/tenants/{tenant}/definition-drafts/{draft.GetProperty("draftId").GetGuid()}";
        var payload = new byte[25 * 1024 * 1024]; Array.Fill(payload, (byte)' ');
        Encoding.UTF8.GetBytes("{\"expectedRevision\":2,\"expectedDraftRevision\":1,\"items\":[]}").CopyTo(payload, 0);
        using var declared = new ByteArrayContent(payload); declared.Headers.ContentType = new("application/json");
        using var declaredRequest = new HttpRequestMessage(HttpMethod.Post, path + "/resources/batch") { Content = declared };
        declaredRequest.Headers.ExpectContinue = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(declaredRequest)).StatusCode);
        using var body = new StreamContent(new NonSeekableContent(new MemoryStream(payload))); body.Headers.ContentType = new("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, path + "/resources/batch") { Content = body };
        request.Headers.TransferEncodingChunked = true;
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.SendAsync(request)).StatusCode);
        Assert.Empty((await Json(await client.GetAsync(path + "/resources"))).GetProperty("items").EnumerateArray());
    }
    private sealed class NativeResourceApiFactory : AgentCoreApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                // These options were bound before WebApplicationFactory's test configuration.
                // This disposable native loopback host explicitly opts into supported local HTTP.
                ((IdentityHttpOptions)services.Single(d => d.ServiceType == typeof(IdentityHttpOptions)).ImplementationInstance!).AllowInsecureLocalHttp = true;
                services.Configure<CookieAuthenticationOptions>(IdentityHttp.Scheme, options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
                services.Configure<AntiforgeryOptions>(options => options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest);
            });
        }
    }
    private sealed class NonSeekableContent(Stream inner) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
