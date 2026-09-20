using System;
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

    /// <summary><c>focus.get</c>: which world panel has the keyboard. A read; answered from a snapshot.</summary>
    public static readonly string FocusGet = new JsonObject { ["method"] = "focus.get", ["caller"] = Caller }.ToJsonString();

    /// <summary><c>window.list</c>: the window panels and the results of recent requests.</summary>
    public static readonly string WindowList = new JsonObject { ["method"] = "window.list", ["caller"] = Caller }.ToJsonString();

    /// <summary>The request number in <c>window.open</c>'s reply (<c>{"ok":true,"result":{"queued":true,"request":N}}</c>), or null.</summary>
    public static long? OpenRequest(string? json) => Read<long>(json, result => Long(result, "request"));

    /// <summary>The panel a <c>window.open</c> request became, from <c>window.list</c>'s <c>requests</c>; null while unknown.</summary>
    public static long? PanelOfRequest(string? json, long request) => Read<long>(json, result =>
    {
        if (!result.TryGetProperty("requests", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var r in list.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object && Long(r, "request") == request && r.TryGetProperty("result", out var res) && res.ValueKind == JsonValueKind.Object)
                return Long(res, "id");
        }

        return (long?)null;
    });

    /// <summary>The focused world panel from <c>focus.get</c>: its id (0: none) and whether it is a window panel. Null: no answer.</summary>
    public static (long Id, bool IsWindow)? Focus(string? json) => Read<(long, bool)>(json, result => Long(result, "id") is { } id
        ? (id, result.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String && k.GetString() == "window")
        : null);

    private static long? Long(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static T? Read<T>(string? json, Func<JsonElement, T?> read)
        where T : struct
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
                return null;
            return read(result);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

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
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return "reply is not JSON";
        }
    }
}
