using System;
using System.Text.Json;

namespace XivArcade.Core.Arcade;

/// <summary>One first-run step: what it is, whether it is true yet, and the one command that makes it true.</summary>
public sealed record ArcadeCheck(string Key, string Title, bool Ok, string Detail, string Command);

/// <summary>A pairing step for save sync: what to do and the command to copy. Nothing is run for the player.</summary>
public sealed record ArcadeStep(string Text, string Command);

/// <summary>Save sync as the host helper last saw it.</summary>
public sealed record ArcadeSync(string Code, string Label, string Detail, bool CanLaunch, IReadOnlyList<ArcadeStep> Steps, IReadOnlyList<string> Conflicts)
{
    public static readonly ArcadeSync Unknown = new("unknown", "Checking…", "", true, [], []);

    /// <summary>ok: nothing to do; busy: data is moving; attention: the player should look.</summary>
    public string Tone => Code switch
    {
        "in-sync" or "no-peers" => "ok",
        "pulling" or "pushing" or "unknown" => "busy",
        _ => "attention",
    };
}

/// <summary>A placeholder on the Final Fantasy shelf: text only. It lights up when <see cref="Games"/> is not empty.</summary>
public sealed record ArcadeShelfEntry(string Key, string Title, int Year, string Platforms, IReadOnlyList<string> Games);

public sealed record ArcadeGame(string Id, string Title, int? Year, string System, string Path, int Discs, IReadOnlyList<string> Ff, string? Boxart, double? LastPlayed, bool Ready);

public sealed record ArcadeSystem(string Id, string Name, string Folder, IReadOnlyList<string> Extensions, string? Core, string Hint, string Note)
{
    /// <summary>The emulator that will run this console ("RetroArch · swanstation", "PCSX2 (standalone)"); empty when none is installed.</summary>
    public string Emulator { get; init; } = "";

    /// <summary>The player's choice: "auto", "retroarch", "retroarch:core" or a standalone emulator's id.</summary>
    public string Preferred { get; init; } = "auto";

    /// <summary>Every emulator the helper can start for this console, installed or not.</summary>
    public IReadOnlyList<ArcadeEmulator> Emulators { get; init; } = [];

    /// <summary>What the chosen emulator wants as a BIOS and what is there; null when this console needs none.</summary>
    public ArcadeBios? Bios { get; init; }
}

public sealed record ArcadeEmulator(string Id, string Label, bool Installed, bool BiosFree);

/// <summary>One BIOS file the emulator reads: its exact name, size and MD5, and whether the player's file is that dump.</summary>
public sealed record ArcadeBiosFile(string Name, string What, long Size, string Md5, string State, string Detail);

/// <summary>The BIOS situation of one console. XivArcade names the file and the folder; it never fetches or locates one.</summary>
public sealed record ArcadeBios(string Dir, bool Required, bool Ok, bool Hle, string Summary, IReadOnlyList<ArcadeBiosFile> Files);

/// <summary>
/// Opt-in cover art. Off until the player turns it on; when on, the helper asks one host (RetroArch's public
/// thumbnail set) for pictures of games that are already in the library, sending the console's and the game's
/// name and nothing else. The plugin itself never opens a connection: it only reads this.
/// </summary>
public sealed record ArcadeArt(bool Enabled, bool Running, string Host, string Notice, string Folder, int Fetched, int Without, int Waiting)
{
    public static readonly ArcadeArt Off = new(false, false, "", "", "", 0, 0, 0);
}

/// <summary>
/// What tools/xiv-arcade wrote to state.json: the first-run checks, the save sync verdict, the shelf, and the
/// library. The plugin only ever reads this; every decision about games and saves is the helper's.
/// </summary>
public sealed record ArcadeState
{
    public static readonly ArcadeState Empty = new();

    public double At { get; init; }

    public string Legal { get; init; } = ArcadeText.Legal;

    public string Message { get; init; } = "";

    public string? PlayingTitle { get; init; }

    /// <summary>The helper process that is waiting on the emulator; 0 when unknown. /proc/PID gone means the game ended.</summary>
    public int PlayingPid { get; init; }

