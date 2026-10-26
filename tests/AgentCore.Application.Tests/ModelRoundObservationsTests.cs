using AgentCore.Application.Observability;
using AgentCore.Domain.Models;

namespace AgentCore.Application.Tests;

public sealed class ModelRoundObservationsTests
{
    [Fact]
    public void Observations_are_tenant_and_revision_scoped_bounded_expiring_and_never_quality_estimates()
    {
        var route = new ModelRoutePin(Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1,
            ModelProviderKind.OpenAI, "fixture-model", ModelTransport.Responses);
        var at = DateTimeOffset.UtcNow;
        for (var i = 0; i < 100; i++) ModelRoundObservations.Record(route, i, i == 99, at);
        var summary = ModelRoundObservations.Read(route.TenantId, route.OfferingId, at, route)!;
        Assert.Equal(64, summary.SampleCount); Assert.Equal(1, summary.ErrorCount);
        Assert.Equal("providerRoundIncludingColdAndRetries", summary.Scope);
        Assert.Equal(67, summary.P50Milliseconds); Assert.Equal(95, summary.P95Milliseconds);
        Assert.Null(ModelRoundObservations.Read(Guid.NewGuid(), route.OfferingId, at));
        Assert.Null(ModelRoundObservations.Read(route.TenantId, route.OfferingId, at.AddMinutes(31)));
        var edited = route with { ConnectionRevision = 2 };
        Assert.Null(ModelRoundObservations.Read(route.TenantId, route.OfferingId, at, edited));
        ModelRoundObservations.Record(edited, 5, false, at);
        Assert.Equal(1, ModelRoundObservations.Read(route.TenantId, route.OfferingId, at, edited)!.SampleCount);
        ModelRoundObservations.Record(edited, double.NaN, true, at);
        Assert.Equal(1, ModelRoundObservations.Read(route.TenantId, route.OfferingId, at, edited)!.SampleCount);
    }
}
