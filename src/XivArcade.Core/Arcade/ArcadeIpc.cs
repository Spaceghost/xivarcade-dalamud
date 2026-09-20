using System.Text.Json.Nodes;

namespace XivArcade.Core.Arcade;

/// <summary>The payload of <c>XivArcade.v1.Search</c>, built here so it can be tested without the game.</summary>
public static class ArcadeIpc
{
    public const int MaxResults = 20;

    public static string SearchJson(ArcadeState state, string? query, bool canLaunch)
    {
        var result = new JsonArray();
        var q = (query ?? "").Trim();
        if (q.Length == 0)
            return result.ToJsonString();
        foreach (var (game, score) in state.Games
            .Select(g => (Game: g, Score: ArcadeCommands.Score(g.Title, q)))
            .Where(x => x.Score != null)
            .OrderByDescending(x => x.Score)
            .Take(MaxResults))
        {
            result.Add(new JsonObject
            {
                ["id"] = game.Id,
                ["title"] = game.Title,
                ["subtitle"] = state.SystemName(game.System) + (game.Year is { } y ? $" · {y}" : "") + (game.Discs > 1 ? $" · {game.Discs} discs" : ""),
                ["ready"] = game.Ready && canLaunch,
                ["score"] = score,
            });
        }

        return result.ToJsonString();
    }
}
