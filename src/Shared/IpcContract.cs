namespace XivArcade.Shared;

/// <summary>
/// Dalamud IPC names: the two ghostty-dalamud gates XivArcade calls to start something inside the host
/// agent's compositor, and the two functions XivArcade publishes so another plugin (a launcher, a toolbar)
/// can find and start the player's games without referencing this assembly.
/// </summary>
public static class IpcContract
{
    /// <summary>string request → string reply (JSON). ghostty-dalamud's Call gate; <c>window.open {"run": cmd}</c>.</summary>
    public const string GhosttyCall = "GhosttyDalamud.v1.Call";

    /// <summary>string line → nothing. ghostty-dalamud's older Post gate; "window pull run CMD".</summary>
    public const string GhosttyPost = "GhosttyDalamud.v1.Post";

    /// <summary>
    /// string query → string JSON: <c>[{"id","title","subtitle","ready"}]</c>, best match first, at most 20. An
    /// empty query returns nothing: a caller's empty search box stays its own.
    /// </summary>
    public const string Search = "XivArcade.v1.Search";

    /// <summary>string game id (from Search) → "ok: …" or "error: …". An empty id opens the Arcade window.</summary>
    public const string Launch = "XivArcade.v1.Launch";
}
