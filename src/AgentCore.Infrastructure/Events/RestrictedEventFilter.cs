using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Events;
using Jint;

namespace AgentCore.Infrastructure.Events;

// Parse an independent, deliberately smaller grammar before the JavaScript engine sees any source.
public sealed class RestrictedEventFilter : IEventFilterEvaluator
{
    private static readonly SemaphoreSlim Workers = new(4);
    public string? Validate(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        if (Encoding.UTF8.GetByteCount(expression) > 1024) return "filter-source-budget";
        try { new Grammar(expression).Parse(); Engine.PrepareScript("(" + expression + ")"); return null; }
        catch (FilterSyntaxException e) { return e.Code; }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException) { return "filter-syntax-not-allowed"; }
    }

    public EventFilterResult Evaluate(string? expression, JsonElement envelope, CancellationToken cancellationToken = default)
    {
        var invalid = Validate(expression);
        if (invalid is not null) return new(null, "error", invalid);
        if (string.IsNullOrWhiteSpace(expression)) return new(true, "matched");
        if (Encoding.UTF8.GetByteCount(envelope.GetRawText()) > 8192) return new(null, "error", "filter-envelope-budget");
        if (!UniqueProperties(envelope)) return new(null, "error", "filter-envelope-ambiguous");
        if (!SafeNumbers(envelope)) return new(null, "error", "filter-unsafe-number");
        if (!Workers.Wait(0, cancellationToken)) return new(null, "error", "filter-worker-budget");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var engine = new Engine(options => options.Strict().LimitMemory(4 * 1024 * 1024)
                .MaxStatements(256).TimeoutInterval(TimeSpan.FromMilliseconds(100)).CancellationToken(deadline.Token));
            // JSON.parse creates engine-owned values; no CLR value, delegate or host object is projected.
            engine.Execute("const event = JSON.parse(" + JsonSerializer.Serialize(envelope.GetRawText()) + ");");
            var result = engine.Evaluate("(" + expression + ")");
            if (!result.IsBoolean()) return new(null, "error", "filter-result-not-boolean");
            return result.AsBoolean() ? new(true, "matched") : new(false, "notMatched");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var code = deadline.IsCancellationRequested || e is TimeoutException or OperationCanceledException
                ? "filter-timeout" : e.GetType().Name switch
                { "ExecutionCanceledException" => "filter-timeout", "MemoryLimitExceededException" => "filter-memory-budget",
                    "StatementsCountOverflowException" => "filter-statement-budget", _ => "filter-evaluation-error" };
            return new(null, "error", code);
        }
        finally { Workers.Release(); }
    }

    private static bool UniqueProperties(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == value.EnumerateObject().Count() && value.EnumerateObject().All(p => UniqueProperties(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(UniqueProperties),
        _ => true
    };

    private static bool SafeNumbers(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetDouble(out var n) && double.IsFinite(n) && Math.Abs(n) <= 9007199254740991d
            && value.TryGetDecimal(out var d) && (decimal)n == d,
        JsonValueKind.Object => value.EnumerateObject().All(p => SafeNumbers(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(SafeNumbers),
        _ => true
    };

    private sealed class FilterSyntaxException(string code) : Exception { public string Code { get; } = code; }
    private sealed class Grammar(string source)
    {
        private int position, nodes;
        private string token = "";
        private bool literalString;
        public void Parse() { Next(); Expression(0, 1); if (token != "") Fail(); }
        private void Expression(int minimum, int depth)
        {
            if (++nodes > 128 || depth > 12) throw new FilterSyntaxException("filter-ast-budget");
            if (token == "!") { Next(); Expression(5, depth + 1); }
            else if (token == "(") { Next(); Expression(0, depth + 1); Require(")"); }
            else if (token == "event" && !literalString)
            {
                Next();
                while (token is "." or "?." or "[")
                {
                    if (++nodes > 128 || ++depth > 12) throw new FilterSyntaxException("filter-ast-budget");
                    var bracket = token == "[";
                    Next();
                    if (bracket ? !literalString : literalString || !Identifier(token)) Fail();
                    if (token is "__proto__" or "constructor" or "prototype") Fail();
                    Next();
                    if (bracket) Require("]");
                }
            }
            else if (literalString || token is "true" or "false" or "null" || Number(token)) Next();
            else Fail();
            while (Precedence(token) is var precedence && precedence >= minimum && precedence > 0)
            { Next(); Expression(precedence + 1, depth + 1); if (++nodes > 128) throw new FilterSyntaxException("filter-ast-budget"); }
        }
        private static int Precedence(string t) => t switch { "??" => 1, "||" => 1, "&&" => 2, "===" or "!==" => 3, ">" or ">=" or "<" or "<=" => 4, _ => 0 };
        private static bool Identifier(string t) => t.Length > 0 && (char.IsAsciiLetter(t[0]) || t[0] == '_') && t.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        private static bool Number(string t) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) && Math.Abs(n) <= 9007199254740991d && decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && (decimal)n == d;
        private void Require(string t) { if (token != t) Fail(); Next(); }
        private static void Fail() => throw new FilterSyntaxException("filter-syntax-not-allowed");
        private void Next()
        {
            literalString = false;
            while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
            if (position == source.Length) { token = ""; return; }
            var start = position;
            var c = source[position++];
            if (c is '\'' or '"')
            {
                while (position < source.Length && source[position] != c)
                { if (source[position] == '\\' || char.IsControl(source[position])) Fail(); position++; }
                if (position == source.Length) Fail();
                token = source[(start + 1)..position++]; literalString = true; return;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            { while (position < source.Length && (char.IsAsciiLetterOrDigit(source[position]) || source[position] == '_')) position++; }
            else if (char.IsAsciiDigit(c) || c == '-')
            { while (position < source.Length && (char.IsAsciiDigit(source[position]) || source[position] is '.' or 'e' or 'E' or '+' or '-')) position++; }
            else
            {
                foreach (var op in new[] { "===", "!==", "&&", "||", "??", "?.", ">=", "<=" })
                    if (source.AsSpan(start).StartsWith(op, StringComparison.Ordinal)) { position = start + op.Length; token = op; return; }
            }
            token = source[start..position];
        }
    }
}
