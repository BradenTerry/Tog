using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;

namespace AgentsDashboard.Extensions.CSharpCode;

/// <summary>
/// Opens a worktree's C# projects into a Roslyn <see cref="Solution"/>.
/// </summary>
/// <remarks>
/// <para>
/// MSBuild has to be found before it is used. <c>MSBuildLocator</c> points the
/// process at the installed SDK's MSBuild, and it can only do that before any
/// <c>Microsoft.Build</c> type has been loaded. The JIT loads the types a method
/// mentions when it compiles that method, not when the line runs, so a single
/// method that both registers and creates the workspace would load MSBuild from
/// the wrong place before the registration line ever executed. Hence the split:
/// <see cref="EnsureMsBuildRegistered"/> names no MSBuild type,
/// <see cref="OpenAsync"/> names them all, both are
/// <see cref="MethodImplOptions.NoInlining"/> so the split survives the JIT, and
/// <see cref="LoadAsync"/> calls them in that order.
/// </para>
/// <para>
/// The workspace is deliberately not disposed. A <see cref="Solution"/> is an
/// immutable snapshot but its services still hang off the workspace that made
/// it, and the workspace stays reachable through the solution, so it lives
/// exactly as long as the solution the caller keeps and is collected with it.
/// </para>
/// <para>
/// MSBuild is registered for the process, not for this copy of the extension.
/// The locator hooks the default load context and MSBuild's assemblies land
/// there, where a reload of the extension cannot take them away, and the
/// locator refuses to register a second time once they are loaded. So a copy
/// that finds them already there, left by the copy before it, uses them as they
/// are, and a copy that registered takes its hook out again when it is disposed
/// so the hook does not keep the old copy in memory.
/// </para>
/// </remarks>
public sealed class SolutionLoader : IDisposable
{
    private static readonly Lock Gate = new();

    /// <summary>Directories that never hold a project worth opening.</summary>
    private static readonly string[] Skipped = ["bin", "obj", "node_modules", ".git", ".claude"];

    /// <summary>
    /// Load the solution or projects at a worktree root. Throws when there is
    /// nothing to load or MSBuild could not load it.
    /// </summary>
    public async Task<Solution> LoadAsync(
        string worktreePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(worktreePath))
        {
            throw new DirectoryNotFoundException($"No worktree at {worktreePath}.");
        }

        EnsureMsBuildRegistered();

        return await OpenAsync(worktreePath, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The solution file at the root, if there is one. <c>.slnx</c> wins over
    /// <c>.sln</c> because a repository carrying both is mid-migration and the
    /// <c>.slnx</c> is the one being edited.
    /// </summary>
    public static string? FindSolutionFile(string worktreePath) =>
        FirstByName(worktreePath, "*.slnx") ?? FirstByName(worktreePath, "*.sln");

    /// <summary>
    /// Project files at the root and in the directories under it. Two levels of
    /// directories covers the usual src/ and tests/ layout without walking a
    /// whole repository of build output.
    /// </summary>
    public static IReadOnlyList<string> FindProjectFiles(string worktreePath)
    {
        var projects = new List<string>(Directory.EnumerateFiles(worktreePath, "*.csproj"));

        foreach (var directory in Directory.EnumerateDirectories(worktreePath))
        {
            var name = Path.GetFileName(directory);
            if (Skipped.Contains(name, StringComparer.OrdinalIgnoreCase) || name.StartsWith('.'))
            {
                continue;
            }

            projects.AddRange(Directory.EnumerateFiles(directory, "*.csproj"));

            foreach (var nested in Directory.EnumerateDirectories(directory))
            {
                var nestedName = Path.GetFileName(nested);
                if (Skipped.Contains(nestedName, StringComparer.OrdinalIgnoreCase) || nestedName.StartsWith('.'))
                {
                    continue;
                }

                projects.AddRange(Directory.EnumerateFiles(nested, "*.csproj"));
            }
        }

        return [.. projects.OrderBy(p => p, StringComparer.Ordinal)];
    }

    private static string? FirstByName(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern).OrderBy(p => p, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Register the SDK's MSBuild, once per process. Nothing in here may name a
    /// <c>Microsoft.Build</c> or <c>Microsoft.CodeAnalysis.MSBuild</c> type; see
    /// the class remarks.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EnsureMsBuildRegistered()
    {
        lock (Gate)
        {
            if (!Microsoft.Build.Locator.MSBuildLocator.IsRegistered && !MsBuildAlreadyLoaded())
            {
                Microsoft.Build.Locator.MSBuildLocator.RegisterDefaults();
            }
        }
    }

    /// <summary>Whether an earlier copy of this extension already brought MSBuild into the process.</summary>
    private static bool MsBuildAlreadyLoaded() =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a =>
            a.GetName().Name is { } name
            && name.StartsWith("Microsoft.Build", StringComparison.Ordinal)
            && name != "Microsoft.Build.Locator");

    /// <summary>The extension is unloading: take the locator's hook out of the default context.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Dispose()
    {
        lock (Gate)
        {
            if (Microsoft.Build.Locator.MSBuildLocator.IsRegistered)
            {
                Microsoft.Build.Locator.MSBuildLocator.Unregister();
            }
        }
    }

    /// <summary>
    /// Everything that touches MSBuild, kept behind the registration above.
    /// </summary>
    /// <remarks>
    /// Roslyn 5.x opens an <c>.slnx</c> natively, so the file is handed straight
    /// to <c>OpenSolutionAsync</c> with no XML parsing of our own.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<Solution> OpenAsync(
        string worktreePath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var workspace = Microsoft.CodeAnalysis.MSBuild.MSBuildWorkspace.Create();
        workspace.LoadMetadataForReferencedProjects = true;

        var solutionFile = FindSolutionFile(worktreePath);
        Solution solution;

        if (solutionFile is not null)
        {
            progress?.Report($"Loading {Path.GetFileName(solutionFile)}...");
            solution = await workspace
                .OpenSolutionAsync(solutionFile, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            var projectFiles = FindProjectFiles(worktreePath);
            if (projectFiles.Count == 0)
            {
                throw new InvalidOperationException($"No solution or C# project found in {worktreePath}.");
            }

            var loaded = 0;
            foreach (var projectFile in projectFiles)
            {
                progress?.Report($"Loading {++loaded} of {projectFiles.Count} projects...");
                await workspace
                    .OpenProjectAsync(projectFile, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            solution = workspace.CurrentSolution;
        }

        var failures = workspace.Diagnostics
            .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure)
            .Select(d => d.Message)
            .ToList();

        if (!solution.Projects.Any())
        {
            throw new InvalidOperationException(failures.Count > 0
                ? $"MSBuild loaded no projects from {worktreePath}: {string.Join("; ", failures)}"
                : $"MSBuild loaded no projects from {worktreePath}.");
        }

        progress?.Report($"Loaded {solution.Projects.Count()} projects.");

        return solution;
    }
}
