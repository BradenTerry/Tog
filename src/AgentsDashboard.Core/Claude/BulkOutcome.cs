namespace AgentsDashboard.Core.Claude;

/// <summary>
/// Folds the results of the same command run over several agents into one answer.
/// </summary>
/// <remarks>
/// Kept away from the process running so the wording can be tested: a bulk stop
/// that half worked is exactly the case a user needs told plainly, and it is the
/// case that never shows up while developing.
/// </remarks>
public static class BulkOutcome
{
    /// <summary>
    /// One result for many, naming every agent the command did not work on.
    /// </summary>
    /// <param name="done">The past tense of what happened, such as "Stopped".</param>
    /// <param name="verb">The plain verb for the failures, such as "stop".</param>
    /// <param name="results">Each agent's id with what the CLI said about it.</param>
    public static CliResult Summarise(
        string done,
        string verb,
        IReadOnlyList<(string Id, CliResult Result)> results)
    {
        var succeeded = results.Count(r => r.Result.Ok);
        var parts = new List<string> { $"{done} {succeeded}." };

        foreach (var (id, result) in results.Where(r => !r.Result.Ok))
        {
            var said = result.Message.Trim();
            parts.Add(said.Length > 0
                ? $"Could not {verb} {id}: {said}"
                : $"Could not {verb} {id}.");
        }

        return new CliResult(succeeded == results.Count, string.Join(' ', parts));
    }
}
