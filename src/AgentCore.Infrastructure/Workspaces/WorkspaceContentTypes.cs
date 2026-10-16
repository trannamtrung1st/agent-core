using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Workspaces;

internal static class WorkspaceContentTypes
{
    public static string Of(string physical) =>
        Path.GetExtension(physical).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".docx" => OfficeLimits.DocumentMime,
            ".xlsx" => OfficeLimits.SpreadsheetMime,
            ".csv" => "text/csv",
            ".tsv" => "text/tab-separated-values",
            ".markdown" => "text/markdown",
            ".jsonl" or ".ndjson" => "application/x-ndjson",
            ".yaml" or ".yml" => "application/yaml",
            ".toml" => "application/toml",
            ".xml" => "application/xml",
            ".log" or ".ini" or ".js" or ".jsx" or ".ts" or ".tsx" or ".cs" or ".py" or ".sql" or ".css" or ".html" or ".sh" or ".rb" or ".go" or ".rs" or ".java" or ".c" or ".cpp" or ".h" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".avif" => "image/avif",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            _ => "application/octet-stream"
        };

}
