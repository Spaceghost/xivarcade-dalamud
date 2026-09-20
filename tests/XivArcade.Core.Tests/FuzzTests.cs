using System.Text;
using XivArcade.Core.Arcade;

namespace XivArcade.Core.Tests;

/// <summary>
/// The parsers that read text this plugin did not write (the helper's state.json, ghostty's replies, what the
/// player types) must return "nothing" on bad input, never throw. XIVARCADE_FUZZ_SECONDS sets the budget
/// (default 2, the nightly workflow raises it); XIVARCADE_FUZZ_SEED replays a run.
/// </summary>
public sealed class FuzzTests
{
    private static readonly string[] Corpus =
    [
        """{"at":1.5,"checks":[{"key":"folder","ok":true,"title":"t","detail":"d","command":"c","launchbox":{"text":"x","command":"y","found":["/a"]}}],"sync":{"code":"pulling","label":"l","detail":"d","can_launch":false,"steps":[{"text":"t","command":"c"}],"conflicts":[{"kept_as":"/s/a.srm"}]},"systems":[{"id":"psx","name":"PlayStation","exts":[".cue"],"core":null}],"shelf":[{"key":"ff7","title":"Final Fantasy VII","year":1997,"platforms":"PS1","games":["a"]}],"games":[{"id":"a","title":"Final Fantasy VII","year":1997,"system":"psx","path":"/p","discs":3,"ff":["ff7"],"boxart":null,"last_played":1.0,"ready":true}],"last":"a","playing":{"title":"x"}}""",
        """{"ok":true,"result":{"queued":12}}""",
        """{"ok":false,"error":"no"}""",
        "launch --dry-run /g/gba/x.gba",
        "ffx-2",
        "import-saves /mnt/x",
    ];

    private static readonly string[] Splices = ["null", "{}", "[]", "\"\"", "-1", "1e999", "99999999999999999999", "\"\\ud800\"", "true", "\u0000", "}", "[", "\"", ",", "\n", "ff", "xii", " "];

    [Fact]
    public void ParsersNeverThrow()
    {
        var seed = int.TryParse(Environment.GetEnvironmentVariable("XIVARCADE_FUZZ_SEED"), out var s) ? s : Environment.TickCount;
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("XIVARCADE_FUZZ_SECONDS"), out var b) ? b : 2;
        var rng = new Random(seed);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var text = Mutate(rng);
            try
            {
                var state = ArcadeState.Parse(text);
                _ = state.EverythingElse().Count();
                ArcadeIpc.SearchJson(state, text, true);
                GhosttyWire.ReplyError(text);
                var request = ArcadeRequest.Parse(text);
                ArcadeCommands.For("/h/xiv-arcade", request, "id");
                ArcadeCommands.Score("Final Fantasy VII", text);
                WineHost.Quote(text);
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {seed}: {ex.GetType().Name} for {Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}: {ex.Message}");
            }
        }
    }

    private static string Mutate(Random rng)
    {
        var text = new StringBuilder(Corpus[rng.Next(Corpus.Length)]);
        for (var i = rng.Next(5); i > 0 && text.Length > 0; i--)
        {
            var at = rng.Next(text.Length);
            switch (rng.Next(5))
            {
                case 0: text.Remove(at, Math.Min(rng.Next(1, 12), text.Length - at)); break;
                case 1: text.Insert(at, Splices[rng.Next(Splices.Length)]); break;
                case 2: text[at] = (char)rng.Next(1, 0x250); break;
                case 3: text.Insert(at, text.ToString(at, Math.Min(rng.Next(1, 40), text.Length - at))); break;
                default: text.Length = at; break;
            }
        }

        return text.ToString();
    }
}
