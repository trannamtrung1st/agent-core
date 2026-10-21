using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentCore.Api.Tests;

internal static class RetiredOwnerHeader
{
    internal const string Name = "X-AgentCore-Owner-Capability";
    internal const string Token = "9999999999999999999999999999999999999999999999999999999999999999";
    internal static HttpClient CreateClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient(); Apply(client); return client;
    }
    internal static void Apply(HttpClient client) => client.DefaultRequestHeaders.TryAddWithoutValidation(Name, Token);
}
