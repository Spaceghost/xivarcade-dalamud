using System.Reflection;
using Dalamud.Plugin.Services;
using XivArcade.Core;
using XivArcade.Core.Arcade;

namespace XivArcade.Plugin;

/// <summary>
/// The plugin's half of /arcade. It owns no game or save logic: it drops the bundled host helper
/// (tools/xiv-arcade) next to the helper's own state, asks the host to run it, and reads the state.json the
/// helper writes. A game launch goes through ghostty-dalamud when it is there, because only a process started
/// inside the agent's compositor becomes a panel; without it, housekeeping runs through Wine and games only
/// when the player chose the host desktop. While the window is open it watches the games folder (directory
/// timestamps, off the framework thread) and asks for a rescan when something changed.
/// </summary>
public sealed class ArcadeService : IDisposable
{
    private const string HelperResource = "xiv-arcade";
    private const long StatePollMs = 1000;
    private const long FolderPollMs = 2500;
    private const long IdleRefreshMs = 20_000;

    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly Func<HostPaths> paths;
    private readonly HostLauncher host;
    private readonly Configuration config;

    private volatile ArcadeState state = ArcadeState.Empty;
    private DateTime stateStamp;
    private string folderSignature = "";
    private long lastStatePoll = long.MinValue / 2;
    private long lastFolderPoll = long.MinValue / 2;
    private long lastRefresh = long.MinValue / 2;
    private long fastUntil;
    private int busy;
    private bool helperReady;
    private string lastMessage = "";
    private string? configDir;
    private volatile bool playingAlive;

    public ArcadeService(IFramework framework, IPluginLog log, Func<HostPaths> paths, HostLauncher host, Configuration config)
    {
        this.framework = framework;
        this.log = log;
        this.paths = paths;
        this.host = host;
        this.config = config;
        framework.Update += OnUpdate;
    }

    public ArcadeState State => state;

    /// <summary>A check the plugin itself contributes to the helper's list (the gamepad hook); null adds none.</summary>
    public Func<ArcadeCheck?>? PluginCheck { get; set; }

    /// <summary>The helper says a game is running and its process is still there (/proc/PID), so a killed helper cannot leave this true.</summary>
    public bool Playing => state.PlayingTitle != null && playingAlive;

    /// <summary>The window.open request of the last game started as a panel; null when it went another way.</summary>
    public long? LastOpenRequest { get; private set; }

    /// <summary>The window is open: poll faster and watch the games folder.</summary>
    public bool Watching { get; set; }

    /// <summary>Raised on the framework thread when the helper reports something new (a refusal, a result).</summary>
    public event Action<string>? Message;

    /// <summary>ghostty-dalamud answers, so a game becomes a panel in the world.</summary>
    public bool Panels => host.Panels;

    /// <summary>A game can be started at all: as a panel, or on the Linux desktop because the player chose that.</summary>
    public bool CanPlay => host.Panels || config.PlayOnHostDesktop;

    /// <summary>Set when the last attempt to reach the host failed (no ghostty, and Wine could not start a Linux program).</summary>
    public string HostProblem { get; private set; } = "";

    /// <summary>Where the helper lives on the Linux host, beside its own state.</summary>
    public string HelperPath => ConfigDir + "/xiv-arcade";

