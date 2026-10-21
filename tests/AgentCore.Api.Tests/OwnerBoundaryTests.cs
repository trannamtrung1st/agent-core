using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Http;
using AgentCore.Contracts.Http;
using Microsoft.AspNetCore.Http;

namespace AgentCore.Api.Tests;

public sealed class OwnerBoundaryTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public OwnerBoundaryTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void IsLoopback_returns_false_when_remote_ip_is_unknown()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = null;
        Assert.False(TrustedLocalCaller.IsLoopback(context));
        Assert.False(TrustedLocalCaller.IsTrustedLocal(context, trustPublishedPortGateway: true));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void IsTrustedLocal_accepts_loopback(string address)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        Assert.True(TrustedLocalCaller.IsTrustedLocal(context, trustPublishedPortGateway: false));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.9")]
    [InlineData("192.168.1.50")]
    public void IsTrustedLocal_rejects_non_loopback_and_private_ranges(string address)
    {
        DockerPublishedPortGateway.ResolveOverride = () => IPAddress.Parse("172.18.0.1");
        try
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(address);
            Assert.False(TrustedLocalCaller.IsTrustedLocal(context, trustPublishedPortGateway: false));
            Assert.False(TrustedLocalCaller.IsTrustedLocal(context, trustPublishedPortGateway: true));
        }
        finally
        {
            DockerPublishedPortGateway.ResolveOverride = null;
        }
    }

    [Fact]
    public void IsTrustedLocal_accepts_only_the_published_port_gateway()
    {
        var gateway = IPAddress.Parse("172.18.0.1");
        DockerPublishedPortGateway.RunningInContainerOverride = () => true;
        DockerPublishedPortGateway.ResolveOverride = () => gateway;
        try
        {
            var trusted = new DefaultHttpContext();
            trusted.Connection.RemoteIpAddress = gateway;
            Assert.False(TrustedLocalCaller.IsTrustedLocal(trusted, trustPublishedPortGateway: false));
            Assert.True(TrustedLocalCaller.IsTrustedLocal(trusted, trustPublishedPortGateway: true));

            var neighbor = new DefaultHttpContext();
            neighbor.Connection.RemoteIpAddress = IPAddress.Parse("172.18.0.2");
            Assert.False(TrustedLocalCaller.IsTrustedLocal(neighbor, trustPublishedPortGateway: true));
        }
        finally
        {
            DockerPublishedPortGateway.ResolveOverride = null;
            DockerPublishedPortGateway.RunningInContainerOverride = null;
        }
    }

    [Fact]
    public void IsTrustedLocal_rejects_gateway_trust_outside_container_even_when_gateway_resolves()
    {
        var gateway = IPAddress.Parse("172.18.0.1");
        DockerPublishedPortGateway.RunningInContainerOverride = () => false;
        DockerPublishedPortGateway.ResolveOverride = () => gateway;
        try
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = gateway;
            Assert.False(TrustedLocalCaller.IsTrustedLocal(context, trustPublishedPortGateway: true));
            Assert.Null(DockerPublishedPortGateway.TryResolve());
        }
        finally
        {
            DockerPublishedPortGateway.ResolveOverride = null;
            DockerPublishedPortGateway.RunningInContainerOverride = null;
        }
    }
}
