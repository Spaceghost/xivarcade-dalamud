using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Interface.Colors;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using XivArcade.Core;
using XivArcade.Core.Arcade;

namespace XivArcade.Plugin;

/// <summary>
/// The Arcade: a wall of the player's own box art in the shared "spaceghost" look (dark ink, a gold accent,
/// glass panels). Covers are grouped by shelf, Final Fantasy first and then by console; the selected one grows
/// a little and glows, the rest dim a touch. A game with no picture gets a drawn cover in its console's box
/// shape. Pictures come only from the player's own files, are loaded in the background as thumbnails and are
/// all released when the plugin unloads. Arrows or the D-pad move, Enter or confirm plays, typing searches,
/// Tab changes shelf, F5 rescans, F6 syncs saves, Esc closes.
/// </summary>
public sealed class ArcadeWindow : Window, IDisposable
{
    private const int ThumbnailSide = 384;
    private const int CachedCovers = 256; // more than fit on screen at once, so the wall never evicts what it is showing
    private const long LargestPicture = 48L * 1024 * 1024;

    private static readonly Vector4 Accent = new(0.96f, 0.78f, 0.36f, 1f); // gold
    private static readonly Vector4 Ink = new(0.043f, 0.047f, 0.066f, 0.95f);
    private static readonly Vector4 Good = new(0.45f, 0.86f, 0.58f, 1f);
    private static readonly Vector4 Busy = new(0.45f, 0.70f, 1.00f, 1f);
    private static readonly Vector4 Attention = new(1.00f, 0.60f, 0.35f, 1f);

    private readonly ArcadeService arcade;
    private readonly ITextureProvider textures;
    private readonly IGamepadState gamepad;
    private readonly IFontHandle? titleFont;
    private readonly Func<HostPaths> paths;
    private readonly Configuration config;
    private readonly Action save;
    private readonly CoverCache<IDalamudTextureWrap> covers;

    private readonly List<Tile> tiles = [];
    private readonly List<Section> sections = [];
    private readonly List<int> counts = [];
    private readonly CoverGrid grid = new();
    private ArcadeState? builtFor;
    private string builtSearch = "\0";
    private string search = "";
    private string launchBoxPath = "";
    private int selected;
    private bool focusSearch;
    private bool scrollToSelected;
    private bool scrolling;
    private float scrollTarget;
    private bool showSync;
    private string? flash;
    private DateTime flashUntil;
    private int pushedColors;
    private int pushedVars;

    public ArcadeWindow(ArcadeService arcade, ITextureProvider textures, IGamepadState gamepad, IFontHandle? titleFont, Func<HostPaths> paths, Configuration config, Action save)
        : base("Arcade###XivArcade")
    {
        this.config = config;
        this.save = save;
        this.arcade = arcade;
        this.textures = textures;
        this.gamepad = gamepad;
        this.titleFont = titleFont;
        this.paths = paths;
        covers = new CoverCache<IDalamudTextureWrap>(CachedCovers, 3, LoadCover);
        Size = new Vector2(900, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(460, 380), MaximumSize = new Vector2(float.MaxValue, float.MaxValue) };
    }

    private sealed class Tile(string key, string title, string line, ArcadeGame? game, string missing, string system, int? year, string? art)
    {
        public string Key { get; } = key;

        public string Title { get; } = title;

        public string Line { get; } = line;

        public ArcadeGame? Game { get; } = game;

        public string Missing { get; } = missing;

        public string System { get; } = system;

        public int? Year { get; } = year;

        /// <summary>The picture's path as this process sees it, or null for a drawn cover.</summary>
        public string? Art { get; } = art;

        public CoverStyle Style { get; } = CoverLayout.Style(system);

        public float Focus { get; set; }

        public float ArtFade { get; set; }

        public CoverTitle? Fit { get; set; }

        public float FitWidth { get; set; }

        public string? Label { get; set; }

        public float LabelWidth { get; set; }
    }

    private sealed record Section(string Name, string Detail);

    /// <summary>Every picture is released here; nothing the texture provider handed out outlives the plugin.</summary>
    public void Dispose() => covers.Dispose();

    public void Show(string initialSearch = "")
    {
        search = initialSearch;
        IsOpen = true;
        BringToFront();
    }

    public void ShowSetup()
    {
        showSync = true;
        Show();
    }

    public override void OnOpen()
    {
        focusSearch = true;
        selected = 0;
        scrollToSelected = true;
        arcade.Watching = true;
        Flash(arcade.Refresh());
    }

    public override void OnClose() => arcade.Watching = false;

    public void Flash(string text)
    {
        if (text == "ok")
            return;
        flash = text;
        flashUntil = DateTime.UtcNow.AddSeconds(8);
    }

