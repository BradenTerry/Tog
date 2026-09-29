namespace Togue.Extensions;

/// <summary>
/// Navigation for one language in the editor: what a symbol is, where it is
/// defined, where it is used, who calls it, and what kind of thing each name in
/// a file is. Registered with <see cref="IExtensionBuilder.AddCodeIntelligence{TProvider}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The app draws everything: the chip and its Load button on the file's bar,
/// hovers, the definition jump, the results panel, the colouring. The provider
/// only answers. It is a singleton for as long as the extension is loaded, and
/// is disposed with it when it implements <see cref="IDisposable"/>.
/// </para>
/// <para>
/// Nothing is loaded until the user asks for it in a worktree with
/// <see cref="LoadAsync"/>. Until then every query answers with nothing, and a
/// query must never start a load of its own: indexing a repository is the most
/// expensive thing Togue could do, and most worktrees are only ever read
/// as a diff. A provider can offer a setting to load on opening a file instead,
/// through <see cref="LoadsOnOpen"/>; the app starts that load, not a query.
/// </para>
/// <para>
/// Paths are worktree-relative with forward slashes. Lines and columns are
/// one-based, as the editor counts them.
/// </para>
/// </remarks>
public interface ICodeIntelligence
{
    /// <summary>What the chip calls this, such as "Roslyn".</summary>
    string Name { get; }

    /// <summary>The editor's language id for the files this answers for, such as "csharp".</summary>
    string Language { get; }

    /// <summary>Whether this answers for a file. Answer from the path alone: it runs on every render.</summary>
    bool Handles(string relativePath);

    /// <summary>Raised with the worktree path whenever its load state changes, on any thread.</summary>
    event Action<string>? StatusChanged;

    /// <summary>Where a worktree's load is, for the chip. Answer from memory.</summary>
    CodeLoadStatus Status(string worktreePath);

    /// <summary>
    /// Start loading a worktree, or join the load in flight. Cancelling the token
    /// stops the caller waiting, not the load: the load belongs to the worktree.
    /// </summary>
    Task LoadAsync(string worktreePath, CancellationToken cancellationToken);

    /// <summary>
    /// Whether opening a file it handles in a worktree that is not loaded should
    /// load it, rather than wait for Load. False unless the provider says
    /// otherwise, which it should only do when the user asked for it in one of
    /// its settings. Answer from memory: it is asked on the window's thread.
    /// Since API 1.9.
    /// </summary>
    bool LoadsOnOpen(string worktreePath) => false;

    /// <summary>Drop what is loaded and load it again, for when a project file changed.</summary>
    Task ReloadAsync(string worktreePath, CancellationToken cancellationToken);

    /// <summary>Drop what is loaded, or stop a load in flight, and free its memory.</summary>
    void Unload(string worktreePath);

    /// <summary>Unsaved editor text, after a pause in typing, so answers are for the buffer.</summary>
    void UpdateDocument(string worktreePath, string relativeFile, string text);

    /// <summary>Read one file again from disk, after a save or after an agent changed it.</summary>
    void RefreshDocumentFromDisk(string worktreePath, string relativeFile);

    /// <summary>What the symbol at a position is, or null.</summary>
    Task<HoverInfo?> HoverAsync(string worktreePath, string relativeFile, int line, int column, CancellationToken cancellationToken);

    /// <summary>What kind of symbol every name in a file is, for colouring.</summary>
    Task<IReadOnlyList<ClassifiedRun>> ClassifyAsync(string worktreePath, string relativeFile, CancellationToken cancellationToken);

    /// <summary>Where the symbol at a position is declared.</summary>
    Task<IReadOnlyList<CodeLocation>> DefinitionAsync(string worktreePath, string relativeFile, int line, int column, CancellationToken cancellationToken);

    /// <summary>Every use of the symbol at a position.</summary>
    Task<IReadOnlyList<CodeLocation>> ReferencesAsync(string worktreePath, string relativeFile, int line, int column, CancellationToken cancellationToken);

    /// <summary>Callers and callees of the method at a position, or null.</summary>
    Task<CallHierarchy?> CallHierarchyAsync(string worktreePath, string relativeFile, int line, int column, CancellationToken cancellationToken);
}

/// <summary>A span of source the editor can jump to.</summary>
/// <remarks>
/// Positions are one-based in both line and column, which is the editor's
/// convention. A provider whose compiler counts from zero converts in one place:
/// an off-by-one here is invisible everywhere except "go to definition lands on
/// the line above".
/// </remarks>
/// <param name="Path">Worktree-relative path with forward slashes.</param>
/// <param name="Line">One-based.</param>
/// <param name="Column">One-based.</param>
/// <param name="EndLine">One-based.</param>
/// <param name="EndColumn">One-based, exclusive.</param>
/// <param name="Preview">The trimmed text of the line, for a results panel.</param>
/// <param name="Container">Containing type and member, such as "ClaudeCli.SendAsync".</param>
public sealed record CodeLocation(
    string Path,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Preview,
    string? Container);

/// <summary>What the editor shows when the pointer rests on a symbol.</summary>
/// <param name="Signature">The symbol as source would write it, minimally qualified.</param>
/// <param name="Summary">The doc summary, tags stripped and whitespace collapsed.</param>
public sealed record HoverInfo(string Signature, string? Summary);

/// <summary>A name in a file and the kind of symbol it is, for colouring.</summary>
/// <param name="Line">One-based, as the editor counts.</param>
/// <param name="Column">One-based.</param>
/// <param name="Length">In UTF-16 code units, as the editor measures.</param>
/// <param name="Kind">A Monaco semantic token type: class, method, parameter and so on.</param>
public sealed record ClassifiedRun(int Line, int Column, int Length, string Kind);

/// <summary>One end of a call relationship: a symbol and where the calls are.</summary>
/// <param name="Name">The symbol's name.</param>
/// <param name="Container">Its containing type, if any.</param>
/// <param name="Location">Where the symbol itself is declared.</param>
/// <param name="CallSites">The individual calls, in the caller's file.</param>
public sealed record CallHierarchyItem(
    string Name,
    string? Container,
    CodeLocation Location,
    IReadOnlyList<CodeLocation> CallSites);

/// <summary>Who calls the method under the caret, and what it calls.</summary>
/// <param name="Target">The method itself.</param>
/// <param name="Callers">Methods that call it.</param>
/// <param name="Callees">Methods it calls.</param>
public sealed record CallHierarchy(
    CodeLocation Target,
    IReadOnlyList<CallHierarchyItem> Callers,
    IReadOnlyList<CallHierarchyItem> Callees);

/// <summary>Where a worktree is in its load.</summary>
public enum CodeLoadState
{
    /// <summary>Off. Nothing is loaded until the user asks.</summary>
    NotLoaded,

    /// <summary>Loading; queries wait for it.</summary>
    Loading,

    /// <summary>Loaded and answering.</summary>
    Ready,

    /// <summary>The load failed; the message says why.</summary>
    Failed,
}

/// <summary>The chip the editor shows for a worktree's load.</summary>
/// <param name="State">Where the load is.</param>
/// <param name="Message">Progress while loading, the reason when failed, else null.</param>
/// <param name="Projects">Projects loaded, when ready.</param>
/// <param name="Documents">Documents loaded, when ready.</param>
public sealed record CodeLoadStatus(CodeLoadState State, string? Message, int Projects, int Documents);
