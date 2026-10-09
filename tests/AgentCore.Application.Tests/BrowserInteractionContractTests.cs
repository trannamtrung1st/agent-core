using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
namespace AgentCore.Application.Tests;
public sealed class BrowserInteractionContractTests
{
    [Theory]
    [InlineData("browser.click", "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"el_0123456789abcdefghijkl\"}}", BrowserOperation.Click)]
    [InlineData("browser.type", "{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"el_0123456789abcdefghijkl\"},\"text\":\"hello\"}", BrowserOperation.Type)]
    [InlineData("browser.fill_form", "{\"fields\":[{\"target\":{\"by\":\"role\",\"value\":\"button\",\"name\":\"el_0123456789abcdefghijkl\"},\"checked\":true}]}", BrowserOperation.FillForm)]
    [InlineData("browser.press_key", "{\"key\":\"Control+A\"}", BrowserOperation.PressKey)]
    public void Canonical_shapes_produce_typed_requests(string tool, string json, BrowserOperation operation)
    {
        using var args = JsonDocument.Parse(json);
        Assert.True(BrowserToolArguments.TryRequest(Guid.NewGuid(), tool, args.RootElement, out var request, out var error), error);
        Assert.Equal(operation, request.Operation);
    }

    [Theory]
    [InlineData("browser.click", "{\"ref\":\"button\"}", "invalid")]
    [InlineData("browser.type", "{\"ref\":\"el_0123456789abcdefghijkl\"}", "invalid")]
    [InlineData("browser.click", "{\"ref\":\"el_0123456789abcdefghijkl\",\"operation\":\"click\"}", "invalid")]
    [InlineData("browser.click", "{\"ref\":\"el_0123456789abcdefghijkl\",\"selector\":\"#private\"}", "invalid")]
    [InlineData("browser.click", "{\"ref\":\"el_0123456789abcdefghijkl\",\"headless\":false}", "forbidden")]
    [InlineData("browser.upload", "{\"ref\":\"el_0123456789abcdefghijkl\",\"path\":\"/tmp/private\"}", "invalid")]
    [InlineData("browser.find", "{\"role\":\"button\",\"text\":\"Save\"}", "invalid")]
    public void Unknown_fields_authority_and_conflicting_modes_are_rejected(string tool, string json, string expected)
    {
        using var args = JsonDocument.Parse(json);
        Assert.False(BrowserToolArguments.TryRequest(Guid.NewGuid(), tool, args.RootElement, out _, out var error));
        Assert.Equal(expected, error); Assert.DoesNotContain("private", error);
    }
}
