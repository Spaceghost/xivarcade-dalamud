using System.Numerics;

namespace XivArcade.Core.Arcade;

/// <summary>A rectangle in the grid's own coordinates (origin at the grid's top-left, y grows downward).</summary>
public readonly record struct CoverRect(float X, float Y, float W, float H)
{
    public Vector2 Min => new(X, Y);

    public Vector2 Max => new(X + W, Y + H);

    public Vector2 Centre => new(X + (W / 2), Y + (H / 2));
}

/// <summary>How a console's retail box looks when no picture exists: its shape, its colour and its short mark.</summary>
public sealed record CoverStyle(float Aspect, uint Rgb, string Mark);

/// <summary>The maths of the cover wall. Pure, so it is tested on the host; the window only draws what this decides.</summary>
public static class CoverLayout
{
    /// <summary>The selected cover grows by this much.</summary>
    public const float FocusGrow = 0.10f;

    /// <summary>Covers that are not selected are dimmed to this brightness while another one has the focus.</summary>
    public const float RestBrightness = 0.80f;

    private static readonly CoverStyle Fallback = new(0.72f, 0x6B7280, "GAME");

    // width / height of the retail box, and a colour the console is remembered by
    private static readonly Dictionary<string, CoverStyle> Styles = new(StringComparer.Ordinal)
    {
        ["nes"] = new(0.72f, 0xC0392B, "NES"),
        ["snes"] = new(1.40f, 0x7B5EA7, "SNES"),
        ["n64"] = new(1.40f, 0x2E8B57, "N64"),
        ["gb"] = new(1.00f, 0x8A9A5B, "GB"),
        ["gbc"] = new(1.00f, 0x2AA198, "GBC"),
        ["gba"] = new(1.00f, 0x5C6BC0, "GBA"),
        ["nds"] = new(1.12f, 0x9AA5B1, "DS"),
        ["wswan"] = new(0.72f, 0x3D8EB9, "WS"),
        ["psx"] = new(1.00f, 0x8D99AE, "PS1"),
        ["ps2"] = new(0.71f, 0x2F4B8F, "PS2"),
        ["psp"] = new(0.58f, 0x4A5568, "PSP"),
        ["genesis"] = new(0.72f, 0xB8860B, "MD"),
    };

    private static readonly Dictionary<string, string> Marks = new(StringComparer.OrdinalIgnoreCase)
    {
        ["NES"] = "nes",
        ["Famicom"] = "nes",
        ["SNES"] = "snes",
        ["Super Famicom"] = "snes",
        ["N64"] = "n64",
        ["Game Boy"] = "gb",
        ["GB"] = "gb",
        ["GBC"] = "gbc",
        ["GBA"] = "gba",
        ["DS"] = "nds",
        ["WonderSwan"] = "wswan",
        ["PS1"] = "psx",
        ["PS2"] = "ps2",
        ["PSP"] = "psp",
        ["Mega Drive"] = "genesis",
        ["Genesis"] = "genesis",
    };

    public static CoverStyle Style(string system) => Styles.GetValueOrDefault(system, Fallback);

    /// <summary>The system a shelf placeholder is drawn as: the first platform in "SNES · PS1 · GBA".</summary>
    public static string SystemForPlatforms(string platforms)
    {
        var first = platforms.Split('·', 2)[0].Trim();
        return Marks.GetValueOrDefault(first, "");
    }

    /// <summary>How many cells of <paramref name="cell"/> width fit in <paramref name="available"/>; never fewer than one.</summary>
    public static int Columns(float available, float cell, float gap)
        => cell <= 0 ? 1 : Math.Max(1, (int)MathF.Floor((available + gap) / (cell + gap)));

