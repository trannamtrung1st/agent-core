using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nop.Core.Domain.Orders;
using Nop.Core.Infrastructure;
using Nop.Services.Events;

namespace AgentCore.OrderEvents;

public sealed class NopStartup : INopStartup
{
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddScoped<IConsumer<OrderPlacedEvent>, OrderPlacedConsumer>();

    public void Configure(IApplicationBuilder application)
    {
    }

    public int Order => 3000;
}
