using Tog.Core.Model;

namespace Tog.Core.Git;

/// <summary>Reads <c>git status</c> for a worktree.</summary>
public sealed class StatusReader(IGitCli git)
{
    /// <summary>
    /// Change counts and upstream position for one worktree, or null when git
    /// could not answer.
    /// </summary>
    /// <remarks>
    /// Porcelain v2 rather than v1 because it reports staged and unstaged state
    /// as separate columns and gives ahead/behind in the same call, so one git
    /// invocation answers the whole card.
    /// </remarks>
    public async Task<GitStatusInfo?> ReadAsync(string worktreePath, CancellationToken ct = default)
    {
        var result = await git
            .RunAsync(worktreePath, ["status", "--porcelain=v2", "--branch", "--untracked-files=normal"], ct)
            .ConfigureAwait(false);

        return result.Ok ? Parse(result.StdOut) : null;
    }

    /// <summary>Parses porcelain v2 output.</summary>
    public static GitStatusInfo Parse(string stdout)
    {
        var changed = 0;
        var staged = 0;
        var untracked = 0;
        var ahead = 0;
        var behind = 0;
        string? upstream = null;

        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            switch (line[0])
            {
                case '#':
                    ReadHeader(line, ref upstream, ref ahead, ref behind);
                    break;

                // "1" is an ordinary change, "2" a rename or copy. Both carry the
                // same two-character state field in the second column.
                case '1':
                case '2':
                {
                    var xy = Field(line, 1);
                    if (xy.Length == 2)
                    {
                        if (xy[0] != '.')
                        {
                            staged++;
                        }

                        if (xy[1] != '.')
                        {
                            changed++;
                        }
                    }

                    break;
                }

                // Unmerged. Counted as changed: it needs your attention either way.
                case 'u':
                    changed++;
                    break;

                case '?':
                    untracked++;
                    break;
            }
        }

        return new GitStatusInfo
        {
            Changed = changed,
            Staged = staged,
            Untracked = untracked,
            Ahead = ahead,
            Behind = behind,
            Upstream = upstream,
        };
    }

    private static void ReadHeader(string line, ref string? upstream, ref int ahead, ref int behind)
    {
        if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            upstream = line["# branch.upstream ".Length..];
            return;
        }

        if (!line.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            return;
        }

        // "+3 -1"
        foreach (var part in line["# branch.ab ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2 || !int.TryParse(part[1..], out var n))
            {
                continue;
            }

            if (part[0] == '+')
            {
                ahead = n;
            }
            else if (part[0] == '-')
            {
                behind = n;
            }
        }
    }

    /// <summary>The nth space-separated field of a porcelain line.</summary>
    private static string Field(string line, int index)
    {
        var start = 0;
        for (var i = 0; i < index; i++)
        {
            start = line.IndexOf(' ', start);
            if (start < 0)
            {
                return "";
            }

            start++;
        }

        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }
}
