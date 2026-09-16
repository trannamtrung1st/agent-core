using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentCore.Api.Tests;

internal static class TestHttpDefaults
{
    public static void UseLoopbackCaller(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, LoopbackCallerStartupFilter>();
        });
    }

    private sealed class LoopbackCallerStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress ??= IPAddress.Loopback;
                    await nextMiddleware().ConfigureAwait(false);
                });
                next(app);
            };
    }
}
