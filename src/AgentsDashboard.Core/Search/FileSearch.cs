namespace AgentsDashboard.Core.Search;

/// <summary>One file found by Go to File, with the characters that matched.</summary>
/// <param name="Path">Worktree-relative, with forward slashes.</param>
/// <param name="Positions">Indexes into <paramref name="Path"/> of the matched characters, ascending.</param>
/// <param name="Recent">Found among the recently opened files rather than the rest.</param>
public sealed record FileMatch(string Path, int Score, IReadOnlyList<int> Positions, bool Recent);

/// <summary>What was typed into Go to File, taken apart.</summary>
/// <param name="Text">What to match paths against, without a line suffix.</param>
/// <param name="Line">A line asked for with <c>path:42</c>, or the line of <c>:42</c>.</param>
/// <param name="GoToLine">The query was <c>:</c> and a number: a line in the file already open.</param>
public sealed record FileQuery(string Text, int? Line, bool GoToLine)
{
    /// <summary>
    /// Reads a query the way VS Code's quick open does: a leading colon goes to
    /// a line in the current file, and a colon and a number after a path opens
    /// that path at the line. A column after the line (<c>:42:7</c>) is allowed
    /// and ignored, since compiler output is where these get copied from.
    /// </summary>
    public static FileQuery Parse(string? raw)
    {
        var text = (raw ?? "").Trim();
        if (text.StartsWith(':'))
        {
            return new FileQuery("", LineOf(text[1..]), GoToLine: true);
        }

        var colon = text.IndexOf(':');
        if (colon > 0 && LineOf(text[(colon + 1)..]) is { } line)
        {
            return new FileQuery(text[..colon].TrimEnd(), line, GoToLine: false);
        }

        return new FileQuery(text, null, GoToLine: false);
    }

    private static int? LineOf(string rest)
    {
        var digits = rest.Split(':', 2)[0].Trim();
        return int.TryParse(digits, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var line) && line > 0
            ? line
            : null;
    }
}

/// <summary>
/// Fuzzy file matching for Go to File: every typed character must appear in the
/// path, in order, but not next to each other.
/// </summary>
/// <remarks>
/// <para>
/// Ranked the way VS Code ranks: a match inside the file name beats one spread
/// across the directories, because the name is what people type. Within that,
/// characters that run together and characters at the start of a word (after a
/// slash, a dot, a dash, an underscore, or a lower-to-upper case change) score
/// higher, which is what makes <c>fdoc</c> find <c>FileDocument.razor</c> ahead
/// of <c>src/fixtures/docs/other.md</c>.
/// </para>
/// <para>
/// A query with spaces is several queries that must all match, and a piece with
/// a slash in it is matched against the whole path, so <c>panels/fd</c> means
/// "fd in something under panels".
/// </para>
/// <para>
/// The best-scoring alignment is found by a small dynamic program rather than
/// greedily: a greedy match of <c>doc</c> in <c>docs/FileDocument</c> takes the
/// first three letters and misses the word start later on. Every path is first
/// checked with a plain subsequence scan, which is linear and throws out most of
/// a large repository before the quadratic part runs.
/// </para>
/// </remarks>
public static class FileSearch
{
    /// <summary>How many results are kept. Past this nobody reads them, and a long list is slow to draw.</summary>
    public const int DefaultLimit = 50;

    private const int NameBonus = 1000;
    private const int NamePrefixBonus = 200;
    private const int ExactNameBonus = 400;

