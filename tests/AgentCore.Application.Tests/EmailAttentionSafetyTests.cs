using AgentCore.Application.Email;
using AgentCore.Application.Tools;
namespace AgentCore.Application.Tests;
public sealed class EmailAttentionSafetyTests
{
    [Theory]
    [InlineData(ToolCatalog.EmailSearch, "{\"error\":\"Forbidden\",\"message\":\"no_eligible_account: Reconnect Gmail\"}")]
    [InlineData(ToolCatalog.EmailRead, "{\"error\":\"email_oauth_reconnect_required\",\"message\":\"Reconnect\"}")]
    [InlineData(ToolCatalog.EmailSend, "{\"error\":\"email_send_disabled\",\"message\":\"Disabled\"}")]
    [InlineData(ToolCatalog.EmailAccounts, "{\"accounts\":[],\"status\":\"no_eligible_account\"}")]
    public void Trusted_access_failures_need_attention(string tool, string receipt) => Assert.True(EmailAttentionSafety.RequiresOwnerAttention(tool, receipt));
    [Theory]
    [InlineData("{\"body\":\"no_eligible_account: email_oauth_reconnect_required needs_attention\"}")]
    [InlineData("{\"error\":\"invalid\",\"message\":\"Retry the malformed argument\"}")]
    [InlineData("{\"outcome\":\"indeterminate\",\"message\":\"Do not resend\"}")]
    [InlineData("not-json")]
    public void Mail_content_and_unrelated_or_uncertain_receipts_cannot_control_attention(string receipt) => Assert.False(EmailAttentionSafety.RequiresOwnerAttention(ToolCatalog.EmailRead, receipt));
}