    /// <summary>$XDG_CONFIG_HOME/xiv-arcade when Wine handed that variable down, else ~/.config/xiv-arcade; an existing default setup stays put.</summary>
    private string ConfigDir
    {
        get
        {
            if (configDir != null)
                return configDir;
            var p = paths();
            var dir = p.ConfigDir(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), File.Exists);
            return p.LinuxHome.Length == 0 ? dir : configDir = dir; // not remembered until the home directory is known
        }
    }

    public string Run(ArcadeRequest request)
    {
        switch (request.Verb)
        {
            case ArcadeVerb.Play:
                var game = ArcadeCommands.Pick(state.Games, request.Arg) ?? state.Game(request.Arg);
                if (game == null)
                    return state.Games.Count == 0 ? "error: the library is empty. " + ArcadeText.Legal : $"error: no game matches \"{request.Arg}\"";
                return Play(game);
            case ArcadeVerb.Last:
                return state.Game(state.Last) is { } last ? Play(last) : "error: nothing has been played yet";
            case ArcadeVerb.LaunchBox or ArcadeVerb.ImportSaves or ArcadeVerb.DryRun when request.Arg.Length == 0:
                return "error: that needs a folder or a name after it";
            default:
                return ArcadeCommands.HelperCall(request) is { } call ? Helper(call.Verb, call.Args, launch: false) : "error: unknown arcade command";
        }
    }

    public string Play(ArcadeGame game)
    {
        if (!CanPlay)
            return "error: " + HostLauncher.GhosttyMissing + " Install it, or turn on \"Play on the Linux desktop instead\" in the Arcade window.";
        if (!game.Ready)
            return $"error: no emulator core for {state.SystemName(game.System)} yet. " + (state.Systems.FirstOrDefault(s => s.Id == game.System)?.Hint ?? "");
        var result = Helper("launch", [game.Id], launch: true);
        return result.StartsWith("ok", StringComparison.Ordinal)
            ? $"ok: {game.Title} · checking saves, then starting" + (state.Sync.CanLaunch ? "" : " (waiting for saves to finish syncing)")
            : result;
    }

    /// <summary>Hands a helper command to the host. "ok" means it was handed over; the outcome arrives in state.json.</summary>
    private string Helper(string verb, string[] args, bool launch)
    {
        if (paths().LinuxHome.Length == 0)
            return "error: the Linux home directory is unknown (set it in /desktop settings)";
        try
        {
            EnsureHelper();
            if (host.Panels)
            {
                var line = ArcadeCommands.Line(HelperPath, verb, args);
                if (line == null)
                    return "error: that name or path cannot be passed to the host";
                var request = host.RunInCompositor(line);
                if (launch)
                    LastOpenRequest = request;
            }
            else if (!launch || config.PlayOnHostDesktop)
            {
                if (args.Any(a => a.Contains('\n') || a.Contains('\r')))
                    return "error: that name or path cannot be passed to the host";
                HostLauncher.RunOnHostDesktop(ArcadeCommands.Argv(HelperPath, verb, args));
            }
            else
            {
                return "error: " + HostLauncher.GhosttyMissing;
            }

            HostProblem = "";
            lastStatePoll = long.MinValue / 2;
            fastUntil = Environment.TickCount64 + 45_000; // the answer lands in state.json; look often until it has
            return "ok";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "XivArcade: {Verb} failed", verb);
            HostProblem = host.Panels ? ex.Message : "The Linux host could not be reached: " + ex.Message;
            return "error: " + HostProblem;
        }
    }

    /// <summary>Writes the bundled helper to the host when it is missing or differs from this build's copy.</summary>
    private void EnsureHelper()
    {
        if (helperReady)
            return;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(HelperResource)
            ?? throw new InvalidOperationException("the arcade helper is missing from this build");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var local = paths().ToLocal(HelperPath);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        if (!File.Exists(local) || !File.ReadAllBytes(local).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(local, bytes);
        helperReady = true;
    }

    /// <summary>Asks the helper to look again (setup is idempotent: folders, READMEs, scan, state).</summary>
    public string Refresh()
    {
        lastRefresh = Environment.TickCount64;
        return Helper("scan", [], launch: false);
    }

    private void OnUpdate(IFramework fw)
    {
        var now = Environment.TickCount64;
        if (Watching && now - lastRefresh > IdleRefreshMs)
            Refresh();
        if (now - lastStatePoll < (Watching || now < fastUntil || state.PlayingTitle != null ? StatePollMs : IdleRefreshMs))
            return;
        lastStatePoll = now;
        var watchFolder = Watching && now - lastFolderPoll >= FolderPollMs;
        if (watchFolder)
            lastFolderPoll = now;
        if (Interlocked.Exchange(ref busy, 1) == 1)
            return;
        _ = Task.Run(() => Poll(watchFolder));
    }

    private void Poll(bool watchFolder)
    {
        try
        {
            var p = paths();
            if (p.LinuxHome.Length == 0)
                return;
            var file = p.ToLocal(ConfigDir + "/state.json");
            if (File.Exists(file))
            {
                var stamp = File.GetLastWriteTimeUtc(file);
                if (stamp != stateStamp)
                {
                    stateStamp = stamp;
                    var next = ArcadeState.Parse(File.ReadAllText(file));
                    if (next.Loaded)
                    {
                        if (PluginCheck?.Invoke() is { } mine)
                            next = next with { Checks = [.. next.Checks, mine] };
                        state = next;
                        if (next.Message.Length > 0 && next.Message != lastMessage)
                            _ = framework.RunOnFrameworkThread(() => Message?.Invoke(next.Message));
                        lastMessage = next.Message;
                    }
                }
            }

            var playing = state;
            playingAlive = playing.PlayingTitle != null && (playing.PlayingPid == 0 || Directory.Exists(p.ToLocal("/proc/" + playing.PlayingPid)));

            if (!watchFolder || state.GamesRoot.Length == 0)
                return;

            // the BIOS folders too: a dump dropped in turns its check green without anyone pressing Rescan
            var signature = FolderSignature(p.ToLocal(state.GamesRoot)) + string.Concat(state.Watch.Select(d => "|" + FolderSignature(p.ToLocal(d))));
            var changed = folderSignature.Length > 0 && signature != folderSignature;
            folderSignature = signature;
            if (changed)
                _ = framework.RunOnFrameworkThread(() => Refresh());
        }
        catch (Exception ex)
        {
            log.Debug(ex, "XivArcade: poll failed");
        }
        finally
        {
            Interlocked.Exchange(ref busy, 0);
        }
    }

    /// <summary>Directory timestamps three levels deep: adding or removing a file changes its folder's.</summary>
    private static string FolderSignature(string root)
    {
        if (!Directory.Exists(root))
            return "missing";
        long ticks = 0;
        var count = 0;
        var level = new List<string> { root };
        for (var depth = 0; depth < 3 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var dir in level)
            {
                ticks = unchecked((ticks * 31) + Directory.GetLastWriteTimeUtc(dir).Ticks);
                count++;
                if (count > 400)
                    break;
                try
                {
                    next.AddRange(Directory.EnumerateDirectories(dir));
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            level = next;
        }

        return $"{count}:{ticks}";
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
    }
}
