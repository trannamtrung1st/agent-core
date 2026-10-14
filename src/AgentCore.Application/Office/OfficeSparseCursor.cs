using System.Text.Json;
using AgentCore.Application.Sessions;

namespace AgentCore.Application.Office;

/// <summary>Traversal evidence only. The caller must reauthorize and read the source on every call.</summary>
public sealed record OfficeSparseCursor(int Version, string Mode, string Sha256, string? SelectedSheet, int SheetOrdinal, string Sheet, string? After)
{
    public static string Encode(string sha256, string? selection, int ordinal, string sheet, string? after) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new OfficeSparseCursor(1, "sparse", sha256, selection, ordinal, sheet, after), OfficeReadResults.Json));

    public static OfficeSparseCursor? Decode(string? text, string sha256, string? selection)
    {
        if (text is null) return null;
        OfficeSparseCursor? cursor;
        try
        {
            if (text.Length is < 1 or > 2048) throw new FormatException();
            cursor = JsonSerializer.Deserialize<OfficeSparseCursor>(Convert.FromBase64String(text), OfficeReadResults.Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        { throw new AgentCoreException("office_cursor_invalid", "Invalid sparse reading cursor. Start a new read.", 400); }
        if (cursor is null || cursor.Version != 1 || cursor.Mode != "sparse" || cursor.SelectedSheet != selection || cursor.SheetOrdinal is < 1 or > 50
            || cursor.Sha256 is null || cursor.Sha256.Length != 64 || !cursor.Sha256.All(Uri.IsHexDigit)
            || string.IsNullOrEmpty(cursor.Sheet) || cursor.Sheet.Length > 31)
            throw new AgentCoreException("office_cursor_invalid", "The cursor does not match this sparse reading selection. Start a new read.", 400);
        if (cursor.Sha256 != sha256)
            throw new AgentCoreException("office_revision_conflict", "Source bytes changed during continuation. Start a new read.", 409);
        return cursor;
    }
}
