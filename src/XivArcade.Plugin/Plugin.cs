using Dalamud.Game.Command;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using XivArcade.Core;
using XivArcade.Core.Arcade;
using XivArcade.Shared;

namespace XivArcade.Plugin;

/// <summary>
/// /arcade: the player's own classic games, started on the Linux host and shown as game panels, with saves
/// kept in step across machines. Standalone: ghostty-dalamud is found over IPC when it is installed, and
/// XivArcade.v1.Search / Launch let a launcher such as XivDesktop list the games without a reference.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    public const string Command = "/arcade";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly WindowSystem windowSystem = new("XivArcade");
    private readonly Configuration config = null!;
    private readonly ArcadeService arcade = null!;
    private readonly ArcadeWindow window = null!;
    private readonly PadCaptureService pad = null!;
    private (string Root, bool Derived)? wineRoot;
    private readonly IFontHandle? coverFont;
    private readonly ICallGateProvider<string, string>? search;
    private readonly ICallGateProvider<string, string>? launch;
    private bool commandRegistered;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog log, IFramework framework, ICommandManager commands, IChatGui chat, ITextureProvider textures,
        IGameInteropProvider interop, ICondition condition, IClientState clientState, IKeyState keys, IGamepadState gamepad)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;

        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            var host = new HostLauncher(pluginInterface);
            arcade = new ArcadeService(framework, log, Paths, host, config);
            arcade.Message += OnArcadeMessage;
            pad = new PadCaptureService(framework, interop, condition, clientState, keys, log, arcade, host, config);
            pad.Changed += OnArcadeMessage;
            arcade.PluginCheck = () => pad.Check;
            try
            {
                // the drawn covers set their titles large; without this font they fall back to the game's UI font
                coverFont = pluginInterface.UiBuilder.FontAtlas.NewDelegateFontHandle(e => e.OnPreBuild(tk => tk.AddDalamudDefaultFont(40f)));
            }
            catch (Exception ex)
            {
                log.Warning(ex, "XivArcade: the cover title font is unavailable; using the default font");
            }

            window = new ArcadeWindow(arcade, textures, gamepad, coverFont, Paths, config, () => pluginInterface.SavePluginConfig(config));
            windowSystem.AddWindow(window);
            pluginInterface.UiBuilder.Draw += DrawUi;
            pluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
            pluginInterface.UiBuilder.OpenConfigUi += OpenConfigUi;

            commandRegistered = commands.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Your own classic games as game panels, saves kept in step across machines. " + ArcadeText.Help,
            });

            search = pluginInterface.GetIpcProvider<string, string>(IpcContract.Search);
            search.RegisterFunc(query => ArcadeIpc.SearchJson(arcade.State, query, arcade.CanPlay));
            launch = pluginInterface.GetIpcProvider<string, string>(IpcContract.Launch);
            launch.RegisterFunc(LaunchById);
        }
        catch
        {
            // Dalamud does not call Dispose when the constructor throws; release what was acquired.
            DisposeCore();
            throw;
        }
    }

    private HostPaths Paths()
    {
        // What Wine calls the Linux root is looked for once (the override, a drive that really holds /proc and /etc, \\?\unix), not assumed to be Z:.
        var root = OperatingSystem.IsWindows() ? (wineRoot ??= HostPaths.DetectWineRoot(Directory.Exists, config.WineRootOverride)).Root : "";
        var home = config.HomeOverride;
        if (string.IsNullOrWhiteSpace(home))
            home = HostPaths.GuessHome(Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("WINEHOMEDIR"), pluginInterface.ConfigDirectory.FullName, root.Length == 2 ? root : HostPaths.DefaultWineRoot);
        return OperatingSystem.IsWindows() ? HostPaths.Wine(home, root) : HostPaths.Native(home);
    }

    private string LaunchById(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            window.Show();
            return "ok";
        }

        return arcade.State.Game(id.Trim()) is { } game ? arcade.Play(game) : "error: that game is not in the library";
    }

    private void DrawUi()
    {
        try
        {
            windowSystem.Draw();
            pad.Draw();

            // "Always the in-game cursor": keep Dalamud from swapping it while over our window.
            GameCursor.Update("windows", window.IsOpen && window.IsHovered);
        }
        catch (Exception ex)
        {
            log.Error(ex, "XivArcade UI draw failed");
        }
    }

    private void OpenMainUi() => window.Show();

    private void OpenConfigUi() => window.ShowSetup();

    private void OnCommand(string command, string arguments)
    {
        try
        {
            var request = ArcadeRequest.Parse(arguments);
            switch (request.Verb)
            {
                case ArcadeVerb.Open:
                    window.Show();
                    return;
                case ArcadeVerb.Help:
                    Print(ArcadeText.Help + " " + ArcadeText.Legal);
                    return;
                case ArcadeVerb.Setup:
                    window.ShowSetup();
                    break;
                case ArcadeVerb.List:
                    PrintList();
                    return;
                case ArcadeVerb.Pad:
                    Print(pad.Command(request.Arg)[4..]);
                    pluginInterface.SavePluginConfig(config);
                    return;
                case ArcadeVerb.Bios or ArcadeVerb.Paths:
                case ArcadeVerb.Emulator when request.Arg.Length == 0:
                    PrintSystems(request.Verb);
                    break;
            }

            var result = arcade.Run(request);
            Print(result == "ok" ? request.Verb switch
            {
                ArcadeVerb.Sync => "syncing saves… " + arcade.State.Sync.Label,
                ArcadeVerb.Setup => "checking folders, emulator and games…",
                ArcadeVerb.Rescan => "rescanning your games folder…",
                ArcadeVerb.Bios or ArcadeVerb.Paths => "looking again…",
                ArcadeVerb.Art => request.Arg switch
                {
                    "on" => "cover art on: fetching covers for the games in your library. " + ArcadeText.ArtNotice,
                    "off" => "cover art off: nothing is contacted. Covers already fetched stay on disk.",
                    "refresh" => "asking again for the covers that had no match…",
                    _ => (arcade.State.Art.Enabled ? $"cover art is on · {arcade.State.Art.Fetched} fetched, {arcade.State.Art.Without} games without a cover. " : "cover art is off (/arcade art on). ") + ArcadeText.ArtNotice,
                },
                ArcadeVerb.Emulator or ArcadeVerb.GamesFolder or ArcadeVerb.SavesFolder => "asked the host; the answer follows",
                _ => "asked the host; the result follows",
            }
            : result);
            if (request.Verb == ArcadeVerb.Setup)
            {
                foreach (var c in arcade.State.Checks)
                    Print($"[{(c.Ok ? "x" : " ")}] {c.Title}: {c.Detail}");
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "{Command} failed", Command);
        }
    }

    private void PrintList()
    {
        var state = arcade.State;
        if (state.Games.Count == 0)
        {
            Print("no games yet. Drop your own game files into " + (state.GamesRoot.Length > 0 ? state.GamesRoot : "your games folder") + ". " + ArcadeText.Legal);
            return;
        }

        foreach (var group in state.Games.GroupBy(g => g.System))
            Print(state.SystemName(group.Key) + ": " + string.Join("; ", group.Select(g => g.Title + (g.Ready ? "" : " (core missing)"))));
        Print("saves: " + state.Sync.Label);
    }

    /// <summary>What the helper last reported, from the state already in memory; a fresh look is asked for right after.</summary>
    private void PrintSystems(ArcadeVerb verb)
    {
        var state = arcade.State;
        if (verb == ArcadeVerb.Paths)
        {
            Print($"games: {state.GamesRoot} · saves: {state.SyncRoot} · Linux root in Wine: {wineRoot?.Root ?? "(native)"}{(wineRoot is { Derived: false } ? " (assumed; set WineRootOverride if wrong)" : "")}");
            foreach (var note in state.Notes)
                Print(note);
            return;
        }

        foreach (var s in state.Systems)
        {
            if (verb == ArcadeVerb.Emulator && s.Emulators.Count > 0)
                Print($"{s.Name} ({s.Id}): {(s.Emulator.Length > 0 ? s.Emulator : "none installed")} · choices: auto, " + string.Join(", ", s.Emulators.Select(e => e.Id + (e.Installed ? "" : " (not installed)"))));
            if (verb == ArcadeVerb.Bios && s.Bios is { } bios)
            {
                Print($"{s.Name}: {bios.Summary}");
                foreach (var f in bios.Files.Where(f => f.State != "missing" || bios.Required))
                    Print($"  [{(f.State == "ok" ? "x" : f.State == "wrong" ? "!" : " ")}] {f.Name} · {f.Size:N0} bytes{(f.Md5.Length > 0 ? " · MD5 " + f.Md5 : "")}{(f.Detail.Length > 0 ? " · " + f.Detail : "")}");
            }
        }
    }

    private void OnArcadeMessage(string message)
    {
        Print(message);
        window.Flash(message);
    }

    private void Print(string message)
    {
        _ = framework.RunOnFrameworkThread(() =>
        {
            try
            {
                chat.Print(message, "XivArcade");
            }
            catch
            {
                // Chat unavailable (e.g. title screen).
            }
        });
    }

    public void Dispose() => DisposeCore();

    private void DisposeCore()
    {
        if (commandRegistered)
        {
            commands.RemoveHandler(Command);
            commandRegistered = false;
        }

        search?.UnregisterFunc();
        launch?.UnregisterFunc();
        pluginInterface.UiBuilder.Draw -= DrawUi;
        pluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfigUi;
        windowSystem.RemoveAllWindows();
        window?.Dispose(); // every cover texture
        coverFont?.Dispose();
        GameCursor.Release();
        if (pad != null)
        {
            // first: the gamepad goes back to FFXIV and the hook comes off before anything else is torn down
            pad.Changed -= OnArcadeMessage;
            pad.Dispose();
        }

        if (arcade != null)
        {
            arcade.PluginCheck = null;
            arcade.Message -= OnArcadeMessage;
            arcade.Dispose();
        }
    }
}
