using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentCore.Api.Tests;

public sealed class FilePreviewPolicyTests
{
    private sealed class PolicyFactory(IReadOnlyDictionary<string, string?> settings) : AgentCoreApiFactory
    {
        protected override IReadOnlyDictionary<string, string?> ExtraConfiguration => settings;
    }
    [Theory]
    [InlineData("Limits:MaxBytes", "0")]
    [InlineData("Limits:TextBytes", "67108865")]
    [InlineData("Image:MaxPixels", "40000001")]
    [InlineData("Spreadsheet:RowsPerWindow", "201")]
    [InlineData("Spreadsheet:MaxCellsPerWindow", "1")]
    [InlineData("Text:HighlightMaxLines", "10001")]
    [InlineData("WorkerTimeoutMs", "10001")]
    public void Invalid_policy_fails_startup(string key, string value)
    {
        using var factory = new PolicyFactory(new Dictionary<string, string?> { [$"FilePreview:{key}"] = value });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }
    [Fact]
    public void Defaults_bind_and_standard_environment_override_is_supported()
    {
        var defaults = new FilePreviewOptions(); Assert.True(defaults.IsValid()); Assert.Equal(12800, defaults.Spreadsheet.MaxCellsPerWindow);
        const string key = "AGENTCORE_PREVIEW_TEST_FilePreview__Limits__CsvBytes";
        Environment.SetEnvironmentVariable(key, "12345");
        try {
            var config = new ConfigurationBuilder().AddEnvironmentVariables("AGENTCORE_PREVIEW_TEST_").Build();
            var policy = config.GetSection("FilePreview").Get<FilePreviewOptions>()!;
            Assert.Equal(12345, policy.Limits.DelimitedBytes); Assert.True(policy.IsValid());
        } finally { Environment.SetEnvironmentVariable(key, null); }
    }
    [Fact]
    public void Global_ceiling_clamps_all_formats_and_immutable_office_ceiling_cannot_be_raised()
    {
        var policy = new FilePreviewOptions(); policy.Limits.MaxBytes = 100;
        Assert.True(policy.IsValid());
        var effective = policy.Effective();
        Assert.All(new[] { effective.Limits.TextBytes, effective.Limits.DelimitedBytes, effective.Limits.DocxBytes, effective.Limits.XlsxBytes, effective.Limits.ImageBytes, effective.Limits.AudioBytes, effective.Limits.VideoBytes }, value => Assert.Equal(100, value));
        Assert.Equal(100, effective.Text.HighlightMaxBytes);
        policy.Limits.MaxBytes = 64 * 1024 * 1024; policy.Limits.XlsxBytes = policy.Limits.MaxBytes;
        Assert.Equal(OfficeLimits.MaxPackageBytes, policy.Effective().Limits.XlsxBytes);
    }

}
