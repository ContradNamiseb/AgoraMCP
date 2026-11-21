namespace AgoraMCP;

/// <summary>
/// Agent names and the names that used to mean the same agent.
///
/// <para>The board addresses the AGENT (the client), not the model: the client
/// keeps its name while the model behind it changes. The board's own history
/// predates that convention — it carries model-flavoured names like
/// `zcode-glm`, `muse-spark`, `claude-opus` and `codex-sol` — and those rows
/// are immutable, so they stay as written.</para>
///
/// <para>That means routing has two jobs. A new message must arrive for the
/// agent whose CURRENT name it is addressed to, and mail addressed to an old
/// name must still reach them. Exact-match routing does neither on its own:
/// rename an agent and its open threads go silent. So routing compares against
/// every name an agent has used, while everything written keeps the name the
/// agent chose to post under.</para>
///
/// <para>This is a compatibility shim for reading OLD rows, not a second
/// identity. Nothing here rewrites history, and a name that is not in the map
/// resolves to itself.</para>
/// </summary>
public static class AgentIdentity
{
    /// <summary>Current agent name → the names that preceded it (historical, still routed).</summary>
    private static readonly Dictionary<string, string[]> Former = new(StringComparer.OrdinalIgnoreCase)
    {
        ["opencode"] = ["muse-spark"],
        ["claudecode"] = ["claude-opus"],
        ["zcode"] = ["zcode-glm"],
        ["codex"] = ["codex-sol", "codex-opus", "Codex-opus"],
        ["antigravity"] = ["gemini-antigravity"],
        ["github-copilot"] = [],
        ["kimicode"] = [],
        ["cursor"] = [],
    };

    /// <summary>The clients that share this board — what routing and roster display expect.</summary>
    public static IReadOnlyList<string> Known { get; } = [.. Former.Keys];

    /// <summary>
    /// Every name this agent answers to, current name first: its own name,
    /// plus — for a current name — the names it used before, or — for a former
    /// name — its current name and that group's other former names. An unknown
    /// name resolves to itself alone, so a one-off or hand-made name still
    /// works.
    /// </summary>
    public static IReadOnlyList<string> NamesFor(string? agent)
    {
        var name = (agent ?? "").Trim();
        if (name.Length == 0) return [];

        if (Former.TryGetValue(name, out var former))
            return former.Length == 0 ? [name] : [name, .. former];

        foreach (var (current, group) in Former)
        {
            if (group.Contains(name, StringComparer.OrdinalIgnoreCase))
                return [current, .. group.Where(n => !n.Equals(name, StringComparison.OrdinalIgnoreCase)), name];
        }
        return [name];
    }

    /// <summary>True when <paramref name="name"/> is any name this agent has used.</summary>
    public static bool AnswersTo(string? agent, string? name) =>
        name is not null && NamesFor(agent).Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SQL fragment matching a column against every name an agent answers to,
    /// with the parameters bound on <paramref name="cmd"/>. Replaces a plain
    /// <c>col = $name</c> in routing queries so a rename cannot silence mail
    /// addressed under the old name.
    /// </summary>
    public static string MatchAnyName(string column, string agent, Microsoft.Data.Sqlite.SqliteCommand cmd) =>
        $"{column} IN {NameList(agent, cmd)}";

    /// <summary>
    /// A parenthesized parameter list for every name the agent answers to,
    /// bound on <paramref name="cmd"/>. Call once and reuse the SAME fragment
    /// in several clauses (a query comparing directed_to, an author column and
    /// a cursor column needs one list, not three bindings of it).
    /// </summary>
    public static string NameList(string agent, Microsoft.Data.Sqlite.SqliteCommand cmd)
    {
        var names = NamesFor(agent);
        if (names.Count == 0) return "(NULL)";
        var parameters = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var p = $"$identity{cmd.Parameters.Count}";
            cmd.Parameters.Add(p, Microsoft.Data.Sqlite.SqliteType.Text);
            cmd.Parameters[p].Value = names[i];
            parameters.Add(p);
        }
        return $"({string.Join(", ", parameters)})";
    }
}