    /// <summary>The largest rectangle of the given aspect inside the box: centred across, standing on the box's floor like a box on a shelf.</summary>
    public static CoverRect AspectFit(float aspect, CoverRect box)
    {
        if (!(aspect > 0) || float.IsInfinity(aspect))
            aspect = 1;
        var w = box.W;
        var h = w / aspect;
        if (h > box.H)
        {
            h = box.H;
            w = h * aspect;
        }

        return new CoverRect(box.X + ((box.W - w) / 2), box.Y + box.H - h, w, h);
    }

    public static CoverRect AspectFit(int width, int height, CoverRect box)
        => AspectFit(height <= 0 ? 1 : (float)width / height, box);

    /// <summary>Smoothstep: focus 0..1 in, eased 0..1 out.</summary>
    public static float Ease(float t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - (2 * t));
    }

    /// <summary>The cover scaled about its centre by the eased focus.</summary>
    public static CoverRect Focus(CoverRect r, float focus)
    {
        var s = 1 + (FocusGrow * Ease(focus));
        var c = r.Centre;
        return new CoverRect(c.X - (r.W * s / 2), c.Y - (r.H * s / 2), r.W * s, r.H * s);
    }

    /// <summary>1 for the focused cover, <see cref="RestBrightness"/> for the rest, eased in between.</summary>
    public static float Brightness(float focus) => RestBrightness + ((1 - RestBrightness) * Ease(focus));

    /// <summary>Frame-rate independent approach to a target: the same motion at 30 and 240 frames a second.</summary>
    public static float Approach(float current, float target, float dt, float rate)
    {
        if (dt <= 0)
            return current;
        var next = current + ((target - current) * (1 - MathF.Exp(-rate * dt)));
        return MathF.Abs(target - next) < 0.001f ? target : next;
    }

    /// <summary>The longest side a thumbnail is kept at; bigger pictures are scaled down once, off the draw thread.</summary>
    public static (int Width, int Height) Thumbnail(int width, int height, int longest)
    {
        if (width <= 0 || height <= 0)
            return (1, 1);
        var big = Math.Max(width, height);
        if (big <= longest)
            return (width, height);
        return (Math.Max(1, (int)MathF.Round((float)width * longest / big)), Math.Max(1, (int)MathF.Round((float)height * longest / big)));
    }
}

/// <summary>Shelves of equal cells under slim headers, and how the arrows move through them.</summary>
public sealed class CoverGrid
{
    private readonly List<CoverRect> cells = [];
    private readonly List<int> section = [];
    private readonly List<float> headers = [];
    private readonly List<int> firsts = [];

    public IReadOnlyList<CoverRect> Cells => cells;

    /// <summary>The y of each section's header.</summary>
    public IReadOnlyList<float> Headers => headers;

    /// <summary>The index of each section's first cell.</summary>
    public IReadOnlyList<int> Firsts => firsts;

    public int Columns { get; private set; } = 1;

    public float Height { get; private set; }

    public int SectionOf(int index) => index >= 0 && index < section.Count ? section[index] : 0;

    public void Build(IReadOnlyList<int> counts, float available, float cellW, float cellH, float gap, float headerH)
    {
        cells.Clear();
        section.Clear();
        headers.Clear();
        firsts.Clear();
        Columns = CoverLayout.Columns(available, cellW, gap);
        var y = 0f;
        for (var s = 0; s < counts.Count; s++)
        {
            headers.Add(y);
            firsts.Add(cells.Count);
            y += headerH;
            for (var n = 0; n < counts[s]; n++)
            {
                cells.Add(new CoverRect(n % Columns * (cellW + gap), y + (n / Columns * (cellH + gap)), cellW, cellH));
                section.Add(s);
            }

            var rows = (counts[s] + Columns - 1) / Columns;
            y += (rows * (cellH + gap)) + (rows == 0 ? gap : 0);
        }

        Height = y;
    }

