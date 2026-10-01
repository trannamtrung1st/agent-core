using AgentCore.Domain.Diagnostics;

namespace AgentCore.Domain.Tests;

public sealed class FailureReferenceTests
{
    private static readonly Guid DiagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
    private static readonly Guid CorrelationId = Guid.Parse("019944af-00d7-7000-8000-0000000000c1");

    [Fact]
    public void Valid_reference_keeps_only_the_safe_fields()
    {
        var reference = new FailureReference(DiagnosticId, "provider", "unavailable", CorrelationId);
        Assert.Equal(DiagnosticId, reference.DiagnosticId);
        Assert.Equal(CorrelationId, reference.CorrelationId);
        Assert.Equal("provider", reference.Category);
        Assert.Equal("unavailable", reference.Code);
        Assert.Equal(reference, new FailureReference(DiagnosticId, "provider", "unavailable", CorrelationId));
    }

    [Fact]
    public void Correlation_is_optional()
    {
        var reference = new FailureReference(DiagnosticId, "persistence", "SessionPersistenceUnavailable");
        Assert.Null(reference.CorrelationId);
        Assert.Equal("SessionPersistenceUnavailable", reference.Code);
    }

    [Fact]
    public void Empty_diagnostic_id_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new FailureReference(Guid.Empty, "provider", "unavailable"));
        Assert.Equal("diagnosticId", error.ParamName);
    }

    [Fact]
    public void Empty_correlation_id_is_rejected()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new FailureReference(DiagnosticId, "provider", "unavailable", Guid.Empty));
        Assert.Equal("correlationId", error.ParamName);
    }

    [Fact]
    public void Json_round_trips_only_the_four_safe_fields()
    {
        var reference = new FailureReference(DiagnosticId, "provider", "Unavailable", CorrelationId);
        var json = FailureReferenceJson.Serialize(reference);
        Assert.Contains("\"diagnosticId\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", json, StringComparison.OrdinalIgnoreCase);
        var restored = FailureReferenceJson.Deserialize(json);
        Assert.Equal(reference, restored);
        Assert.Null(FailureReferenceJson.Deserialize(null));
        Assert.Null(FailureReferenceJson.Deserialize("  "));
    }

    [Fact]
    public void Json_round_trips_allowlisted_provider_detail()
    {
        var reference = new FailureReference(
            DiagnosticId,
            "provider",
            "InvalidResponse",
            CorrelationId,
            "toolCallTruncated",
            "toolCall");
        var restored = FailureReferenceJson.Deserialize(FailureReferenceJson.Serialize(reference));
        Assert.Equal(reference, restored);
        Assert.DoesNotContain("arguments", FailureReferenceJson.Serialize(reference), StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_detail_rejects_raw_payload_tokens()
    {
        Assert.Throws<ArgumentException>(() =>
            new FailureReference(DiagnosticId, "provider", "InvalidResponse", failureReason: "{\"path\":\"secret\"}"));
        Assert.Throws<ArgumentException>(() =>
            new FailureReference(DiagnosticId, "provider", "InvalidResponse", providerResponseChannel: "prompt"));
    }

    [Fact]
    public void Json_rejects_unknown_fields()
    {
        var json = $$"""{"diagnosticId":"{{DiagnosticId}}","category":"provider","code":"Unavailable","stack":"secret"}""";
        var error = Assert.Throws<ArgumentException>(() => FailureReferenceJson.Deserialize(json));
        Assert.Contains("unknown field", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("provider failure")]
    [InlineData("bad\ncategory")]
    [InlineData("bad\rcategory")]
    [InlineData("{\"x\":1}")]
    [InlineData("a/b")]
    [InlineData("a:b")]
    [InlineData("a=b")]
    [InlineData("a\"b")]
    [InlineData("a\\b")]
    [InlineData("<script>")]
    [InlineData("1provider")]
    [InlineData("provider'")]
    public void Category_and_code_reject_empty_overlong_and_payload_characters(string token)
    {
        Assert.False(FailureReference.IsSafeToken(token));
        Assert.Throws<ArgumentException>(() => new FailureReference(DiagnosticId, token, "unavailable"));
        Assert.Throws<ArgumentException>(() => new FailureReference(DiagnosticId, "provider", token));
    }

    [Fact]
    public void Overlong_token_is_rejected()
    {
        var token = new string('a', FailureReference.MaxTokenLength + 1);
        Assert.False(FailureReference.IsSafeToken(token));
        Assert.Throws<ArgumentException>(() => new FailureReference(DiagnosticId, token, "unavailable"));
    }

    [Theory]
    [InlineData("provider", "unavailable")]
    [InlineData("persistence", "SessionPersistenceUnavailable")]
    [InlineData("work", "side-effect-indeterminate")]
    [InlineData("error", "error.code")]
    public void Bounded_tokens_are_accepted(string category, string code)
    {
        var reference = new FailureReference(DiagnosticId, category, code);
        Assert.Equal(category, reference.Category);
        Assert.Equal(code, reference.Code);
    }

    [Fact]
    public void Allowlisted_provider_detail_round_trips_and_rejects_raw_text()
    {
        var reference = new FailureReference(
            DiagnosticId,
            "provider",
            "InvalidResponse",
            CorrelationId,
            "toolCallTruncated",
            "toolCall");
        var restored = FailureReferenceJson.Deserialize(FailureReferenceJson.Serialize(reference));
        Assert.Equal(reference, restored);
        Assert.DoesNotContain("arguments", FailureReferenceJson.Serialize(reference), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new FailureReference(
            DiagnosticId,
            "provider",
            "InvalidResponse",
            failureReason: "{\"path\":\"secret\"}"));
    }
}
