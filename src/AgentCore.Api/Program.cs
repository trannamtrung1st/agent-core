using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Infrastructure;
using Microsoft.AspNetCore.Http.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});

var agentDirectory = ResolveAgentDirectory(builder);
builder.Services.AddAgentCoreInfrastructure(agentDirectory);
builder.Services.AddSingleton<OutboundHttpProbe>();
builder.Services.AddTransient<ForbiddenOutboundHandler>();
builder.Services.ConfigureHttpClientDefaults(client =>
    client.AddHttpMessageHandler<ForbiddenOutboundHandler>());
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", (IConfiguration configuration) =>
        Results.Json(new HealthResponse(
            "healthy",
            configuration["AgentCore:Profile"] ?? "Synthetic",
            HttpMapping.ProtocolVersion)))
    .WithName("Health");

app.MapGet("/api/v1/agents", async (SessionManager sessions, CancellationToken cancellationToken) =>
{
    var agents = await sessions.ListAgentsAsync(cancellationToken).ConfigureAwait(false);
    return Results.Json(new AgentListResponse(agents.Select(HttpMapping.ToAgent).ToArray()));
});

app.MapGet("/api/v1/agents/{agentId}", async (string agentId, int? version, SessionManager sessions, CancellationToken cancellationToken) =>
{
    try
    {
        var agent = await sessions.GetAgentAsync(agentId, version, cancellationToken).ConfigureAwait(false);
        return Results.Json(HttpMapping.ToAgent(agent));
    }
    catch (AgentCoreException ex)
    {
        return ProblemResults.From(ex);
    }
});

app.MapPost("/api/v1/sessions", async (CreateSessionRequest? body, SessionManager sessions, HttpContext http, CancellationToken cancellationToken) =>
{
    try
    {
        if (body is null || string.IsNullOrWhiteSpace(body.AgentId))
        {
            throw AgentCoreErrors.Validation("agentId is required.");
        }

        var snapshot = await sessions.CreateAsync(
                body.AgentId,
                body.AgentVersion,
                HttpMapping.ParseMode(body.Mode),
                cancellationToken)
            .ConfigureAwait(false);
        var view = HttpMapping.ToView(snapshot, activeResponseId: null);
        var location = $"/api/v1/sessions/{view.SessionId}";
        http.Response.Headers.Location = location;
        return Results.Json(view, statusCode: StatusCodes.Status201Created);
    }
    catch (AgentCoreException ex)
    {
        return ProblemResults.From(ex);
    }
});

app.MapGet("/api/v1/sessions/{sessionId:guid}", async (Guid sessionId, SessionManager sessions, CancellationToken cancellationToken) =>
{
    try
    {
        var snapshot = await sessions.GetAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Results.Json(HttpMapping.ToView(snapshot, activeResponseId: null));
    }
    catch (AgentCoreException ex)
    {
        return ProblemResults.From(ex);
    }
});

app.MapGet("/api/v1/sessions/{sessionId:guid}/messages", async (
    Guid sessionId,
    SessionManager sessions,
    long after,
    int? limit,
    CancellationToken cancellationToken) =>
{
    try
    {
        var pageLimit = limit ?? 50;
        var items = await sessions.ReadHistoryAsync(sessionId, after, pageLimit, cancellationToken)
            .ConfigureAwait(false);
        var next = items.Count == 0 ? after : items[^1].Sequence;
        return Results.Json(new HistoryPageResponse(
            items.Select(HttpMapping.ToHistoryItem).ToArray(),
            next,
            HasMore: items.Count == pageLimit));
    }
    catch (AgentCoreException ex)
    {
        return ProblemResults.From(ex);
    }
});

app.MapDelete("/api/v1/sessions/{sessionId:guid}", async (Guid sessionId, SessionManager sessions, CancellationToken cancellationToken) =>
{
    try
    {
        await sessions.EndAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }
    catch (AgentCoreException ex)
    {
        return ProblemResults.From(ex);
    }
});

app.Run();

static string ResolveAgentDirectory(WebApplicationBuilder builder)
{
    var configured = builder.Configuration["AgentCore:AgentDirectory"] ?? "agents";
    if (Path.IsPathRooted(configured) && Directory.Exists(configured))
    {
        return configured;
    }

    var candidates = new[]
    {
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, configured)),
        Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, "..", "..", configured)),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured)),
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", configured))
    };

    return candidates.FirstOrDefault(Directory.Exists)
           ?? throw new InvalidOperationException("Agent definition directory was not found.");
}

public partial class Program;
