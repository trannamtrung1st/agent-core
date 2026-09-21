using System.Net;
using AgentCore.Infrastructure.PublicWeb;

namespace AgentCore.Infrastructure.Tests;

public sealed class PublicAddressPolicyTests
{
    [Theory]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:127.0.0.1")]
    public void Rejects_ipv4_mapped_private_addresses(string address)
    {
        Assert.False(PublicAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("fd00::1")]
    [InlineData("fe80::1")]
    public void Rejects_ula_and_link_local_ipv6(string address)
    {
        Assert.False(PublicAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("metadata.google.internal")]
    [InlineData("host.local")]
    [InlineData("service.corp.internal")]
    public void Rejects_metadata_and_machine_local_hostnames(string host)
    {
        Assert.False(PublicAddressPolicy.IsAllowedHostName(host));
    }

    [Fact]
    public void Allows_public_ipv4_literal()
    {
        Assert.True(PublicAddressPolicy.IsAllowed(IPAddress.Parse("93.184.216.34")));
    }
}
