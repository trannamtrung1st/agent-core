using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Application.Models;
using AgentCore.Domain.Models;

namespace AgentCore.Infrastructure.Tests;

public sealed class SyntheticModelCatalogTests
{
    [Fact]
    public void Offline_fixture_has_fixed_profiles_and_no_real_provider_identity()
    {
        var catalog = SyntheticModelCatalog.Create();
        Assert.Equal("dca30000-0000-4000-8000-000000000001", catalog.DefaultOfferingId);
        Assert.Equal(new[] { "dca30000-0000-4000-8000-000000000001", "dca30000-0000-4000-8000-000000000002", "dca30000-0000-4000-8000-000000000003" }, catalog.Models.Select(m => m.OfferingId));
        Assert.All(catalog.Models, model => Assert.Equal("dca20000-0000-4000-8000-000000000001", model.ConnectionId));
        Assert.All(catalog.Models, model => Assert.True(model.SupportsInstructionHierarchy));
        Assert.Equal("low", catalog.Default.DefaultReasoningEffort);
        Assert.True(catalog.Get("dca30000-0000-4000-8000-000000000003")!.Vision);
        Assert.False(catalog.Get("dca30000-0000-4000-8000-000000000001")!.Vision);
    }

    [Fact]
    public void Offline_fixture_binding_is_exactly_qualified_by_the_authorized_tenant()
    {
        var first = SessionModelBinder.PinDefault(SyntheticModelCatalog.Create(Guid.NewGuid()));
        var second = SessionModelBinder.PinDefault(SyntheticModelCatalog.Create(Guid.NewGuid()));
        Assert.Equal(first.OfferingId, second.OfferingId); Assert.Equal(first.ConnectionId, second.ConnectionId);
        Assert.NotEqual(first.Binding, second.Binding);
        Assert.Equal(ModelMetadataSource.SyntheticFixture, first.Binding!.Capabilities.Source);
        Assert.Null(first.Binding.Route.CredentialId);
        Assert.Null(first.Binding.Route.CredentialRevision); Assert.Null(first.Binding.Route.SecretRevision);
        Assert.True(SyntheticModelCatalog.IsExactFixture(first.Binding));
    }
}