    /// <summary>
    /// Matches paths against a query. Recently opened files that match come
    /// first, as their own group, then the rest; each group best first.
    /// </summary>
    /// <param name="recent">Recently opened paths, most recent first. Only these can be in the first group.</param>
    public static IReadOnlyList<FileMatch> Find(
        IReadOnlyList<string> paths,
        string query,
        IReadOnlyList<string> recent,
        int limit = DefaultLimit)
    {
        var pieces = Pieces(query);
        if (pieces.Length == 0)
        {
            return [];
        }

        var recentSet = new HashSet<string>(recent, StringComparer.Ordinal);
        var found = new List<FileMatch>();
        foreach (var path in paths)
        {
            if (Match(path, pieces) is { } match)
            {
                found.Add(match with { Recent = recentSet.Contains(path) });
            }
        }

        // Recent files the list does not have (deleted since, or opened from a
        // link) are left out: opening them would fail.
        return found
            .OrderByDescending(m => m.Recent)
            .ThenByDescending(m => m.Score)
            .ThenBy(m => m.Path.Length)
            .ThenBy(m => m.Path, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    /// <summary>One path against a query, or null when some piece of it is not in the path.</summary>
    public static FileMatch? Match(string path, string query) => Match(path, Pieces(query));

    private static string[] Pieces(string query) =>
        query.Replace('\\', '/').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static FileMatch? Match(string path, string[] pieces)
    {
        if (pieces.Length == 0)
        {
            return null;
        }

        var nameStart = path.LastIndexOf('/') + 1;
        var positions = new SortedSet<int>();
        var total = 0;

        foreach (var piece in pieces)
        {
            // A piece without a slash tries the name first: a match there is the
            // one the person meant, even when the directories would score more.
            if (!piece.Contains('/') && Align(path, nameStart, piece) is { } inName)
            {
                total += inName.Score + NameBonus + NameShape(path[nameStart..], piece);
                positions.UnionWith(inName.Positions);
                continue;
            }

            if (Align(path, 0, piece) is not { } inPath)
            {
                return null;
            }

            total += inPath.Score;
            positions.UnionWith(inPath.Positions);
        }

        return new FileMatch(path, total, positions.ToArray(), Recent: false);
    }

    /// <summary>The name typed whole, or its start typed, is the strongest signal there is.</summary>
    private static int NameShape(string name, string piece)
    {
        var stem = name.IndexOf('.') is > 0 and var dot ? name[..dot] : name;
        if (name.Equals(piece, StringComparison.OrdinalIgnoreCase) || stem.Equals(piece, StringComparison.OrdinalIgnoreCase))
        {
            return ExactNameBonus;
        }

        return name.StartsWith(piece, StringComparison.OrdinalIgnoreCase) ? NamePrefixBonus : 0;
    }

    /// <summary>
    /// The best placement of <paramref name="piece"/> in <paramref name="text"/>
    /// from <paramref name="start"/> on, or null when it is not a subsequence.
    /// </summary>
    private static (int Score, int[] Positions)? Align(string text, int start, string piece)
    {
        var n = text.Length - start;
        var m = piece.Length;
        if (m == 0 || m > n || !IsSubsequence(text, start, piece))
        {
            return null;
        }

        // score[i, j]: the best score with piece[i] placed on text[start + j].
        // best[i, j]: the best score[i, k] for any k <= j, and where, so a gap
        // can jump from anywhere earlier without scanning back.
        const int None = int.MinValue / 2;
        var score = new int[m, n];
        var from = new int[m, n];
        var best = new int[m, n];
        var bestAt = new int[m, n];

        for (var i = 0; i < m; i++)
        {
            var want = char.ToLowerInvariant(piece[i]);
            for (var j = 0; j < n; j++)
            {
                score[i, j] = None;
                from[i, j] = -1;

                if (j >= i && char.ToLowerInvariant(text[start + j]) == want)
                {
                    var bonus = CharBonus(text, start + j, piece[i]);
                    if (i == 0)
                    {
                        // A little is taken off for every character skipped
                        // before the first match, so earlier is better when
                        // nothing else differs.
                        score[i, j] = bonus - Math.Min(j, 10);
                    }
                    else
                    {
                        var run = j > 0 && score[i - 1, j - 1] > None ? score[i - 1, j - 1] + Consecutive : None;
                        var jump = j > 1 && best[i - 1, j - 2] > None ? best[i - 1, j - 2] - 1 : None;
                        if (run >= jump && run > None)
                        {
                            score[i, j] = run + bonus;
                            from[i, j] = j - 1;
                        }
                        else if (jump > None)
                        {
                            score[i, j] = jump + bonus;
                            from[i, j] = bestAt[i - 1, j - 2];
                        }
                    }
                }

                var carried = j > 0 ? best[i, j - 1] : None;
                if (score[i, j] > carried)
                {
                    best[i, j] = score[i, j];
                    bestAt[i, j] = j;
                }
                else
                {
                    best[i, j] = carried;
                    bestAt[i, j] = j > 0 ? bestAt[i, j - 1] : -1;
                }
            }
        }

        if (best[m - 1, n - 1] <= None)
        {
            return null;
        }

        var positions = new int[m];
        var at = bestAt[m - 1, n - 1];
        for (var i = m - 1; i >= 0; i--)
        {
            positions[i] = start + at;
            at = from[i, at];
        }

        return (best[m - 1, n - 1], positions);
    }

    private const int Consecutive = 6;

    private static int CharBonus(string text, int index, char typed)
    {
        var bonus = 1;
        var c = text[index];
        if (c == typed)
        {
            bonus += 1;
        }

        if (index == 0)
        {
            return bonus + 8;
        }

        var before = text[index - 1];
        if (before is '/' or '.' or '-' or '_' or ' ')
        {
            bonus += 8;
        }
        else if (char.IsUpper(c) && char.IsLower(before))
        {
            bonus += 7;
        }

        return bonus;
    }

    private static bool IsSubsequence(string text, int start, string piece)
    {
        var i = 0;
        for (var j = start; j < text.Length && i < piece.Length; j++)
        {
            if (char.ToLowerInvariant(text[j]) == char.ToLowerInvariant(piece[i]))
            {
                i++;
            }
        }

        return i == piece.Length;
    }
}
