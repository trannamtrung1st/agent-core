using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Credentials;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Browser;
using AgentCore.Infrastructure.Credentials;
using AgentCore.Infrastructure.Providers;
using AgentCore.Infrastructure.Providers.OpenAICompatible;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit.Abstractions;

namespace AgentCore.Api.Tests;

public sealed class LiveSystemCredentialFactAttribute : FactAttribute
{
    public LiveSystemCredentialFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_SYSTEM_CREDENTIALS") != "1")
            Skip = "Explicit AGENTCORE_LIVE_SYSTEM_CREDENTIALS=1 required; default gates remain key-free.";
        else if (string.IsNullOrWhiteSpace(LiveIdentityMaintenanceTests.Key())) Skip = "OpenRouter key is missing.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_CREDENTIAL_DEMO_PASSWORD"))
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTCORE_CREDENTIAL_DEMO_USERNAME")))
            Skip = "Explicit disposable demo credentials are required; no bootstrap secrets are imported.";
    }
}

public sealed class NopCommerceCredentialJourneyTests(ITestOutputHelper output)
{
    [LiveSystemCredentialFact(Timeout = 600000)]
    public async Task Prepared_secretary_signs_in_with_bound_alias_and_reuses_profile_after_host_restart()
    {
        var origin = Environment.GetEnvironmentVariable("AGENTCORE_NOPCOMMERCE_ORIGIN") ?? "http://127.0.0.1:5088";
        var username = Environment.GetEnvironmentVariable("AGENTCORE_CREDENTIAL_DEMO_USERNAME")!;
        var password = Environment.GetEnvironmentVariable("AGENTCORE_CREDENTIAL_DEMO_PASSWORD")!;
        var root = Path.Combine(Path.GetTempPath(), "credential-nopcommerce-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var options = new LanguageModelProviderOptions { Adapter = "OpenAICompatible", BaseUrl = "https://openrouter.ai/api/v1/",
            ApiKey = LiveIdentityMaintenanceTests.Key(), DefaultModel = "deepseek/deepseek-v4.1-flash", Tools = true, StructuredOutput = false };
        using var http = new HttpClient();
        var model = new ObservedModel(new SemanticResponseLanguageModel(new OpenAICompatibleLanguageModel(http, options)));
        ExperienceHost Host() => new(Path.Combine(root, "app.db"), model, configure: services => {
            services.RemoveAll<IModelCatalog>(); services.AddSingleton<IModelCatalog>(ModelCatalogFactory.Create("Real", options, null));
            services.RemoveAll<ILanguageModelResolver>(); services.AddSingleton<ILanguageModelResolver>(new Resolver(model));
            services.RemoveAll<BrowserOptions>(); services.AddSingleton(new BrowserOptions { Enabled = true, Headless = true,
                ProfileMode = nameof(BrowserProfileMode.PersistentAgent), ProfileRoot = Path.Combine(root, "profiles"),
                PolicyMode = nameof(BrowserPolicyMode.Restricted), NavigationOrigins = [origin], InteractionOrigins = [origin], FixtureEnabled = false });
            services.RemoveAll<ICredentialProtector>(); services.AddSingleton<ICredentialProtector>(new LocalCredentialProtector(Path.Combine(root, "keys")));
        }, configuration: c => c.AddInMemoryCollection(new Dictionary<string, string?> {
            ["AgentCore:Profile"] = "Real", ["Providers:LanguageModels:primary-llm:Adapter"] = options.Adapter,
            ["Providers:LanguageModels:primary-llm:BaseUrl"] = options.BaseUrl, ["Providers:LanguageModels:primary-llm:ApiKey"] = options.ApiKey,
            ["Providers:LanguageModels:primary-llm:DefaultModel"] = options.DefaultModel,
            ["Providers:LanguageModels:primary-llm:Tools"] = "true", ["Providers:LanguageModels:primary-llm:StructuredOutput"] = "false" }));
        Guid owner, other, credentialId;
        async Task<Guid> Chat(IServiceProvider services, Guid id, string prompt)
        {
            var snapshot = await services.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
            await using var runtime = services.GetRequiredService<SessionRuntimeFactory>().Create(snapshot, new CapturingSessionOutput());
            Assert.True(await runtime.AttachAsync());
            Assert.True(await runtime.SubmitUserTextAsync(prompt));
            await runtime.WaitUntilIdleAsync(new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token);
            Assert.Contains(runtime.Snapshot.Entries, e => e.Role == ConversationRole.Assistant && !string.IsNullOrWhiteSpace(e.Text));
            var observed = await services.GetRequiredService<IBrowser>().SnapshotAsync(snapshot.SessionId);
            Assert.Null(observed.ErrorCode);
            Assert.Contains("/Admin", observed.Observation!.Url, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("/login", observed.Observation.Url, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(BrowserInterventionKind.None, observed.Observation.Intervention);
            Assert.True(await runtime.RequestEndAsync());
            output.WriteLine("Authenticated store observed; Chat ended with persistent owner profile.");
            return snapshot.SessionId;
        }
        try
        {
            await using (var host = Host())
            {
                _ = TestOwnerCapability.CreateOwnerClient(host);
                var services = host.Services;
                var instances = services.GetRequiredService<AdminAgentInstanceService>();
                owner = (await instances.CreateManagedAsync("secretary", 5)).InstanceId;
                other = (await instances.CreateManagedAsync("secretary", 5)).InstanceId;
                var credentials = services.GetRequiredService<CredentialService>();
                var credential = await credentials.CreateAsync("nopCommerce proof", "Password", new Dictionary<string,string> { ["username"] = username, ["application"] = "nopCommerce" }, [origin], password);
                credentialId = credential.CredentialId;
                await credentials.BindAsync(owner, credentialId, "store-admin", 1);
                await credentials.BindAsync(other, credentialId, "ecommerce-admin", 1);
                await Chat(services, owner, $"Sign in to the nopCommerce store at {origin}/Admin using your store signin skill and bound credentials. Keep this login for later Chats and host restart using the site offered Remember me control, verify it is checked before submitting, read the authenticated admin dashboard, then stop. Do not change store data. The owner-supplied origin here overrides the example location in the skill.");
                Assert.Contains(model.Calls, c => c.Name == ToolCatalog.CredentialsList);
                Assert.Contains(model.Calls, c => IsCredentialFill(c));
                Assert.Contains(model.Requests.SelectMany(r => r.Messages), m => m.Text.Contains("store.signin", StringComparison.Ordinal));
                var before = model.Calls.Count;
                await Chat(services, owner, $"Read the nopCommerce product list at {origin}/Admin/Product/List using your saved browser sign-in. Report an observed product name and stop; do not change anything.");
                Assert.DoesNotContain(model.Calls.Skip(before), IsCredentialFill);
            }
            await using (var restarted = Host())
            {
                _ = TestOwnerCapability.CreateOwnerClient(restarted);
                var services = restarted.Services;
                var before = model.Calls.Count;
                await Chat(services, owner, $"Read the nopCommerce product list at {origin}/Admin/Product/List using the saved browser profile. Report an observed product name and stop; do not change anything.");
                Assert.DoesNotContain(model.Calls.Skip(before), IsCredentialFill);
                var credentials = services.GetRequiredService<CredentialService>();
                // One replacement preserves both grants. The same value is intentional: the external account is unchanged.
                var c = await credentials.GetAsync(credentialId);
                c = await credentials.ReplaceAsync(c.CredentialId, c.Revision, password);
                Assert.Equal(2, c.BindingCount);
                Assert.True(await credentials.ResolvePasswordAsync(owner, "store-admin", origin) == password);
                Assert.True(await credentials.ResolvePasswordAsync(other, "ecommerce-admin", origin) == password);
                await Chat(services, other, $"Sign in to the nopCommerce store at {origin}/Admin. Read credentials.list and use your own bound alias ecommerce-admin (the store-admin alias is another owner's grant). Fill the existing account username from safe metadata and password with fill_credential. Read the dashboard and stop; do not change store data.");
                var browser = services.GetRequiredService<IBrowser>();
                var tools = services.GetRequiredService<SessionToolExecutor>();
                var definition = (await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("secretary", 5))!;
                var detached = new ToolExecutionAdmission(true, TriggerKind.ApplicationEvent, AgentInstanceId: owner);
                var detachedSession = Guid.NewGuid();
                var args = JsonSerializer.Serialize(new { url = origin + "/Admin/Product/List" });
                var review = await tools.ExecuteAsync(definition, detachedSession, new("review", ToolCatalog.BrowserNavigate, args), ToolLimits.MaxOutputBytes, admission: detached);
                Assert.DoesNotContain("user_intervention_required", review.Text);
                Assert.Contains("/Admin/Product/List", review.Text);
                await services.GetRequiredService<IBrowserLease>().ReleaseAsync(detachedSession);
                await browser.ResetPersistentProfileAsync(owner);
                var wall = await tools.ExecuteAsync(definition, Guid.NewGuid(), new("expired-login", ToolCatalog.BrowserNavigate, args), ToolLimits.MaxOutputBytes, admission: detached);
                Assert.Contains("user_intervention_required", wall.Text);
                Assert.Equal("authentication", JsonDocument.Parse(wall.Text).RootElement.GetProperty("kind").GetString());
                output.WriteLine("Restart, two aliases, single replacement, detached authenticated read, and reset login-wall intervention passed.");
            }
            Assert.False(JsonSerializer.Serialize(model.Requests).Contains(password, StringComparison.Ordinal), "Protected material entered model requests.");
            Assert.False(JsonSerializer.Serialize(model.Calls).Contains(password, StringComparison.Ordinal), "Protected material entered tool arguments.");
            foreach (var file in Directory.GetFiles(root, "*.db*", SearchOption.TopDirectoryOnly))
                Assert.False(System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file)).Contains(password, StringComparison.Ordinal), "SQLite contained plaintext protected material.");
            output.WriteLine("Real model issued {0} calls; model requests, tool arguments and SQLite contained no protected value.", model.Calls.Count);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static bool IsCredentialFill(ModelToolCall call) => call.Name == ToolCatalog.BrowserClick && call.ArgumentsJson.Contains("fill_credential", StringComparison.Ordinal);
    private sealed class Resolver(ILanguageModel model) : ILanguageModelResolver
    { public ILanguageModel Resolve(SessionModelSelection selection, ModelPurpose purpose) => model; }
    private sealed class ObservedModel(ILanguageModel inner) : ILanguageModel
    {
        public ModelCapabilities Capabilities => inner.Capabilities;
        public List<ModelToolCall> Calls { get; } = [];
        public List<ModelRequest> Requests { get; } = [];
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Add(request with { Messages = request.Messages.ToArray() });
            await foreach (var e in inner.GenerateAsync(request, ct)) { if (e is ModelToolCallEvent call) Calls.Add(call.Call); yield return e; }
        }
    }
}
