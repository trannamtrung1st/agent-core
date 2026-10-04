using System.Net;
using System.Text.Json;
using Xunit;

namespace AgentCore.OrderEvents.Tests;

public sealed class OrderPlacedEmitterTests
{
    [Fact]
    public void One_order_builds_one_stable_order_placed_envelope()
    {
        var first = OrderPlacedEmitter.BuildEnvelope(
            OrderPlacedEmitter.EventIdForOrder(105),
            "105",
            new DateTimeOffset(2026, 10, 4, 1, 2, 3, TimeSpan.Zero));
        var retry = OrderPlacedEmitter.BuildEnvelope(
            OrderPlacedEmitter.EventIdForOrder(105),
            "105",
            new DateTimeOffset(2026, 10, 4, 1, 2, 3, TimeSpan.Zero));
        Assert.Equal(first, retry);
        using var document = JsonDocument.Parse(first);
        Assert.Equal("order-105-placed", document.RootElement.GetProperty("eventId").GetString());
        Assert.Equal("order.placed", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("105", document.RootElement.GetProperty("data").GetProperty("orderReference").GetString());
        Assert.False(document.RootElement.TryGetProperty("instructions", out _));
    }

    [Fact]
    public async Task Invalid_credentials_are_not_retried()
    {
        var calls = 0;
        var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }));
        var delivered = await OrderPlacedEmitter.TryDeliverAsync(
            http,
            "http://127.0.0.1:5080/api/v1/hooks/11111111-1111-1111-1111-111111111111",
            "token",
            OrderPlacedEmitter.BuildEnvelope(OrderPlacedEmitter.EventIdForOrder(105), "105", DateTimeOffset.UnixEpoch));
        Assert.False(delivered);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_network_failure_on_the_last_attempt_returns_false()
    {
        var calls = 0;
        var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            throw new HttpRequestException("down");
        }));
        var delivered = await OrderPlacedEmitter.TryDeliverAsync(
            http,
            "http://127.0.0.1:5080/api/v1/hooks/11111111-1111-1111-1111-111111111111",
            "token",
            OrderPlacedEmitter.BuildEnvelope(OrderPlacedEmitter.EventIdForOrder(105), "105", DateTimeOffset.UnixEpoch));
        Assert.False(delivered);
        Assert.Equal(OrderPlacedEmitter.MaxAttempts, calls);
    }

    [Fact]
    public async Task Missing_configuration_sends_nothing()
    {
        var calls = 0;
        var http = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }));
        var delivered = await OrderPlacedEmitter.TryDeliverAsync(
            http,
            null,
            null,
            OrderPlacedEmitter.BuildEnvelope(OrderPlacedEmitter.EventIdForOrder(1), "1", DateTimeOffset.UnixEpoch));
        Assert.False(delivered);
        Assert.Equal(0, calls);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