    /// <summary>Folders whose changes should trigger a rescan besides the games folder (the BIOS folders).</summary>
    public IReadOnlyList<string> Watch { get; init; } = [];

    /// <summary>Things the helper wants the player to know about its directories (a setup kept where it was).</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    public string GamesRoot { get; init; } = "";

    public string SyncRoot { get; init; } = "";

    public string LaunchBoxText { get; init; } = "";

    public string LaunchBoxCommand { get; init; } = "";

    public IReadOnlyList<string> LaunchBoxFound { get; init; } = [];

    public IReadOnlyList<ArcadeCheck> Checks { get; init; } = [];

    public ArcadeSync Sync { get; init; } = ArcadeSync.Unknown;

    public ArcadeArt Art { get; init; } = ArcadeArt.Off;

    public IReadOnlyList<ArcadeShelfEntry> Shelf { get; init; } = [];

    public IReadOnlyList<ArcadeGame> Games { get; init; } = [];

    public IReadOnlyList<ArcadeSystem> Systems { get; init; } = [];

    public string? Last { get; init; }

    public bool Loaded => At > 0;

    public bool SetupDone => Checks.Count > 0 && Checks.All(c => c.Ok);

    public ArcadeGame? Game(string? id) => id == null ? null : Games.FirstOrDefault(g => g.Id == id);

    public string SystemName(string id) => Systems.FirstOrDefault(s => s.Id == id)?.Name ?? id;

    /// <summary>Games no shelf entry claims, grouped by system in the helper's system order.</summary>
    public IEnumerable<(ArcadeSystem System, List<ArcadeGame> Games)> EverythingElse()
    {
        foreach (var s in Systems)
        {
            var games = Games.Where(g => g.System == s.Id && g.Ff.Count == 0).ToList();
            if (games.Count > 0)
                yield return (s, games);
        }
    }

