using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

internal static class TestOwnerCapability
{
    private static readonly ConditionalWeakTable<IServiceProvider, TokenBox> Tokens = [];

    public static string Token(IServiceProvider services) =>
        Tokens.GetValue(
                services,
                provider => new TokenBox(
                    provider.GetRequiredService<IOwnerCapabilityService>()
                        .IssueAsync()
                        .AsTask()
                        .GetAwaiter()
                        .GetResult()
                        .Token))
            .Value;

    public static HttpClient CreateOwnerClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        Apply(client, factory.Services);
        return client;
    }

    public static void Apply(HttpConnectionOptions options, IServiceProvider services) =>
        options.Headers[OwnerCapabilityHeaders.Name] = Token(services);

    public static void Apply(HttpClient client, IServiceProvider services) =>
        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, Token(services));

    private sealed record TokenBox(string Value);
}
