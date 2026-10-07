using AgentCore.Infrastructure.Browser;

namespace AgentCore.Infrastructure.Tests;

public sealed class ProtectedBrowserValuesTests
{
    [Fact]
    public void Duplicate_variants_do_not_grow_and_capacity_failure_preserves_older_values_atomically()
    {
        var values = new ProtectedBrowserValues();
        for (var i = 0; i < 1000; i++) Assert.True(values.TryRegister("first<&"));
        Assert.Equal(3, values.Count);
        for (var i = 3; i < ProtectedBrowserValues.MaxVariants; i++) Assert.True(values.TryRegister("secret-" + i));
        Assert.False(values.TryRegister("new<&"));
        Assert.Equal(ProtectedBrowserValues.MaxVariants, values.Count);
        Assert.Contains("first<&", values);
        Assert.DoesNotContain("new<&", values);
        Assert.True(values.TryRegister("first<&"));
    }

    [Fact]
    public void Encoded_utf8_byte_budget_is_bounded_independently_of_variant_count()
    {
        var values = new ProtectedBrowserValues();
        var large = new string('&', 60000);
        Assert.True(values.TryRegister(large));
        Assert.False(values.TryRegister(large + ">"));
        Assert.Equal(3, values.Count);
        Assert.True(values.TryRegister(large));
    }
}