    public override void PreDraw()
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 16 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 16) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ChildRounding, 12 * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8) * scale);
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 6 * scale);
        pushedVars = 6;
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Ink);
        ImGui.PushStyleColor(ImGuiCol.Border, Accent with { W = 0.28f });
        ImGui.PushStyleColor(ImGuiCol.FrameBg, new Vector4(1, 1, 1, 0.06f));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.ScrollbarGrab, Accent with { W = 0.30f });
        ImGui.PushStyleColor(ImGuiCol.Button, Accent with { W = 0.16f });
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Accent with { W = 0.30f });
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Accent with { W = 0.42f });
        ImGui.PushStyleColor(ImGuiCol.NavHighlight, Vector4.Zero);
        pushedColors = 10;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(pushedColors);
        ImGui.PopStyleVar(pushedVars);
        pushedColors = pushedVars = 0;
    }

    public override void Draw()
    {
        var state = arcade.State;
        var scale = ImGuiHelpers.GlobalScale;
        var dt = Math.Clamp(ImGui.GetIO().DeltaTime, 0f, 0.1f);

        var searchActive = DrawHeader(state, scale);
        BuildTiles(state);
        DrawShelfTabs(scale);
        HandleKeys(searchActive);

        var line = ImGui.GetTextLineHeightWithSpacing();
        var details = (line * 3) + (26 * scale);
        var footer = details + line + (ImGui.GetStyle().ItemSpacing.Y * 2);
        if (ImGui.BeginChild("##arcade-body", new Vector2(0, -footer), false))
        {
            DrawHostNotice();
            if (search.Length == 0)
            {
                if (!state.SetupDone)
                    DrawWelcome(state, scale);
                if (showSync || (state.SetupDone && state.Sync.Steps.Count > 0))
                    DrawSync(state, scale);
            }

            DrawGrid(scale, dt);
        }

        ImGui.EndChild();
        DrawDetails(state, scale, details);
        DrawFooter(state);
    }

    /// <summary>The search box and the save-sync pill. Returns whether the search box has the keyboard.</summary>
    private bool DrawHeader(ArcadeState state, float scale)
    {
        var sync = state.Sync;
        var pill = "●  " + (state.PlayingTitle is { } playing ? "Playing " + playing : sync.Label);
        var pillWidth = ImGui.CalcTextSize(pill).X + (24 * scale);
        if (focusSearch)
        {
            ImGui.SetKeyboardFocusHere();
            focusSearch = false;
        }

        ImGui.SetNextItemWidth(-(pillWidth + (8 * scale)));
        var before = search;
        if (ImGui.InputTextWithHint("##arcade-search", "Search your games…   Enter plays · Tab changes shelf · F5 rescans · F6 syncs saves", ref search, 128, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            focusSearch = true;
            Activate();
        }

        if (before != search)
        {
            selected = 0;
            scrollToSelected = true;
        }

        var active = ImGui.IsItemActive();
        ImGui.SameLine();
        ImGui.PushStyleColor(ImGuiCol.Text, Tone(sync));
        if (ImGui.Button(pill + "##sync", new Vector2(pillWidth, 0)))
            showSync = !showSync;
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted("Save sync: " + sync.Label);
            if (sync.Detail.Length > 0)
                ImGui.TextDisabled(sync.Detail);
            ImGui.TextDisabled("Click for details. F6 syncs now. A game never starts while its saves are still arriving.");
            ImGui.EndTooltip();
        }

        return active;
    }

    /// <summary>Rebuilt only when the helper wrote a new state or the search changed: nothing here runs per frame.</summary>
    private void BuildTiles(ArcadeState state)
    {
        if (ReferenceEquals(state, builtFor) && search == builtSearch)
            return;
        var keep = search == builtSearch && selected >= 0 && selected < tiles.Count ? tiles[selected].Key : null;
        var old = new Dictionary<string, Tile>(tiles.Count, StringComparer.Ordinal);
        foreach (var t in tiles)
            old[t.Key] = t;
        builtFor = state;
        builtSearch = search;
        tiles.Clear();
        sections.Clear();
        counts.Clear();
        var host = paths();

        if (search.Length > 0)
        {
            foreach (var g in state.Games
                .Select(g => (Game: g, Score: ArcadeCommands.Score(g.Title, search)))
                .Where(x => x.Score != null)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Game))
            {
                tiles.Add(GameTile(state, host, g));
            }

            sections.Add(new Section("Matches", tiles.Count == 1 ? "1 game" : $"{tiles.Count} games"));
            counts.Add(tiles.Count);
        }
        else
        {
            foreach (var e in state.Shelf)
            {
                var game = e.Games.Select(state.Game).Where(g => g != null).OrderByDescending(g => g!.LastPlayed ?? 0).FirstOrDefault();
                tiles.Add(new Tile("shelf:" + e.Key, e.Title, $"{e.Platforms} · {e.Year}", game,
                    "Not in your library yet. Drop your own copy into a console folder under " + state.GamesRoot + ".",
                    game?.System ?? CoverLayout.SystemForPlatforms(e.Platforms), e.Year, game?.Boxart is { } art ? host.ToLocal(art) : null));
            }

            if (state.Shelf.Count > 0)
            {
                sections.Add(new Section("Final Fantasy", $"{state.Shelf.Count(e => e.Games.Count > 0)} of {state.Shelf.Count} in your library"));
                counts.Add(state.Shelf.Count);
            }

            foreach (var (system, games) in state.EverythingElse())
            {
                foreach (var g in games)
                    tiles.Add(GameTile(state, host, g));
                sections.Add(new Section(system.Name, games.Count == 1 ? "1 game" : $"{games.Count} games"));
                counts.Add(games.Count);
            }
        }

        foreach (var t in tiles)
        {
            if (old.TryGetValue(t.Key, out var was))
            {
                t.Focus = was.Focus;
                t.ArtFade = was.Art == t.Art ? was.ArtFade : 0;
            }
        }

        if (keep != null && tiles.FindIndex(t => t.Key == keep) is >= 0 and var at)
            selected = at;
        selected = Math.Clamp(selected, 0, Math.Max(0, tiles.Count - 1));
    }

    private static Tile GameTile(ArcadeState state, HostPaths host, ArcadeGame g)
        => new(g.Id, g.Title, state.SystemName(g.System) + (g.Year is { } y ? $" · {y}" : "") + (g.Discs > 1 ? $" · {g.Discs} discs" : ""), g, "",
            g.System, g.Year, g.Boxart is { } art ? host.ToLocal(art) : null);

    /// <summary>One chip per shelf; Tab and the shoulder buttons walk them, a click jumps.</summary>
    private void DrawShelfTabs(float scale)
    {
        if (sections.Count < 2)
            return;
        var current = grid.SectionOf(selected);
        var right = ImGui.GetWindowContentRegionMax().X;
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 999);
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(10, 3) * scale);
        for (var s = 0; s < sections.Count; s++)
        {
            if (counts[s] == 0)
                continue;
            var label = sections[s].Name;
            var width = ImGui.CalcTextSize(label).X + (20 * scale);
            if (s > 0)
            {
                ImGui.SameLine(0, 6 * scale);
                if (ImGui.GetCursorPosX() + width > right)
                    ImGui.NewLine();
            }

            var on = s == current;
            ImGui.PushStyleColor(ImGuiCol.Button, on ? Accent with { W = 0.90f } : new Vector4(1, 1, 1, 0.05f));
            ImGui.PushStyleColor(ImGuiCol.Text, on ? new Vector4(0.08f, 0.07f, 0.04f, 1f) : new Vector4(1, 1, 1, 0.70f));
            if (ImGui.Button(label + "##shelf" + s.ToString(System.Globalization.CultureInfo.InvariantCulture)) && s < grid.Firsts.Count)
                Select(grid.Firsts[s]);
            ImGui.PopStyleColor(2);
        }

        ImGui.PopStyleVar(2);
    }

    private void Select(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, tiles.Count - 1));
        if (index == selected)
            return;
        selected = index;
        scrollToSelected = true;
    }

    private bool Pad(GamepadButtons button) => gamepad.Pressed(button) > 0;

    private void HandleKeys(bool searchActive)
    {
        if (!ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows))
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.F5))
        {
            covers.Clear(); // a replaced picture is read again
            Flash(Describe(arcade.Refresh(), "Rescanning your games folder…"));
        }

        if (ImGui.IsKeyPressed(ImGuiKey.F6))
            Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.Sync)), "Syncing saves…"));
        if (ImGui.IsKeyPressed(ImGuiKey.Escape) && !ImGui.IsAnyItemActive())
            IsOpen = false;
        if (tiles.Count == 0)
            return;
        if (ImGui.IsKeyPressed(ImGuiKey.DownArrow) || Pad(GamepadButtons.DpadDown))
            Select(grid.Move(selected, 0, 1));
        if (ImGui.IsKeyPressed(ImGuiKey.UpArrow) || Pad(GamepadButtons.DpadUp))
            Select(grid.Move(selected, 0, -1));

        // while text is being typed, left and right belong to the text cursor
        var typing = searchActive && search.Length > 0;
        if ((!typing && ImGui.IsKeyPressed(ImGuiKey.RightArrow)) || Pad(GamepadButtons.DpadRight))
            Select(grid.Move(selected, 1, 0));
        if ((!typing && ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) || Pad(GamepadButtons.DpadLeft))
            Select(grid.Move(selected, -1, 0));

        if (ImGui.IsKeyPressed(ImGuiKey.Tab) || Pad(GamepadButtons.R1) || Pad(GamepadButtons.L1))
        {
            var back = ImGui.GetIO().KeyShift || Pad(GamepadButtons.L1);
            Select(grid.NextSection(selected, back ? -1 : 1));
            focusSearch = true; // Tab would otherwise walk ImGui's focus off the search box
        }

        if ((!searchActive && (ImGui.IsKeyPressed(ImGuiKey.Enter) || ImGui.IsKeyPressed(ImGuiKey.KeypadEnter))) || Pad(GamepadButtons.South))
            Activate();
    }

    private void Activate()
    {
        if (selected < 0 || selected >= tiles.Count)
            return;
        var tile = tiles[selected];
        Flash(tile.Game is { } game ? arcade.Play(game) : tile.Missing);
    }

    /// <summary>
    /// Runs on a pool thread, never on the framework or draw thread: read the player's own file, decode it, and
    /// keep only a thumbnail. Nothing here touches the network; the path came from the helper's scan of local folders.
    /// </summary>
    private async Task<IDalamudTextureWrap?> LoadCover(string path, CancellationToken cancel)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > LargestPicture)
            return null;
        var bytes = await File.ReadAllBytesAsync(path, cancel).ConfigureAwait(false);
        var full = await textures.CreateFromImageAsync(bytes, "XivArcade cover", cancel).ConfigureAwait(false);
        var (w, h) = CoverLayout.Thumbnail(full.Width, full.Height, ThumbnailSide);
        if (w == full.Width && h == full.Height)
            return full;
        try
        {
            return await textures.CreateFromExistingTextureAsync(full, new TextureModificationArgs { NewWidth = w, NewHeight = h }, false, "XivArcade cover thumbnail", cancel).ConfigureAwait(false);
        }
        finally
        {
            full.Dispose();
        }
    }


    private static string Describe(string result, string ok) => result == "ok" ? ok : result;

    /// <summary>Says plainly when games cannot become panels, and offers the Linux desktop instead.</summary>
    private void DrawHostNotice()
    {
        if (arcade.HostProblem.Length > 0)
        {
            ImGui.PushTextWrapPos(0);
            ImGui.TextColored(Attention, arcade.HostProblem);
            ImGui.PopTextWrapPos();
        }

        if (arcade.Panels)
            return;
        ImGui.PushTextWrapPos(0);
        ImGui.TextColored(Attention, HostLauncher.GhosttyMissing);
        ImGui.TextDisabled("Your library, setup and save sync still work. To play inside the game, install ghostty-dalamud and run its host agent.");
        ImGui.PopTextWrapPos();
        var onDesktop = config.PlayOnHostDesktop;
        if (ImGui.Checkbox("Play on the Linux desktop instead (the game opens outside FFXIV)", ref onDesktop))
        {
            config.PlayOnHostDesktop = onDesktop;
            save();
        }

        ImGui.Spacing();
    }

    // Welcome -----------------------------------------------------------------------------------

    /// <summary>A glass panel behind whatever <paramref name="body"/> draws: the body goes first so its size is known, the glass is slipped underneath.</summary>
    private static void Glass(float scale, Action body)
    {
        var draw = ImGui.GetWindowDrawList();
        var pad = new Vector2(14, 12) * scale;
        var width = ImGui.GetContentRegionAvail().X;
        draw.ChannelsSplit(2);
        draw.ChannelsSetCurrent(1);
        var start = ImGui.GetCursorScreenPos();
        ImGui.SetCursorScreenPos(start + pad);
        ImGui.BeginGroup();
        body();
        ImGui.EndGroup();
        var bottom = ImGui.GetItemRectMax().Y + pad.Y;
        draw.ChannelsSetCurrent(0);
        draw.AddRectFilled(start, new Vector2(start.X + width, bottom), White(0.045f), 12 * scale);
        draw.AddRect(start, new Vector2(start.X + width, bottom), White(0.09f), 12 * scale);
        draw.ChannelsMerge();
        ImGui.SetCursorScreenPos(new Vector2(start.X, bottom));
        ImGui.Dummy(new Vector2(width, 6 * scale));
    }

    private void DrawWelcome(ArcadeState state, float scale) => Glass(scale, () => WelcomeBody(state, scale));

    private void WelcomeBody(ArcadeState state, float scale)
    {
        ImGui.TextColored(Accent, "WELCOME TO THE ARCADE");
        ImGui.PushTextWrapPos(Wrap(scale));
        ImGui.TextUnformatted("Your own classic games, running on your Linux desktop and shown here as a game panel, with every save kept in step across your machines. Three steps; each one ticks itself off.");
        ImGui.PopTextWrapPos();

        if (!state.Loaded)
        {
            ImGui.TextDisabled(arcade.HostProblem.Length == 0 ? "Preparing your folders…" : "Waiting for the Linux host.");
        }

        for (var i = 0; i < state.Checks.Count; i++)
        {
            var c = state.Checks[i];
            DrawStep(i + 1, c.Title, c.Detail, c.Ok, c.Command, scale);
            if (c.Key == "folder")
                DrawLaunchBox(state, scale);
        }

        if (ImGui.Button("Rescan  (F5)"))
            Flash(Describe(arcade.Refresh(), "Rescanning your games folder…"));
        ImGui.SameLine();
        ImGui.TextDisabled("New files show up by themselves while this window is open.");
        ImGui.PushTextWrapPos(Wrap(scale));
        ImGui.TextDisabled("Covers are your own pictures: <console>/covers/<game name>.png, cover.png in a game's own folder, or your LaunchBox Images folder. A game without one gets a drawn cover, or, only if you turn it on, a cover fetched from RetroArch's thumbnail set.");
        ImGui.TextDisabled(state.Legal);
        ImGui.PopTextWrapPos();
    }

    private void DrawStep(int number, string title, string detail, bool ok, string command, float scale)
    {
        ImGui.PushID(number);
        var draw = ImGui.GetWindowDrawList();
        var pos = ImGui.GetCursorScreenPos();
        var r = 11 * scale;
        var centre = pos + new Vector2(r, r + (2 * scale));
        if (ok)
        {
            draw.AddCircleFilled(centre, r, ImGui.GetColorU32(Good));
            draw.AddLine(centre + new Vector2(-5, 0) * scale, centre + new Vector2(-1.5f, 4) * scale, 0xFF101010, 2.2f * scale);
            draw.AddLine(centre + new Vector2(-1.5f, 4) * scale, centre + new Vector2(5.5f, -4) * scale, 0xFF101010, 2.2f * scale);
        }
        else
        {
            draw.AddCircle(centre, r, ImGui.GetColorU32(new Vector4(1, 1, 1, 0.35f)), 24, 1.6f * scale);
            var label = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            draw.AddText(centre - (ImGui.CalcTextSize(label) / 2), ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
        }

        ImGui.Dummy(new Vector2(r * 2, r * 2));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextColored(ok ? Good : new Vector4(1, 1, 1, 0.95f), title);
        ImGui.PushTextWrapPos(Wrap(scale));
        ImGui.TextDisabled(detail);
        ImGui.PopTextWrapPos();
        if (!ok && command.Length > 0)
            CopyRow(command, scale);
        ImGui.EndGroup();
        ImGui.PopID();
    }

    private void DrawLaunchBox(ArcadeState state, float scale)
    {
        ImGui.Indent(30 * scale);
        ImGui.PushID("launchbox");
        if (state.LaunchBoxFound.Count > 0)
        {
            ImGui.TextColored(Good, "LaunchBox library found: " + string.Join(", ", state.LaunchBoxFound));
        }
        else
        {
            ImGui.TextDisabled("I have a LaunchBox folder: copy it into the games folder as it is, or point at it here.");
            ImGui.SetNextItemWidth(Math.Max(160 * scale, ImGui.GetContentRegionAvail().X - (110 * scale)));
            var enter = ImGui.InputTextWithHint("##lb", "/path/to/LaunchBox (a Linux path)", ref launchBoxPath, 512, ImGuiInputTextFlags.EnterReturnsTrue);
            ImGui.SameLine();
            if ((ImGui.Button("Use it") || enter) && launchBoxPath.Trim().Length > 0)
                Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.LaunchBox, launchBoxPath.Trim())), "Reading your LaunchBox library…"));
        }

        ImGui.PopID();
        ImGui.Unindent(30 * scale);
    }

    private void CopyRow(string command, float scale)
    {
        // The helper is not on PATH unless the player put it there; the copy that the plugin keeps always works.
        if (command.StartsWith("xiv-arcade ", StringComparison.Ordinal))
            command = "python3 " + ArcadeCommands.Quote(arcade.HelperPath) + command["xiv-arcade".Length..];

        if (ImGui.SmallButton("Copy"))
        {
            ImGui.SetClipboardText(command);
            Flash("ok: copied. Paste it into a terminal on your Linux desktop.");
        }

        ImGui.SameLine();
        ImGui.PushTextWrapPos(Wrap(scale));
        ImGui.TextColored(Accent with { W = 0.9f }, command);
        ImGui.PopTextWrapPos();
    }

    // Save sync ---------------------------------------------------------------------------------

    private void DrawSync(ArcadeState state, float scale) => Glass(scale, () => SyncBody(state, scale));

    /// <summary>Text inside a glass panel wraps short of the panel's right padding.</summary>
    private static float Wrap(float scale) => ImGui.GetWindowContentRegionMax().X - (14 * scale);

    private void SyncBody(ArcadeState state, float scale)
    {
        var sync = state.Sync;
        ImGui.TextColored(Tone(sync), "Save sync · " + sync.Label);
        ImGui.PushTextWrapPos(Wrap(scale));
        ImGui.TextDisabled(sync.Detail);
        if (state.SyncRoot.Length > 0)
            ImGui.TextDisabled("Every save and state lives in " + state.SyncRoot + ". Syncthing copies that folder between your machines, keeps old versions, and XivArcade never starts it or pairs anything for you.");
        ImGui.PopTextWrapPos();
        for (var i = 0; i < sync.Steps.Count; i++)
            DrawStep(i + 1, sync.Steps[i].Text, "", false, sync.Steps[i].Command, scale);
        foreach (var kept in sync.Conflicts)
            ImGui.TextColored(Attention, "Both saves kept: " + kept);
        if (ImGui.Button("Sync now  (F6)"))
            Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.Sync)), "Syncing saves…"));
    }

    // Cover wall --------------------------------------------------------------------------------

    private static uint Rgba(uint rgb, float alpha, float brightness = 1f)
    {
        var r = (uint)Math.Clamp(((rgb >> 16) & 0xFF) * brightness, 0, 255);
        var g = (uint)Math.Clamp(((rgb >> 8) & 0xFF) * brightness, 0, 255);
        var b = (uint)Math.Clamp((rgb & 0xFF) * brightness, 0, 255);
        return ((uint)Math.Clamp(alpha * 255f, 0, 255) << 24) | (b << 16) | (g << 8) | r;
    }

    private static uint White(float alpha, float brightness = 1f) => Rgba(0xFFFFFF, alpha, brightness);

    private static uint Gold(float alpha) => ImGui.GetColorU32(Accent with { W = alpha });

    private void DrawGrid(float scale, float dt)
    {
        if (tiles.Count == 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(search.Length > 0 ? "Nothing in your library matches." : "Your covers will line up here as soon as the first game is found.");
            return;
        }

        var cellW = 148 * scale;
        var coverH = 172 * scale;
        var labelH = ImGui.GetFontSize() + (8 * scale);
        var gap = 16 * scale;
        var headerH = 34 * scale;
        var margin = cellW * CoverLayout.FocusGrow; // room for the focused cover to grow without being clipped
        var available = ImGui.GetContentRegionAvail().X - (margin * 2);
        grid.Build(counts, available, cellW, coverH + labelH, gap, headerH);

        var used = (grid.Columns * (cellW + gap)) - gap;
        var gridTop = ImGui.GetCursorPosY();
        var origin = ImGui.GetCursorScreenPos() + new Vector2(margin + Math.Max(0, (available - used) / 2), 0);
        ImGui.Dummy(new Vector2(available, grid.Height));
        var after = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();

        var view = ImGui.GetWindowHeight();
        if (scrollToSelected)
        {
            scrollTarget = CoverGrid.ScrollTo(grid.Cells[selected], gridTop, view, ImGui.GetScrollMaxY() + view);
            scrolling = true;
            scrollToSelected = false;
        }

        if (scrolling)
        {
            if (ImGui.GetIO().MouseWheel != 0)
            {
                scrolling = false; // the wheel wins
            }
            else
            {
                var target = Math.Min(scrollTarget, ImGui.GetScrollMaxY());
                var y = CoverLayout.Approach(ImGui.GetScrollY(), target, dt, 14f);
                if (MathF.Abs(y - target) < 0.5f)
                {
                    y = target;
                    scrolling = false;
                }

                ImGui.SetScrollY(y);
            }
        }

        for (var s = 0; s < sections.Count; s++)
            DrawSectionHeader(draw, origin + new Vector2(0, grid.Headers[s]), used, headerH, sections[s], scale);

        var hoveredTile = -1;
        for (var i = 0; i < tiles.Count; i++)
        {
            var cell = grid.Cells[i];
            var min = origin + cell.Min;
            var max = origin + cell.Max;
            var tile = tiles[i];
            if (!ImGui.IsRectVisible(min, max))
            {
                tile.Focus = i == selected ? 1 : 0; // nothing to animate off screen
                continue;
            }

            ImGui.SetCursorScreenPos(min);
            ImGui.PushID(i);
            var clicked = ImGui.InvisibleButton("##cover", cell.Max - cell.Min);
            ImGui.PopID();
            var hovered = ImGui.IsItemHovered();
            if (hovered)
                hoveredTile = i;
            if (clicked)
            {
                if (i == selected)
                    Activate(); // a second click, or a double click, plays
                else
                    Select(i);
            }

            tile.Focus = CoverLayout.Approach(tile.Focus, i == selected ? 1f : hovered ? 0.45f : 0f, dt, 16f);
            if (i != selected)
                DrawCover(draw, tile, new CoverRect(min.X, min.Y, cellW, coverH), labelH, scale, dt);
        }

        // the focused cover is drawn last so its glow and its growth sit above its neighbours
        if (selected < tiles.Count)
        {
            var cell = grid.Cells[selected];
            var min = origin + cell.Min;
            if (ImGui.IsRectVisible(min, origin + cell.Max))
                DrawCover(draw, tiles[selected], new CoverRect(min.X, min.Y, cellW, coverH), labelH, scale, dt);
        }

        ImGui.SetCursorScreenPos(after);
        ImGui.Dummy(Vector2.Zero);
        if (hoveredTile >= 0)
            TileTooltip(tiles[hoveredTile]);
    }

    private static void DrawSectionHeader(ImDrawListPtr draw, Vector2 at, float width, float height, Section section, float scale)
    {
        if (!ImGui.IsRectVisible(at, at + new Vector2(width, height)))
            return;
        var size = ImGui.GetFontSize();
        var y = at.Y + height - size - (10 * scale);
        draw.AddRectFilled(new Vector2(at.X, y + (2 * scale)), new Vector2(at.X + (3 * scale), y + size - (1 * scale)), Gold(0.95f), 2 * scale);
        var name = section.Name.ToUpperInvariant();
        draw.AddText(new Vector2(at.X + (10 * scale), y), Gold(0.95f), name);
        var x = at.X + (10 * scale) + ImGui.CalcTextSize(name).X + (10 * scale);
        draw.AddText(new Vector2(x, y), White(0.40f), section.Detail);
        x += ImGui.CalcTextSize(section.Detail).X + (12 * scale);
        if (x < at.X + width)
            draw.AddLine(new Vector2(x, y + (size / 2)), new Vector2(at.X + width, y + (size / 2)), White(0.08f), 1f);
    }

    private void DrawCover(ImDrawListPtr draw, Tile tile, CoverRect box, float labelH, float scale, float dt)
    {
        var image = tile.Art is { } art ? covers.Get(art) : null;
        tile.ArtFade = image == null ? 0 : CoverLayout.Approach(tile.ArtFade, 1, dt, 10f);
        var lit = tile.Game != null;
        var ready = tile.Game is { Ready: true };
        var presence = lit ? (ready ? 1f : 0.75f) : 0.42f;
        var brightness = CoverLayout.Brightness(tile.Focus);
        var eased = CoverLayout.Ease(tile.Focus);
        var rounding = 4 * scale;

        // a drawn cover keeps its console's box shape; a picture keeps its own
        var drawn = CoverLayout.Focus(CoverLayout.AspectFit(tile.Style.Aspect, box), tile.Focus);
        var shown = image == null ? drawn : CoverLayout.Focus(CoverLayout.AspectFit(image.Width, image.Height, box), tile.Focus);
        var frame = tile.ArtFade > 0.5f ? shown : drawn;

        draw.AddRectFilled(frame.Min + new Vector2(0, 5 * scale), frame.Max + new Vector2(0, 7 * scale), Rgba(0, 0.35f * presence), rounding * 2);
        if (eased > 0.01f)
        {
            for (var k = 6; k >= 1; k--)
            {
                var spread = new Vector2(k, k) * 1.7f * scale;
                var falloff = 1f - (k / 7f);
                draw.AddRect(frame.Min - spread, frame.Max + spread, Gold(0.30f * eased * falloff * falloff), rounding + spread.X, ImDrawFlags.None, 2.2f * scale);
            }
        }

        if (tile.ArtFade < 1f)
            DrawGeneratedCover(draw, tile, drawn, presence, brightness, scale);
        if (image != null)
            draw.AddImageRounded(image.Handle, shown.Min, shown.Max, Vector2.Zero, Vector2.One, White(tile.ArtFade * (lit && !ready ? 0.75f : 1f), brightness), rounding);
        if (eased > 0.01f)
            draw.AddRect(frame.Min, frame.Max, Gold(0.95f * eased), rounding, ImDrawFlags.None, 2f * scale);
        if (lit && !ready)
            draw.AddCircleFilled(new Vector2(frame.Max.X - (9 * scale), frame.Min.Y + (9 * scale)), 4.5f * scale, ImGui.GetColorU32(Attention));

        // the name under the box, one line
        var width = box.W;
        if (tile.Label == null || tile.LabelWidth != width)
        {
            tile.Label = CoverText.Wrap(tile.Title, width, 1, s => ImGui.CalcTextSize(s).X, out _).FirstOrDefault() ?? "";
            tile.LabelWidth = width;
        }

        var labelSize = ImGui.CalcTextSize(tile.Label);
        draw.AddText(new Vector2(box.X + ((width - labelSize.X) / 2), box.Y + box.H + (labelH - labelSize.Y)), White((0.50f + (0.50f * eased)) * (lit ? 1f : 0.6f)), tile.Label);
    }

    /// <summary>
    /// The cover for a game with no picture: the console's colour down the spine and washed over dark ink, the
    /// series as a small gold kicker, the title as large as fits, the year and the console's mark. Draw lists
    /// only: no image file and nothing fetched.
    /// </summary>
    private void DrawGeneratedCover(ImDrawListPtr draw, Tile tile, CoverRect r, float presence, float brightness, float scale)
    {
        var rgb = tile.Style.Rgb;
        var rounding = 4 * scale;
        var min = r.Min;
        var max = r.Max;
        draw.AddRectFilled(min, max, Rgba(0x10121A, presence < 1 ? 0.92f : 1f, brightness), rounding);
        var top = Rgba(rgb, 0.50f * presence, brightness);
        var bottom = Rgba(rgb, 0.06f * presence, brightness);
        draw.AddRectFilledMultiColor(min + new Vector2(1, 1), max - new Vector2(1, 1), top, top, bottom, bottom);

        var spine = MathF.Max(6 * scale, r.W * 0.085f);
        draw.AddRectFilled(min, new Vector2(min.X + spine, max.Y), Rgba(rgb, presence, brightness), rounding, ImDrawFlags.RoundCornersLeft);
        draw.AddLine(new Vector2(min.X + spine, min.Y + 1), new Vector2(min.X + spine, max.Y - 1), White(0.22f * presence), 1f);
        for (var n = 0; n < 3; n++)
        {
            var y = max.Y - ((10 + (n * 5)) * scale * (r.H / (172 * scale)));
            draw.AddLine(new Vector2(min.X + (spine * 0.25f), y), new Vector2(min.X + (spine * 0.75f), y), Rgba(0, 0.35f * presence), 1.5f * scale);
        }

        var pad = r.W * 0.07f;
        var left = min.X + spine + pad;
        var right = max.X - pad;
        draw.AddRect(new Vector2(min.X + spine + (pad * 0.45f), min.Y + (pad * 0.45f)), max - new Vector2(pad * 0.45f, pad * 0.45f), Gold(0.22f * presence), rounding * 0.5f, ImDrawFlags.None, 1f);

        using var pushed = titleFont is { Available: true } ? titleFont.Push() : null;
        var font = ImGui.GetFont();
        var fontSize = ImGui.GetFontSize();
        var small = MathF.Max(9 * scale, r.H * 0.062f);
        float Width(string s, float size) => ImGui.CalcTextSize(s).X * size / fontSize;

        if (tile.Year is > 0 and var year)
        {
            var text = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
            draw.AddText(font, small, new Vector2(right - Width(text, small), min.Y + pad), White(0.62f * presence), text);
        }

        var mark = tile.Style.Mark;
        var markW = Width(mark, small);
        var pillMin = new Vector2(right - markW - (10 * scale), max.Y - pad - small - (4 * scale));
        draw.AddRectFilled(pillMin, new Vector2(right, max.Y - pad), Rgba(rgb, 0.95f * presence, brightness), 999);
        draw.AddText(font, small, pillMin + new Vector2(5 * scale, 2 * scale), Rgba(0x0B0C11, presence), mark);

        var areaTop = min.Y + pad + small + (4 * scale);
        var areaBottom = pillMin.Y - (4 * scale);
        var textW = right - left;

        // fitted once for the resting size; the focused cover only scales the result
        var rest = r.W / (1 + (CoverLayout.FocusGrow * CoverLayout.Ease(tile.Focus)));
        if (tile.Fit == null || MathF.Abs(tile.FitWidth - rest) > 0.5f)
        {
            var k = rest / r.W;
            var kickerRoom = CoverText.Split(tile.Title).Kicker.Length > 0 ? (small * 1.9f * k) : 0;
            tile.Fit = CoverText.Fit(tile.Title, textW * k, ((areaBottom - areaTop) * k) - kickerRoom, r.H * k * 0.30f, 9 * scale, s => ImGui.CalcTextSize(s).X / fontSize);
            tile.FitWidth = rest;
        }

        var fit = tile.Fit;
        var size = fit.Size * (r.W / rest);
        var lineH = size * 1.12f;
        var kickerH = fit.Kicker.Length > 0 ? small * 1.9f : 0;
        var block = kickerH + (fit.Lines.Count * lineH);
        var yText = areaTop + MathF.Max(0, (areaBottom - areaTop - block) / 2);
        var centre = (left + right) / 2;
        if (fit.Kicker.Length > 0)
        {
            var kw = MathF.Min(Width(fit.Kicker, small), textW);
            var ks = Width(fit.Kicker, small) > textW ? small * textW / Width(fit.Kicker, small) : small;
            draw.AddText(font, ks, new Vector2(centre - (kw / 2), yText), Gold(0.95f * presence), fit.Kicker);
            draw.AddLine(new Vector2(centre - (textW * 0.18f), yText + (small * 1.45f)), new Vector2(centre + (textW * 0.18f), yText + (small * 1.45f)), Gold(0.55f * presence), 1f);
            yText += kickerH;
        }

        foreach (var l in fit.Lines)
        {
            var w = Width(l, size);
            draw.AddText(font, size, new Vector2(centre - (w / 2) + scale, yText + scale), Rgba(0, 0.45f * presence), l);
            draw.AddText(font, size, new Vector2(centre - (w / 2), yText), White(presence, MathF.Min(1f, brightness + 0.1f)), l);
            yText += lineH;
        }
    }

    private void TileTooltip(Tile tile)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26);
        ImGui.TextUnformatted(tile.Title);
        ImGui.TextDisabled(tile.Line);
        ImGui.Separator();
        if (tile.Game is { } g)
        {
            ImGui.TextUnformatted(g.Ready ? "Click once to select, again to play." : "Its emulator core is not installed yet; see /arcade setup.");
            if (tile.Art == null)
                ImGui.TextDisabled(ArtHint(tile));
        }
        else
        {
            ImGui.TextUnformatted(tile.Missing);
            ImGui.TextDisabled(ArcadeText.Legal);
        }

        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    /// <summary>Where the player's own picture goes. Artwork is never downloaded.</summary>
    private string ArtHint(Tile tile)
    {
        var state = arcade.State;
        var folder = state.Systems.FirstOrDefault(s => s.Id == tile.System)?.Folder ?? tile.System;
        return $"This is a drawn cover. To use your own picture, save it as {state.GamesRoot}/{folder}/covers/{tile.Title}.png (or .jpg), or as cover.png in the game's own folder, then press F5."
            + (state.Art.Enabled ? "" : " Or press \"Fetch cover art for my games\" below: it is off until you do.");
    }

    /// <summary>The glass strip under the wall: everything about the selected game, and the Play button.</summary>
    private void DrawDetails(ArcadeState state, float scale, float height)
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new Vector4(1, 1, 1, 0.045f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(1, 1, 1, 0.09f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 10) * scale);
        if (ImGui.BeginChild("##arcade-details", new Vector2(0, height), true, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
            && selected >= 0 && selected < tiles.Count)
        {
            var tile = tiles[selected];
            var game = tile.Game;
            var label = game == null ? "Not in your library" : game.Ready ? "Play" : "Needs its core";
            var buttonW = MathF.Max(120 * scale, ImGui.CalcTextSize(label).X + (36 * scale));
            var textW = ImGui.GetContentRegionAvail().X - buttonW - (14 * scale);
            var top = ImGui.GetCursorPos();

            ImGui.PushTextWrapPos(top.X + textW);
            ImGui.TextColored(Accent, tile.Title);
            var facts = tile.Line;
            if (game?.LastPlayed is { } t)
                facts += "  ·  last played " + DateTimeOffset.FromUnixTimeSeconds((long)t).LocalDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
            else if (game != null)
                facts += "  ·  never played";
            ImGui.TextUnformatted(facts);
            if (game == null)
            {
                ImGui.TextDisabled("Not in your library yet. Drop your own copy into a console folder under " + state.GamesRoot + ".");
            }
            else
            {
                ImGui.TextColored(Tone(state.Sync), "●");
                ImGui.SameLine(0, 6 * scale);
                var system = state.Systems.FirstOrDefault(x => x.Id == game.System);
                var runs = system is { Emulator.Length: > 0 } ? "   ·   runs in " + system.Emulator : "";
                var bios = system?.Bios is { Required: true } b ? "   ·   BIOS: " + (b.Ok ? "ok" : b.Summary) : "";
                ImGui.TextDisabled("Saves: " + state.Sync.Label + runs + bios + (tile.Art == null ? "   ·   drawn cover: hover it for where your own picture goes" : ""));
            }

            ImGui.PopTextWrapPos();

            ImGui.SetCursorPos(new Vector2(top.X + textW + (14 * scale), top.Y + (4 * scale)));
            var can = game is { Ready: true };
            ImGui.PushStyleColor(ImGuiCol.Button, can ? Accent with { W = 0.92f } : new Vector4(1, 1, 1, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, can ? Accent : new Vector4(1, 1, 1, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, can ? Accent with { W = 0.80f } : new Vector4(1, 1, 1, 0.06f));
            ImGui.PushStyleColor(ImGuiCol.Text, can ? new Vector4(0.08f, 0.07f, 0.04f, 1f) : new Vector4(1, 1, 1, 0.45f));
            if (ImGui.Button(label + "##play", new Vector2(buttonW, height - (28 * scale))))
                Activate();
            ImGui.PopStyleColor(4);
        }

        ImGui.EndChild();
        ImGui.PopStyleVar();
        ImGui.PopStyleColor(2);
    }


    /// <summary>
    /// Cover art is opt-in. The button says what it will do before it does it, and the helper, not the game,
    /// does the asking: the plugin never opens a connection.
    /// </summary>
    private void DrawArtButton(ArcadeState state)
    {
        var art = state.Art;
        if (!state.Loaded || (art.Without == 0 && !art.Enabled))
            return;
        var label = art.Running ? "Fetching cover art…" : !art.Enabled ? "Fetch cover art for my games" : art.Waiting > 0 ? $"Fetch {art.Waiting} more covers" : "Cover art: on";
        if (ImGui.Button(label + "##art") && !art.Running)
        {
            var verb = !art.Enabled || art.Waiting > 0 ? "on" : "off";
            Flash(Describe(arcade.Run(new ArcadeRequest(ArcadeVerb.Art, verb)), verb == "on" ? "Fetching cover art for the games in your library…" : "Cover art is off. Nothing is contacted."));
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28);
            ImGui.TextUnformatted(ArcadeText.ArtNotice);
            if (art.Enabled)
                ImGui.TextDisabled($"{art.Fetched} fetched · {art.Without} games without a cover. Click to turn it off; /arcade art refresh asks again. Fetched covers are kept in {art.Folder}.");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }

        ImGui.SameLine();
    }

    private void DrawFooter(ArcadeState state)
    {
        if (ImGui.Button("Rescan"))
            Flash(Describe(arcade.Refresh(), "Rescanning your games folder…"));
        ImGui.SameLine();
        if (ImGui.Button(showSync ? "Hide save sync" : "Save sync"))
            showSync = !showSync;
        ImGui.SameLine();
        DrawArtButton(state);
        if (flash != null && DateTime.UtcNow < flashUntil)
        {
            var bad = flash.StartsWith("error", StringComparison.Ordinal);
            ImGui.TextColored(bad ? ImGuiColors.DalamudOrange : ImGuiColors.HealerGreen, bad ? flash[7..] : flash.StartsWith("ok: ", StringComparison.Ordinal) ? flash[4..] : flash);
        }
        else
        {
            ImGui.TextDisabled(state.Message.Length > 0 ? state.Message : $"{state.Games.Count} games · saves: {state.Sync.Label}");
        }

        ImGui.TextDisabled(state.Art.Enabled
            ? "Bring your own games. Cover art is on: only game and console names go to thumbnails.libretro.com. Games and BIOS files are never fetched."
            : "Bring your own games and covers: nothing is downloaded, and only folders you choose are read.");
    }

    private static Vector4 Tone(ArcadeSync sync) => sync.Tone switch
    {
        "ok" => Good,
        "busy" => Busy,
        _ => Attention,
    };
}