    /// <summary>Parses state.json; anything missing or malformed reads as empty rather than throwing.</summary>
    public static ArcadeState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                return Empty;
            var checks = List(r, "checks", c => new ArcadeCheck(Str(c, "key"), Str(c, "title"), Bool(c, "ok"), Str(c, "detail"), Str(c, "command")));
            var lb = r.TryGetProperty("checks", out var cs) && cs.ValueKind == JsonValueKind.Array
                ? cs.EnumerateArray().Select(c => c.TryGetProperty("launchbox", out var l) ? l : default).FirstOrDefault(l => l.ValueKind == JsonValueKind.Object)
                : default;
            var sync = r.TryGetProperty("sync", out var s) && s.ValueKind == JsonValueKind.Object
                ? new ArcadeSync(Str(s, "code"), Str(s, "label"), Str(s, "detail"), !s.TryGetProperty("can_launch", out var cl) || cl.ValueKind != JsonValueKind.False,
                    List(s, "steps", x => new ArcadeStep(Str(x, "text"), Str(x, "command"))),
                    List(s, "conflicts", x => System.IO.Path.GetFileName(Str(x, "kept_as"))))
                : ArcadeSync.Unknown;
            return new ArcadeState
            {
                At = Num(r, "at") ?? 0,
                Legal = Str(r, "legal") is { Length: > 0 } legal ? legal : ArcadeText.Legal,
                Message = Str(r, "message"),
                PlayingTitle = r.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.Object ? Str(p, "title") : null,
                PlayingPid = p.ValueKind == JsonValueKind.Object && Num(p, "pid") is > 0 and < int.MaxValue and var pid ? (int)pid : 0,
                Watch = Strings(r, "watch"),
                Notes = Strings(r, "notes"),
                GamesRoot = Str(r, "games_root"),
                SyncRoot = Str(r, "sync_root"),
                LaunchBoxText = lb.ValueKind == JsonValueKind.Object ? Str(lb, "text") : "",
                LaunchBoxCommand = lb.ValueKind == JsonValueKind.Object ? Str(lb, "command") : "",
                LaunchBoxFound = lb.ValueKind == JsonValueKind.Object ? Strings(lb, "found") : [],
                Checks = checks,
                Sync = sync,
                Art = r.TryGetProperty("art", out var art) && art.ValueKind == JsonValueKind.Object
                    ? new ArcadeArt(Bool(art, "enabled"), Bool(art, "running"), Str(art, "host"), Str(art, "notice"), Str(art, "folder"),
                        (int)(Num(art, "fetched") ?? 0), (int)(Num(art, "without") ?? 0), (int)(Num(art, "waiting") ?? 0))
                    : ArcadeArt.Off,
                Shelf = List(r, "shelf", e => new ArcadeShelfEntry(Str(e, "key"), Str(e, "title"), (int)(Num(e, "year") ?? 0), Str(e, "platforms"), Strings(e, "games"))),
                Games = List(r, "games", g => new ArcadeGame(Str(g, "id"), Str(g, "title"), (int?)Num(g, "year"), Str(g, "system"), Str(g, "path"),
                    (int)(Num(g, "discs") ?? 0), Strings(g, "ff"), NullStr(g, "boxart"), Num(g, "last_played"), Bool(g, "ready"))),
                Systems = List(r, "systems", x => new ArcadeSystem(Str(x, "id"), Str(x, "name"), Str(x, "folder"), Strings(x, "exts"), NullStr(x, "core"), Str(x, "hint"), Str(x, "note"))
                {
                    Emulator = Str(x, "emulator"),
                    Preferred = Str(x, "preferred") is { Length: > 0 } pref ? pref : "auto",
                    Emulators = List(x, "emulators", o => new ArcadeEmulator(Str(o, "id"), Str(o, "label"), Bool(o, "installed"), Bool(o, "bios_free"))),
                    Bios = x.ValueKind == JsonValueKind.Object && x.TryGetProperty("bios", out var b) && b.ValueKind == JsonValueKind.Object
                        ? new ArcadeBios(Str(b, "dir"), Bool(b, "required"), Bool(b, "ok"), Bool(b, "hle"), Str(b, "summary"),
                            List(b, "files", f => new ArcadeBiosFile(Str(f, "name"), Str(f, "what"), (long)(Num(f, "size") ?? 0), Str(f, "md5"), Str(f, "state"), Str(f, "detail"))))
                        : null,
                }),
                Last = NullStr(r, "last"),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Empty;
        }
    }

    private static string Str(JsonElement e, string name) => NullStr(e, name) ?? "";

    private static string? NullStr(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static List<string> Strings(JsonElement e, string name) => List(e, name, x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "");

    private static List<T> List<T>(JsonElement e, string name, Func<JsonElement, T> map)
    {
        var result = new List<T>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
                result.Add(map(item));
        }

        return result;
    }
}

/// <summary>Words the window, the chat output and the docs all use.</summary>
public static class ArcadeText
{
    public const string Legal = "XivArcade never downloads, links to or helps find ROMs, disc images or BIOS files. It only reads folders you point it at. Use your own dumps of games you own.";

    /// <summary>Shown wherever cover art can be turned on, before anything is contacted.</summary>
    public const string ArtNotice = "Off by default. When on, the helper on your Linux host asks thumbnails.libretro.com (RetroArch's own public thumbnail set) over HTTPS "
        + "for covers of the games that are already in your library. Only the console's name and the game's name are sent: no files, no hashes, no paths. "
        + "Artwork belongs to its owners and is fetched only to be shown in your own library. Your own pictures always win, and games, disc images and BIOS files are never fetched.";

    public const string Help = "/arcade opens the library; /arcade <name> plays the closest match; /arcade last resumes; /arcade list; /arcade sync; /arcade setup; "
        + "/arcade rescan; /arcade art [on|off|refresh]; /arcade launchbox <folder>; /arcade import-saves <folder>; /arcade launch --dry-run <name or file>; "
        + "/arcade pad [on|off|auto] (the gamepad belongs to the arcade game while you play; hold Start+Select or press Esc to take it back); "
        + "/arcade emulator [console|game choice]; /arcade bios; /arcade paths; /arcade games-folder <dir>; /arcade saves-folder <dir>.";
}
