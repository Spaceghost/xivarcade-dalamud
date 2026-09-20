using Dalamud.Game.Command;
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
    private readonly ICallGateProvider<string, string>? search;
    private readonly ICallGateProvider<string, string>? launch;
    private bool commandRegistered;

    public Plugin(IDalamudPluginInterface pluginInterface, IPluginLog log, IFramework framework, ICommandManager commands, IChatGui chat, ITextureProvider textures)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;

        try
        {
            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            arcade = new ArcadeService(framework, log, Paths, new HostLauncher(pluginInterface), config);
            arcade.Message += OnArcadeMessage;
            window = new ArcadeWindow(arcade, textures, Paths, config, () => pluginInterface.SavePluginConfig(config));
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
        var home = config.HomeOverride;
        if (string.IsNullOrWhiteSpace(home))
            home = HostPaths.GuessHome(Environment.GetEnvironmentVariable("HOME"), Environment.GetEnvironmentVariable("WINEHOMEDIR"), pluginInterface.ConfigDirectory.FullName);
        return OperatingSystem.IsWindows() ? HostPaths.Wine(home) : HostPaths.Native(home);
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
            }

            var result = arcade.Run(request);
            Print(result == "ok" ? request.Verb switch
            {
                ArcadeVerb.Sync => "syncing saves… " + arcade.State.Sync.Label,
                ArcadeVerb.Setup => "checking folders, emulator and games…",
                ArcadeVerb.Rescan => "rescanning your games folder…",
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
        GameCursor.Release();
        if (arcade != null)
        {
            arcade.Message -= OnArcadeMessage;
            arcade.Dispose();
        }
    }
}
