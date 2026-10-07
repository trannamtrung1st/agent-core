using System.Collections;
using System.Text;

namespace AgentCore.Infrastructure.Browser;

// Owned under SessionBrowser.Gate. Retain every previously injected value until profile reset;
// eviction would let a page reflect an older password into a later capture.
internal sealed class ProtectedBrowserValues : IReadOnlyList<string>
{
    internal const int MaxVariants = 256;
    internal const int MaxBytes = 1024 * 1024;
    private readonly List<string> _values = [];
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private int _bytes;
    public int Count => _values.Count;
    public string this[int index] => _values[index];
    public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool TryRegister(string value)
    {
        var additions = new[] { value, Uri.EscapeDataString(value), System.Net.WebUtility.HtmlEncode(value) }
            .Where(v => v.Length > 0 && !_known.Contains(v)).Distinct(StringComparer.Ordinal).ToArray();
        var bytes = additions.Sum(Encoding.UTF8.GetByteCount);
        if (_values.Count + additions.Length > MaxVariants || _bytes + bytes > MaxBytes) return false;
        foreach (var addition in additions) { _known.Add(addition); _values.Add(addition); }
        _bytes += bytes;
        return true;
    }
}
