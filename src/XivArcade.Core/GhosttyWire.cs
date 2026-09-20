using System.Text.Json;
using System.Text.Json.Nodes;

namespace XivArcade.Core;

/// <summary>
/// The one request XivArcade sends to ghostty-dalamud's Call gate, and its reply envelope. Pure: no Dalamud
/// types. The parser is total: malformed input is a failure with a reason, never an exception.
/// </summary>
public static class GhosttyWire
{
    public const string Caller = "XivArcade";

    public const string RunPrefix = "window pull run ";

    /// <summary><c>window.open {"run": command}</c>: the host agent starts it with sh -c and shows its window as a panel.</summary>
    public static string OpenRun(string command)
        => new JsonObject { ["method"] = "window.open", ["params"] = new JsonObject { ["run"] = command }, ["caller"] = Caller }.ToJsonString();

    /// <summary>The same thing for the older Post gate (a /term line without "/term ").</summary>
    public static string PostLine(string command) => RunPrefix + command;

    /// <summary>Null when the reply says ok; otherwise why not.</summary>
    public static string? ReplyError(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "empty reply";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "reply is not an object";
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                return null;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            return string.IsNullOrEmpty(error) ? "ghostty refused the call" : error;
        }
        catch (JsonException)
        {
            return "reply is not JSON";
        }
    }
}
