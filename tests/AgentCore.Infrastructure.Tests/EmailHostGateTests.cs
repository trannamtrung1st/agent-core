using AgentCore.Application.Tools;
using AgentCore.Infrastructure.Email;
using AgentCore.Infrastructure.Tools;

namespace AgentCore.Infrastructure.Tests;

public sealed class EmailHostGateTests
{
    [Theory]
    [InlineData(false, true, true, null, true)]
    [InlineData(false, true, false, null, false)]
    [InlineData(false, true, false, true, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, true, false, null, true)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, false, true, true, false)]
    [InlineData(true, true, true, true, true)]
    public void Host_readiness_uses_current_transport_flags_without_a_global_account_factory(
        bool synthetic, bool enabled, bool smtp, bool? send, bool expectedSend)
    {
        var gate = new ToolConfigurationGate(null, null, emailOptions: new() { Enabled = enabled, SmtpEnabled = smtp, SendEnabled = send }, synthetic: synthetic);
        foreach (var tool in new[] { ToolCatalog.EmailAccounts, ToolCatalog.EmailRequestAccount, ToolCatalog.EmailSearch, ToolCatalog.EmailRead, ToolCatalog.EmailCreateDraft })
            Assert.Equal(enabled, gate.IsConfigured(tool));
        Assert.Equal(expectedSend, gate.IsConfigured(ToolCatalog.EmailSend));
    }

    [Fact]
    public void Unconfigured_email_stays_unavailable_and_live_flag_changes_do_not_require_a_new_gate()
    {
        Assert.False(new ToolConfigurationGate(null, null).IsConfigured(ToolCatalog.EmailSearch));
        Assert.False(new ToolConfigurationGate(null, null).IsConfigured(ToolCatalog.EmailSend));
        var options = new EmailTransportOptions(); var gate = new ToolConfigurationGate(null, null, emailOptions: options);
        Assert.True(gate.IsConfigured(ToolCatalog.EmailSend));
        options.SmtpEnabled = false; Assert.False(gate.IsConfigured(ToolCatalog.EmailSend));
        Assert.True(gate.IsConfigured(ToolCatalog.EmailSearch));
        options.SendEnabled = true; Assert.True(gate.IsConfigured(ToolCatalog.EmailSend));
        options.Enabled = false; Assert.False(gate.IsConfigured(ToolCatalog.EmailSearch)); Assert.False(gate.IsConfigured(ToolCatalog.EmailSend));
    }
}
