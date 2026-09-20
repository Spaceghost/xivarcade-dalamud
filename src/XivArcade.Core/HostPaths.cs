namespace XivArcade.Core;

/// <summary>
/// The Linux directories XivArcade reads, and how this process reaches them. Every path in here is a
/// Linux path; <see cref="ToLocal"/> maps it for file access (identity on the host, "Z:\..." under Wine).
/// </summary>
public sealed class HostPaths
{
    public HostPaths(string linuxHome, Func<string, string> toLocal)
    {
        LinuxHome = linuxHome.TrimEnd('/');
        ToLocal = toLocal;
    }

    /// <summary>The user's Linux home directory (e.g. "/home/name"); may be empty when unknown.</summary>
    public string LinuxHome { get; }

    /// <summary>Maps a Linux path to one this process can open.</summary>
    public Func<string, string> ToLocal { get; }

    /// <summary>Paths on the host itself (tests, the scan tool).</summary>
    public static HostPaths Native(string linuxHome) => new(linuxHome, p => p);

    /// <summary>What Wine calls the Linux root when nothing better is known.</summary>
    public const string DefaultWineRoot = "Z:";

    /// <summary>Wine's own name for the Linux root; it works whatever the prefix's drive letters are.</summary>
    public const string UnixNamespace = @"\\?\unix";

    /// <summary>Paths as seen from inside Wine, where <paramref name="wineRoot"/> ("Z:" by default) is the Linux root.</summary>
    public static HostPaths Wine(string linuxHome, string wineRoot = DefaultWineRoot) => new(linuxHome, p => WineMap(p, wineRoot));

    public static string WineMap(string linuxPath, string wineRoot = DefaultWineRoot) => wineRoot + linuxPath.Replace('/', '\\');

    /// <summary>
    /// Finds what this Wine prefix calls the Linux root instead of assuming "Z:": the player's override, else
    /// the first drive (Z: down to D:) on which the Linux root is really there (/proc and /etc, and the home
    /// directory when it is known), else Wine's \\?\unix namespace. <c>Derived</c> is false when nothing
    /// answered and the default is only a guess, so the window can say so.
    /// </summary>
    public static (string Root, bool Derived) DetectWineRoot(Func<string, bool> directoryExists, string? overrideRoot = null, string linuxHome = "")
    {
        var over = (overrideRoot ?? "").Trim().TrimEnd('\\', '/');
        if (over.Length > 0)
            return (over, true);
        var candidates = new List<string>();
        for (var letter = 'Z'; letter >= 'D'; letter--)
            candidates.Add(letter + ":");
        candidates.Add(UnixNamespace);
        foreach (var root in candidates)
        {
            if (directoryExists(root + @"\proc") && directoryExists(root + @"\etc")
                && (linuxHome.Length == 0 || directoryExists(WineMap(linuxHome, root))))
                return (root, true);
        }

        return (DefaultWineRoot, false);
    }

    /// <summary>
    /// XivArcade's directory on the host: <c>$XDG_CONFIG_HOME/xiv-arcade</c> when that variable is an absolute
    /// Linux path, else <c>~/.config/xiv-arcade</c>. A setup that already lives under the default path is kept
    /// where it is (nothing is moved) unless the XDG one has been set up too.
    /// </summary>
    public string ConfigDir(string? xdgConfigHome, Func<string, bool> fileExists)
    {
        var legacy = LinuxHome + "/.config/xiv-arcade";
        if (string.IsNullOrEmpty(xdgConfigHome) || !xdgConfigHome.StartsWith('/'))
            return legacy;
        var xdg = xdgConfigHome.TrimEnd('/') + "/xiv-arcade";
        return xdg != legacy && fileExists(ToLocal(legacy + "/config.json")) && !fileExists(ToLocal(xdg + "/config.json")) ? legacy : xdg;
    }

    /// <summary>
    /// Application directories in increasing priority: an entry in a later directory replaces one with the
    /// same desktop-file id from an earlier one.
    /// </summary>
    public IReadOnlyList<string> ApplicationDirs => WithHome(
    [
        "/usr/share/applications",
        "/usr/local/share/applications",
        "~/.local/share/applications",
        "/var/lib/flatpak/exports/share/applications",
        "~/.local/share/flatpak/exports/share/applications",
    ]);

    /// <summary>Icon theme base directories, searched in order.</summary>
    public IReadOnlyList<string> IconRoots => WithHome(
    [
        "/usr/share/icons",
        "~/.local/share/icons",
        "/var/lib/flatpak/exports/share/icons",
        "~/.local/share/flatpak/exports/share/icons",
    ]);

    public IReadOnlyList<string> PixmapDirs => ["/usr/share/pixmaps"];

    /// <summary>Where a relative TryExec= is looked up (the game's PATH is Wine's, so this list is fixed).</summary>
    public IReadOnlyList<string> ExecSearchDirs => WithHome(
    [
        "/usr/local/bin",
        "/usr/bin",
        "/bin",
        "/usr/local/sbin",
        "/usr/sbin",
        "~/.local/bin",
        "/var/lib/flatpak/exports/bin",
        "~/.local/share/flatpak/exports/bin",
    ]);

    private List<string> WithHome(string[] paths)
    {
        var result = new List<string>(paths.Length);
        foreach (var p in paths)
        {
            if (!p.StartsWith("~/", StringComparison.Ordinal))
                result.Add(p);
            else if (LinuxHome.Length > 0)
                result.Add(LinuxHome + p[1..]);
        }

        return result;
    }

    /// <summary>
    /// Converts a Wine path on drive Z: ("Z:\home\name\x") back to a Linux path ("/home/name/x").
    /// Also accepts the NT form Wine puts in WINEHOMEDIR ("\??\Z:\home\name"). Returns null otherwise.
    /// </summary>
    public static string? LinuxFromWine(string? path, string wineRoot = DefaultWineRoot)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        var p = path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        string rest;
        if (p.StartsWith(@"unix\", StringComparison.OrdinalIgnoreCase))
            rest = p[4..]; // newer Wine: WINEHOMEDIR=\??\unix\home\name
        else if (wineRoot.Length == 2 && p.Length >= 2 && char.ToUpperInvariant(p[0]) == char.ToUpperInvariant(wineRoot[0]) && p[1] == ':')
            rest = p[2..];
        else
            return null;
        rest = rest.Replace('\\', '/');
        return rest.Length == 0 ? "/" : rest.StartsWith('/') ? rest : "/" + rest;
    }

    /// <summary>
    /// Best guess at the Linux home directory from the environment Wine gives the game: $HOME when it is a
    /// Linux path, then WINEHOMEDIR, then the part of <paramref name="xlcorePath"/> (e.g. the plugin config
    /// directory, "Z:\home\name\.xlcore\pluginConfigs\XivArcade") before "/.xlcore". Empty when unknown.
    /// </summary>
    public static string GuessHome(string? homeEnv, string? wineHomeDir, string? xlcorePath, string wineRoot = DefaultWineRoot)
    {
        if (!string.IsNullOrEmpty(homeEnv) && homeEnv.StartsWith('/'))
            return homeEnv.TrimEnd('/');
        if (LinuxFromWine(wineHomeDir, wineRoot) is { } fromWine)
            return fromWine.TrimEnd('/');
        if (LinuxFromWine(xlcorePath, wineRoot) is { } fromXl)
        {
            var idx = fromXl.IndexOf("/.xlcore", StringComparison.Ordinal);
            if (idx > 0)
                return fromXl[..idx];
        }

        return "";
    }
}
