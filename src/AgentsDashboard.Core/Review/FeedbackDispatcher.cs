using AgentsDashboard.Core.Model;
using AgentsDashboard.Core.Platform;

namespace AgentsDashboard.Core.Review;

/// <summary>
/// Submits a review: writes it, and offers it.
/// </summary>
/// <remarks>
/// The markdown file lands first and unconditionally, because it is the artifact
/// that survives everything else failing. The clipboard copy is next, so the
/// review can be pasted into the agent's terminal. Handing it to an agent
/// directly is the caller's business, because only the caller knows which agent
/// and whether it is one the dashboard can reach.
/// </remarks>
public sealed class FeedbackDispatcher(IClipboard clipboard, IClock? clock = null)
{
    /// <summary>Where reviews are written, relative to the worktree.</summary>
    public const string ReviewsDirName = ".agents-dashboard/reviews";

    private readonly IClock _clock = clock ?? new SystemClock();

    /// <summary>Writes the review and puts a prompt pointing at it on the clipboard.</summary>
    public async Task<ReviewSubmission> SubmitAsync(ReviewDraft draft, CancellationToken ct = default)
    {
        var at = _clock.Now;
        var relative = Path.Combine(ReviewsDirName, $"review-{at:yyyy-MM-dd-HHmm}.md").Replace('\\', '/');
        var full = Path.Combine(draft.WorktreePath, relative.Replace('/', Path.DirectorySeparatorChar));

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllTextAsync(full, ReviewMarkdownWriter.Render(draft, at), ct).ConfigureAwait(false);

        var prompt = ReviewMarkdownWriter.Prompt(relative, draft.Comments.Count);
        return new ReviewSubmission(full, prompt, clipboard.TryCopy(prompt), false, null);
    }
}
