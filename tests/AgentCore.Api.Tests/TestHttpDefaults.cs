using System.Net;
using AgentCore.Infrastructure.Persistence;
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
            // Test hosts must never resolve workspace bytes from the developer data tree.
            var persistence = services.FirstOrDefault(d => d.ServiceType == typeof(PersistenceOptions))?.ImplementationInstance as PersistenceOptions;
            if (persistence?.WorkspaceRoot == "data/workspaces")
                persistence.WorkspaceRoot = Path.Combine(Path.GetTempPath(), "agent-core-http-fixture-" + Guid.NewGuid().ToString("N"), "workspaces");
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
