using Nop.Core.Domain.Orders;
using Nop.Services.Events;

namespace AgentCore.OrderEvents;

public sealed class OrderPlacedConsumer : IConsumer<OrderPlacedEvent>
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task HandleEventAsync(OrderPlacedEvent eventMessage)
    {
        var order = eventMessage.Order;
        if (order is null || order.Id <= 0)
        {
            return;
        }

        try
        {
            var reference = string.IsNullOrWhiteSpace(order.CustomOrderNumber)
                ? order.Id.ToString()
                : order.CustomOrderNumber.Trim();
            string envelope;
            try
            {
                envelope = OrderPlacedEmitter.BuildEnvelope(
                    OrderPlacedEmitter.EventIdForOrder(order.Id),
                    reference,
                    DateTimeOffset.UtcNow);
            }
            catch (ArgumentException)
            {
                envelope = OrderPlacedEmitter.BuildEnvelope(
                    OrderPlacedEmitter.EventIdForOrder(order.Id),
                    order.Id.ToString(),
                    DateTimeOffset.UtcNow);
            }

            await OrderPlacedEmitter.TryDeliverAsync(
                Http,
                Environment.GetEnvironmentVariable("AGENTCORE_WEBHOOK_URL"),
                Environment.GetEnvironmentVariable("AGENTCORE_WEBHOOK_TOKEN"),
                envelope).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Delivery is best-effort. A webhook failure must not fail the store order.
        }
    }
}
