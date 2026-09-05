using System.Text;
using AgentsDashboard.Core.Model;

namespace AgentsDashboard.Core.Review;

/// <summary>
/// Renders a review as markdown an agent can act on.
/// </summary>
/// <remarks>
/// The shape matters more than the prose. Comments are grouped by file and
/// ordered by line, each one leading with its line range, so the agent reads a
/// list of located changes rather than a paragraph it has to map back onto the
/// code itself. That is the whole point of reviewing in the diff instead of
/// describing the problem in the terminal.
/// </remarks>
public static class ReviewMarkdownWriter
{
    public static string Render(ReviewDraft draft, DateTimeOffset at)
    {
        var sb = new StringBuilder();
        sb.Append("# Review ").Append(at.ToString("yyyy-MM-dd HH:mm")).Append('\n');

        if (!string.IsNullOrWhiteSpace(draft.Summary))
        {
            sb.Append('\n').Append(draft.Summary!.Trim()).Append('\n');
        }

        var byFile = draft.Comments
            .GroupBy(c => c.FilePath, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var file in byFile)
        {
            sb.Append("\n## ").Append(file.Key).Append('\n').Append('\n');

            foreach (var comment in file.OrderBy(c => c.StartLine).ThenBy(c => c.EndLine))
            {
                sb.Append("- **").Append(comment.Range).Append("**");

                if (comment.Side == DiffSide.Left)
                {
                    sb.Append(" (removed line)");
                }

                sb.Append(": ").Append(OneBlock(comment.Body)).Append('\n');

                if (!string.IsNullOrWhiteSpace(comment.Snippet))
                {
                    sb.Append('\n');
                    foreach (var line in comment.Snippet!.Split('\n'))
                    {
                        sb.Append("  > ").Append(line.TrimEnd('\r')).Append('\n');
                    }

                    sb.Append('\n');
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Folds a multi-line comment into one bullet. A raw newline would end the
    /// list item and orphan the rest of the comment from its line number.
    /// </summary>
    private static string OneBlock(string body)
    {
        var lines = body.Trim().Split('\n').Select(l => l.TrimEnd('\r').Trim());
        return string.Join("\n  ", lines.Where(l => l.Length > 0));
    }

    /// <summary>
    /// The prompt that points an agent at a written review. Deliberately short:
    /// the detail is in the file, and a long prompt would just repeat it.
    /// </summary>
    public static string Prompt(string relativePath, int commentCount)
    {
        var noun = commentCount == 1 ? "comment" : "comments";
        return $"I left {commentCount} review {noun} in `{relativePath}`. "
               + "Read it and make the changes. Each entry gives the file and the line range it refers to.";
    }
}
