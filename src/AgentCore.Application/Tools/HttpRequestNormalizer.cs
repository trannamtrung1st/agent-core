using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class HttpRequestLimits
{
    public const int MaxRequestBodyBytes = 64 * 1024;
    public const int MaxHeaderCount = 8;
    public const int MaxHeaderValueLength = 512;
    public const int MaxBodyPreviewChars = 240;
}

public sealed record HttpRequestSnapshot(
    string Method,
    string Url,
    IReadOnlyList<HttpRequestHeader> Headers,
    string Body,
    string BodySha256,
    bool FollowRedirects);

public static class HttpRequestNormalizer
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"
    };

    private static readonly Dictionary<string, string> AllowedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["accept"] = "Accept",
        ["content-type"] = "Content-Type",
        ["accept-language"] = "Accept-Language"
    };

    public static bool TryNormalize(
        JsonElement args,
        out HttpRequestSnapshot? snapshot,
        out string errorCode,
        out string message)
    {
        snapshot = null;
        errorCode = "";
        message = "";
        if (!TryString(args, "method", out var methodRaw) || !TryString(args, "url", out var urlRaw))
        {
            return Invalid("method and url are required.", out errorCode, out message);
        }

        var method = methodRaw.Trim().ToUpperInvariant();
        if (!Methods.Contains(method))
        {
            return Invalid("method must be GET, HEAD, POST, PUT, PATCH, or DELETE.", out errorCode, out message);
        }

        if (!Uri.TryCreate(urlRaw.Trim(), UriKind.Absolute, out var uri))
        {
            return Invalid("url must be an absolute http or https URL.", out errorCode, out message);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("url must be an absolute http or https URL.", out errorCode, out message);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            return Reject("credentials_in_url", "Embedded credentials in URLs are not permitted.", out errorCode, out message);
        }

        if (!PublicNetworkPolicy.IsAllowedHostName(uri.Host))
        {
            return Reject("forbidden_host", "Host is not permitted for public HTTP requests.", out errorCode, out message);
        }

        if (!TryHeaders(args, out var headers, out errorCode, out message))
        {
            return false;
        }

        var body = "";
        if (args.TryGetProperty("body", out var bodyElement) && bodyElement.ValueKind != JsonValueKind.Null)
        {
            if (bodyElement.ValueKind != JsonValueKind.String)
            {
                return Invalid("body must be a string.", out errorCode, out message);
            }

            body = bodyElement.GetString() ?? "";
        }

        if (body.Length > 0 && method is "GET" or "HEAD")
        {
            return Invalid("GET and HEAD requests cannot include a body. Prefer web.fetch for ordinary public reads.", out errorCode, out message);
        }

        if (Encoding.UTF8.GetByteCount(body) > HttpRequestLimits.MaxRequestBodyBytes)
        {
            return Invalid("Request body exceeded the permitted size.", out errorCode, out message);
        }

        var url = uri.GetComponents(
            UriComponents.Scheme | UriComponents.Host | UriComponents.Port | UriComponents.Path | UriComponents.Query,
            UriFormat.UriEscaped);
        snapshot = new HttpRequestSnapshot(
            method,
            url,
            headers,
            body,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
            method is "GET" or "HEAD");
        return true;
    }

    public static string ComputeActionHash(HttpRequestSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("bodySha256", snapshot.BodySha256);
            writer.WritePropertyName("headers");
            writer.WriteStartArray();
            foreach (var header in snapshot.Headers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", header.Name);
                writer.WriteString("value", header.Value);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("method", snapshot.Method);
            writer.WriteString("url", snapshot.Url);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return ToolActionHash.Compute(ToolCatalog.HttpRequest, document.RootElement);
    }

    public static string Summary(HttpRequestSnapshot snapshot) =>
        ToolApprovalPreview.BoundSummary($"{snapshot.Method} {snapshot.Url}");

    public static Dictionary<string, string> Details(HttpRequestSnapshot snapshot)
    {
        var headers = snapshot.Headers.Count == 0
            ? "(none)"
            : string.Join(", ", snapshot.Headers.Select(header => $"{header.Name}: {header.Value}"));
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Method"] = snapshot.Method,
            ["URL"] = ToolApprovalPreview.BoundDetailValue(snapshot.Url),
            ["Headers"] = ToolApprovalPreview.BoundDetailValue(headers),
            ["Body SHA-256"] = snapshot.BodySha256
        };
        if (snapshot.Body.Length > 0)
        {
            var preview = snapshot.Body.Length <= HttpRequestLimits.MaxBodyPreviewChars
                ? snapshot.Body
                : snapshot.Body[..HttpRequestLimits.MaxBodyPreviewChars];
            details["Body preview"] = ToolApprovalPreview.BoundDetailValue(preview);
        }

        return details;
    }

    private static bool TryHeaders(
        JsonElement args,
        out IReadOnlyList<HttpRequestHeader> headers,
        out string errorCode,
        out string message)
    {
        headers = [];
        errorCode = "";
        message = "";
        if (!args.TryGetProperty("headers", out var element) || element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return Invalid("headers must be an object of permitted header names.", out errorCode, out message);
        }

        var list = new List<HttpRequestHeader>();
        foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsCredentialHeader(property.Name))
            {
                return Reject(
                    "forbidden_header",
                    "Authorization, Cookie, API keys, and other credentials are not permitted on http.request.",
                    out errorCode,
                    out message);
            }

            if (!AllowedHeaders.TryGetValue(property.Name, out var canonical))
            {
                return Reject(
                    "forbidden_header",
                    "Only Accept, Content-Type, and Accept-Language headers are permitted.",
                    out errorCode,
                    out message);
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return Invalid("header values must be strings.", out errorCode, out message);
            }

            var value = property.Value.GetString() ?? "";
            if (value.Length == 0
                || value.Length > HttpRequestLimits.MaxHeaderValueLength
                || value.Any(ch => ch is '\r' or '\n' or '\0'))
            {
                return Invalid("A header value is empty or not permitted.", out errorCode, out message);
            }

            if (canonical == "Content-Type" && !IsSafeContentType(value))
            {
                return Invalid("Content-Type is not a permitted media type.", out errorCode, out message);
            }

            list.Add(new HttpRequestHeader(canonical, value.Trim()));
        }

        if (list.Count > HttpRequestLimits.MaxHeaderCount
            || list.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
        {
            return Invalid("Too many headers.", out errorCode, out message);
        }

        headers = list;
        return true;
    }

    private static bool IsCredentialHeader(string name)
    {
        var normalized = name.Trim();
        if (normalized.Equals("authorization", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("proxy-authorization", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("set-cookie", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("api-key", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("x-api-token", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("x-auth-token", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("x-access-token", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalized.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("api_key", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeContentType(string value)
    {
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1)
        {
            return false;
        }

        return value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '/' or '+' or '.' or '-' or ';' or '=' or ' ' or '"');
    }

    private static bool TryString(JsonElement args, string name, out string value)
    {
        value = "";
        if (!args.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool Invalid(string message, out string errorCode, out string errorMessage)
    {
        errorCode = "invalid";
        errorMessage = message;
        return false;
    }

    private static bool Reject(string code, string message, out string errorCode, out string errorMessage)
    {
        errorCode = code;
        errorMessage = message;
        return false;
    }
}
