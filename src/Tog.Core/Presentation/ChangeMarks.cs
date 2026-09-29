using Tog.Core.Model;

namespace Tog.Core.Presentation;

/// <summary>How a line of the file on disk differs from the commit it is compared with.</summary>
public enum ChangeMarkKind
{
    /// <summary>A line that is new: nothing was removed where it is.</summary>
    Added,

    /// <summary>A line that replaced one or more removed lines.</summary>
    Modified,

    /// <summary>Lines were removed just below this one, with nothing added in their place.</summary>
    Deleted,
}

/// <summary>A gutter mark on one line of the file as it is now, 1-based.</summary>
public readonly record struct ChangeMark(int Line, ChangeMarkKind Kind);

/// <summary>
/// Turns one file's diff into the marks VS Code draws beside a changed file.
/// </summary>
/// <remarks>
/// The diff is read with no context lines, so each hunk is exactly one change:
/// only additions is an added block, only removals is a deletion, and a mix is a
/// modification, where every added line is marked modified, as VS Code does. A
/// deletion has no line of its own in the new file, so it is hung on the line
/// above the gap, or on the first line when the gap is at the top.
/// </remarks>
public static class ChangeMarks
{
    public static IReadOnlyList<ChangeMark> From(DiffFile? file)
    {
        if (file is null)
        {
            return [];
        }

        var marks = new List<ChangeMark>();
        foreach (var hunk in file.Hunks)
        {
            var added = hunk.Lines.Where(l => l.Kind == DiffLineKind.Added && l.NewLine is not null).ToList();
            var removed = hunk.Lines.Any(l => l.Kind == DiffLineKind.Removed);

            if (added.Count == 0)
            {
                if (removed)
                {
                    // With no context, a pure deletion's new start is the line
                    // before the gap, and zero when the gap is at the very top.
                    marks.Add(new ChangeMark(Math.Max(hunk.NewStart, 1), ChangeMarkKind.Deleted));
                }

                continue;
            }

            var kind = removed ? ChangeMarkKind.Modified : ChangeMarkKind.Added;
            marks.AddRange(added.Select(l => new ChangeMark(l.NewLine!.Value, kind)));
        }

        return marks.OrderBy(m => m.Line).ToList();
    }

    /// <summary>The first changed line, for landing on a file's changes when no line was asked for.</summary>
    public static int? First(IReadOnlyList<ChangeMark> marks) =>
        marks.Count == 0 ? null : marks.Min(m => m.Line);
}