    /// <summary>Left and right walk the list; up and down go to the nearest cover in the next row, across shelves.</summary>
    public int Move(int index, int dx, int dy)
    {
        if (cells.Count == 0)
            return 0;
        index = Math.Clamp(index, 0, cells.Count - 1);
        if (dx != 0)
            return Math.Clamp(index + dx, 0, cells.Count - 1);
        if (dy == 0)
            return index;
        var from = cells[index];
        var rowY = float.NaN;
        var best = index;
        var bestDistance = float.MaxValue;
        for (var i = index + dy; i >= 0 && i < cells.Count; i += dy)
        {
            var c = cells[i];
            if (c.Y == from.Y)
                continue;
            if (float.IsNaN(rowY))
                rowY = c.Y;
            else if (c.Y != rowY)
                break;
            var d = MathF.Abs(c.X - from.X);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Tab: the first cover of the next shelf (or the previous one), wrapping round.</summary>
    public int NextSection(int index, int direction)
    {
        if (firsts.Count == 0 || cells.Count == 0)
            return 0;
        var populated = new List<int>();
        for (var s = 0; s < firsts.Count; s++)
        {
            if (firsts[s] < cells.Count && section[firsts[s]] == s)
                populated.Add(s);
        }

        if (populated.Count == 0)
            return 0;
        var at = Math.Max(0, populated.IndexOf(SectionOf(index)));
        return firsts[populated[(((at + direction) % populated.Count) + populated.Count) % populated.Count]];
    }

    /// <summary>The scroll position that puts the cell in the middle of the view, kept inside the content.</summary>
    public static float ScrollTo(CoverRect cell, float gridTop, float viewHeight, float contentHeight)
        => Math.Clamp(gridTop + cell.Centre.Y - (viewHeight / 2), 0, Math.Max(0, contentHeight - viewHeight));
}

/// <summary>The words on a drawn cover, fitted to the space they get.</summary>
public sealed record CoverTitle(string Kicker, IReadOnlyList<string> Lines, float Size);

public static class CoverText
{
    private static readonly string[] Series = ["Final Fantasy", "Dragon Quest", "The Legend of Zelda", "Super Mario", "Mega Man"];

    /// <summary>"Final Fantasy VI" is set as a small FINAL FANTASY over a large VI; a title with no series is all headline.</summary>
    public static (string Kicker, string Main) Split(string title)
    {
        title = title.Trim();
        foreach (var s in Series)
        {
            if (title.Length > s.Length + 1 && title.StartsWith(s, StringComparison.OrdinalIgnoreCase) && (title[s.Length] == ' ' || title[s.Length] == ':'))
            {
                var rest = title[s.Length..].TrimStart(' ', ':', '-').Trim();
                if (rest.Length > 0)
                    return (s.ToUpperInvariant(), rest);
            }
        }

        return ("", title);
    }

    /// <summary>
    /// The largest size from <paramref name="maxSize"/> down to <paramref name="minSize"/> at which the text wraps
    /// into the box; at the smallest size whatever does not fit ends in an ellipsis.
    /// <paramref name="measure"/> is the width of a string at size 1 (text width is linear in size).
    /// </summary>
    public static CoverTitle Fit(string title, float width, float height, float maxSize, float minSize, Func<string, float> measure, float lineGap = 1.12f)
    {
        var (kicker, main) = Split(title);
        minSize = Math.Max(1, Math.Min(minSize, maxSize));
        for (var size = maxSize; ; size = Math.Max(minSize, size * 0.9f))
        {
            var maxLines = Math.Max(1, (int)(height / (size * lineGap)));
            var lines = Wrap(main, width / size, maxLines, measure, out var clipped);
            if (!clipped || size <= minSize)
                return new CoverTitle(kicker, lines, size);
        }
    }

    /// <summary>Greedy word wrap into at most <paramref name="maxLines"/> lines of <paramref name="width"/>; what is cut ends in an ellipsis.</summary>
    public static List<string> Wrap(string text, float width, int maxLines, Func<string, float> measure, out bool clipped)
    {
        clipped = false;
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (measure(candidate) <= width)
            {
                line = candidate;
                continue;
            }

            if (line.Length > 0)
            {
                if (lines.Count + 1 >= maxLines)
                {
                    clipped = true;
                    lines.Add(Ellipsis(line, width, measure));
                    return lines;
                }

                lines.Add(line);
            }

            line = word;
            if (measure(line) > width)
            {
                clipped = true; // a single word wider than the box: a smaller size may still hold it
                line = Ellipsis(line, width, measure);
            }
        }

        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }

    private static string Ellipsis(string line, float width, Func<string, float> measure)
    {
        while (line.Length > 1 && measure(line + "…") > width)
            line = line[..^1].TrimEnd();
        return line + "…";
    }
}

/// <summary>
/// Pictures by path, loaded in the background and kept least-recently-used. <see cref="Get"/> never waits: it
/// returns the picture if it is here and otherwise starts the load and returns nothing, so a frame is never
/// blocked. Everything evicted, everything that arrives after it stopped being wanted, and everything left at
/// <see cref="Dispose"/> is disposed exactly once.
/// </summary>
public sealed class CoverCache<T> : IDisposable
    where T : class, IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> order = new(); // most recent first
    private readonly Func<string, CancellationToken, Task<T?>> load;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopping; // taken once: the source is disposed while loads may still be starting
    private readonly int capacity;
    private readonly int maxLoading;
    private int loading;
    private bool disposed;

