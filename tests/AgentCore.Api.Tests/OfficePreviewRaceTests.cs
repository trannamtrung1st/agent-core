using AgentCore.Contracts.Http;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Workspaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace AgentCore.Api.Tests;
public sealed class OfficePreviewRaceTests
{
    private sealed class Factory : AgentCoreApiFactory
    {
        public RacingStore Race = null!;
        private readonly string root = Path.Combine(Path.GetTempPath(), "office-race-" + Guid.NewGuid());
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["FilePreview:Limits:DocxBytes"] = "32" };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                var proxy = DispatchProxy.Create<IAgentInstanceWorkspaceStore, RacingStore>(); Race = (RacingStore)proxy;
                Race.Inner = new FileAgentInstanceWorkspaceStore(root, TimeProvider.System, new AgentCore.Infrastructure.Identity.SystemIdGenerator(TimeProvider.System));
                services.Replace(ServiceDescriptor.Singleton(proxy));
            });
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
    [Fact]
    public async Task Workspace_growth_after_metadata_preflight_is_rejected_without_full_read_and_download_is_available()
    {
        using var host = new Factory(); var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var response = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 27 }); response.EnsureSuccessStatusCode();
        var owner = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("instanceId").GetGuid();
        var item = await host.Race.Inner.WriteFileAsync(owner, "/home/race.docx", OfficeLimits.DocumentMime, "small"u8.ToArray(), null, null, null);
        host.Race.Armed = true;
        var path = $"/api/v2/agent-instances/{owner}/workspace/{item.ItemId}";
        var preview = await client.GetAsync(path + "/office-preview");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, preview.StatusCode);
        Assert.False(host.Race.FullRead);
        Assert.Equal(new byte[64], await client.GetByteArrayAsync(path + "/content"));
    }
    private sealed class ResourceFactory : AgentCoreApiFactory
    {
        public ResourceRace Race = null!; public ContentSpy Spy = null!;
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => new Dictionary<string, string?> { ["FilePreview:Limits:DocxBytes"] = "32" };
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                var original = services.Last(d => d.ServiceType == typeof(IAgentInstanceStore));
                services.Replace(ServiceDescriptor.Singleton<IAgentInstanceStore>(sp => {
                    var proxy = DispatchProxy.Create<IAgentInstanceStore, ResourceRace>(); Race = (ResourceRace)proxy;
                    Race.Inner = (IAgentInstanceStore)(original.ImplementationInstance ?? original.ImplementationFactory!(sp)); return proxy;
                }));
                var content = services.Last(d => d.ServiceType == typeof(IDefinitionResourceContentStore));
                services.Replace(ServiceDescriptor.Singleton<IDefinitionResourceContentStore>(sp => {
                    var proxy = DispatchProxy.Create<IDefinitionResourceContentStore, ContentSpy>(); Spy = (ContentSpy)proxy;
                    Spy.Inner = (IDefinitionResourceContentStore)(content.ImplementationInstance ?? content.ImplementationFactory!(sp)); return proxy;
                }));
            });
        }
    }
    [Fact]
    public async Task Resource_growth_between_preview_catalog_and_content_read_is_rejected_before_blob_allocation()
    {
        using var host = new ResourceFactory(); var client = host.CreateClient();
        client.DefaultRequestHeaders.Add(OwnerCapabilityHeaders.Name, TestOwnerCapability.Token(host.Services));
        var response = await client.PostAsJsonAsync("/api/v2/admin/agent-instances", new { definitionId = "general-assistant", version = 26 }); response.EnsureSuccessStatusCode();
        var owner = (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("instanceId").GetGuid();
        var resources = host.Services.GetRequiredService<AgentCore.Application.Admin.AgentInstanceResourceService>();
        var catalog = await resources.ListAsync(owner);
        var saved = await resources.UpsertAsync(owner, catalog.InstanceRevision, null, null, "reference/race.docx", AgentCore.Domain.Definitions.AgentDefinitionResourceKind.Reference, OfficeLimits.DocumentMime, "small"u8.ToArray());
        var item = saved.Resources.Single(r => r.Origin == "Instance");
        var row = (await host.Services.GetRequiredService<IAgentInstanceStore>().ReadResourcesAsync(owner)).InstanceResources.Single();
        var larger = new byte[64]; host.Spy.TargetHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(larger));
        host.Race.Mutate = async () => { await resources.UpsertAsync(owner, saved.InstanceRevision, row.ResourceId, item.Revision, item.LogicalPath, row.Kind, row.MediaType, larger); };
        host.Race.Armed = true;
        var preview = await client.GetAsync($"/api/v2/admin/agent-instances/{owner}/resources/{Uri.EscapeDataString(item.Key)}/office-preview");
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, preview.StatusCode);
        Assert.False(host.Spy.ReadTarget);
    }
    public class ContentSpy : DispatchProxy
    {
        public IDefinitionResourceContentStore Inner = null!; public string? TargetHash; public bool ReadTarget;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IDefinitionResourceContentStore.ReadAsync) && (string)args![0]! == TargetHash) ReadTarget = true;
            return method.Invoke(Inner, args);
        }
    }
    public class ResourceRace : DispatchProxy
    {
        public IAgentInstanceStore Inner = null!; public bool Armed; public Func<Task> Mutate = null!; private int finds;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAgentInstanceStore.FindAsync)) return new ValueTask<AgentCore.Domain.Definitions.AgentInstance?>(Find((Guid)args![0]!, (CancellationToken)args[1]!));
            return method.Invoke(Inner, args);
        }
        private async Task<AgentCore.Domain.Definitions.AgentInstance?> Find(Guid owner, CancellationToken ct)
        {
            // ListAsync verifies owner revision twice. Mutate after that completed
            // preflight, just before the content service's fresh catalog resolves.
            if (Armed && ++finds == 3) { Armed = false; await Mutate(); }
            return await Inner.FindAsync(owner, ct);
        }
    }
    public class RacingStore : DispatchProxy
    {
        public IAgentInstanceWorkspaceStore Inner = null!; public bool Armed, FullRead;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(IAgentInstanceWorkspaceStore.ReadAsync)) FullRead = true;
            if (method.Name == nameof(IAgentInstanceWorkspaceStore.InspectAsync))
                return new ValueTask<AgentWorkspaceContent>(Inspect((Guid)args![0]!, (Guid)args[1]!, (int)args[2]!, (CancellationToken)args[3]!));
            return method.Invoke(Inner, args);
        }
        private async Task<AgentWorkspaceContent> Inspect(Guid owner, Guid id, int max, CancellationToken ct)
        {
            var result = await Inner.InspectAsync(owner, id, max, ct);
            if (Armed && max == 0) { Armed = false; await Inner.WriteFileAsync(owner, result.Item.LogicalPath, result.Item.ContentType, new byte[64], null, result.Item.Revision, result.Item.Sha256Hex, ct); }
            return result;
        }
    }
}
