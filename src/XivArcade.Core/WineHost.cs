namespace XivArcade.Core;

/// <summary>
/// Starting a Linux program from inside Wine without ghostty: Wine's own <c>start /unix</c>. The program
/// opens on the Linux desktop, not as a game panel, and inherits Wine's environment. This is the fallback,
/// and it cannot work where the game runs in a sandbox that hides the host (a Flatpak launcher).
/// </summary>
public static class WineHost
{
    public const string FileName = "start.exe";

    /// <summary>Arguments for start.exe: /unix, the program, then each argument quoted for the Windows command line.</summary>
    public static string Arguments(IReadOnlyList<string> argv)
        => "/unix " + string.Join(' ', argv.Select(Quote));

    /// <summary>Windows command-line quoting (CommandLineToArgvW rules): backslashes before a quote are doubled.</summary>
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => c is ' ' or '\t' or '"'))
            return arg;
        var sb = new System.Text.StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }

            sb.Append('\\', c == '"' ? (slashes * 2) + 1 : slashes);
            slashes = 0;
            sb.Append(c);
        }

        sb.Append('\\', slashes * 2).Append('"');
        return sb.ToString();
    }
}