    public CoverCache(int capacity, int maxLoading, Func<string, CancellationToken, Task<T?>> load)
    {
        this.capacity = Math.Max(1, capacity);
        this.maxLoading = Math.Max(1, maxLoading);
        this.load = load;
        stopping = stop.Token;
    }

    private sealed class Entry(string key)
    {
        public string Key { get; } = key;

        public T? Value { get; set; }

        public bool Loading { get; set; }

        public bool Failed { get; set; }

        public bool Dropped { get; set; }
    }

    public int Count
    {
        get
        {
            lock (gate)
                return map.Count;
        }
    }

    /// <summary>The picture if it is loaded. Otherwise null, and a load is started unless enough are already running or this one failed before.</summary>
    public T? Get(string key)
    {
        Entry entry;
        lock (gate)
        {
            if (disposed)
                return null;
            if (map.TryGetValue(key, out var node))
            {
                order.Remove(node);
                order.AddFirst(node);
                return node.Value.Value;
            }

            if (loading >= maxLoading)
                return null; // asked again next frame; the queue is the screen itself
            entry = new Entry(key) { Loading = true };
            map[key] = order.AddFirst(entry);
            loading++;
            Trim();
        }

        _ = Task.Run(() => Run(entry));
        return null;
    }

    /// <summary>Forget a path (the file changed); the next <see cref="Get"/> loads it again.</summary>
    public void Invalidate(string key)
    {
        lock (gate)
        {
            if (map.Remove(key, out var node))
                Drop(node);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            foreach (var node in map.Values.ToList())
                Drop(node);
            map.Clear();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
        }

        stop.Cancel();
        Clear();
        stop.Dispose();
    }

    private async Task Run(Entry entry)
    {
        T? value = null;
        try
        {
            value = await load(entry.Key, stopping).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // a picture that cannot be read is a game without a picture: the drawn cover stands in
        }

        lock (gate)
        {
            loading--;
            entry.Loading = false;
            if (!entry.Dropped && !disposed)
            {
                entry.Value = value;
                entry.Failed = value == null;
                return;
            }
        }

        value?.Dispose();
    }

    // callers hold the gate
    private void Trim()
    {
        var node = order.Last;
        while (map.Count > capacity && node != null)
        {
            var previous = node.Previous;
            if (!node.Value.Loading)
            {
                map.Remove(node.Value.Key);
                Drop(node);
            }

            node = previous;
        }
    }

    private void Drop(LinkedListNode<Entry> node)
    {
        order.Remove(node);
        node.Value.Dropped = true;
        node.Value.Value?.Dispose();
        node.Value.Value = null;
    }
}
