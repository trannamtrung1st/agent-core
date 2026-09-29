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
}
