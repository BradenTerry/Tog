using System.Globalization;
using Togue.Core.Model;

namespace Togue.Core.Git;

/// <summary>
/// Parses <c>git diff</c> output into per-file hunks with both sides' line
/// numbers, which is what makes a comment anchorable to a line.
/// </summary>
/// <remarks>
/// File paths are taken from the <c>---</c>/<c>+++</c> and rename lines rather
/// than from the <c>diff --git</c> header, because the header concatenates both
/// paths with no delimiter that a path cannot itself contain. Callers pass
/// <c>-c core.quotePath=false</c> so non-ASCII paths arrive as themselves rather
/// than octal-escaped.
/// </remarks>
public static class UnifiedDiffParser
{
    public static IReadOnlyList<DiffFile> Parse(string diff)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrEmpty(diff))
        {
            return files;
        }

        Pending? current = null;

        void Flush()
        {
            if (current is not null)
            {
                files.Add(current.Build());
                current = null;
            }
        }

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                current = new Pending(line);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (current.InHunk && line.Length > 0 && line[0] is ' ' or '+' or '-')
            {
                current.AddLine(line);
                continue;
            }

            ReadHeaderLine(line, current);
        }

        Flush();
        return files;
    }

    private static void ReadHeaderLine(string line, Pending current)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            current.StartHunk(line);
            return;
        }

        if (line.StartsWith("new file mode", StringComparison.Ordinal))
        {
            current.Kind = FileChangeKind.Added;
        }
        else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
        {
            current.Kind = FileChangeKind.Deleted;
        }
        else if (line.StartsWith("rename from ", StringComparison.Ordinal)
                 || line.StartsWith("copy from ", StringComparison.Ordinal))
        {
            current.OldPath = line[(line.IndexOf("from ", StringComparison.Ordinal) + 5)..];
            current.Kind = FileChangeKind.Renamed;
        }
        else if (line.StartsWith("rename to ", StringComparison.Ordinal)
                 || line.StartsWith("copy to ", StringComparison.Ordinal))
        {
            current.NewPath = line[(line.IndexOf("to ", StringComparison.Ordinal) + 3)..];
            current.Kind = FileChangeKind.Renamed;
        }
        else if (line.StartsWith("Binary files ", StringComparison.Ordinal)
                 || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
        {
            current.IsBinary = true;
        }
        else if (line.StartsWith("--- ", StringComparison.Ordinal))
        {
            var p = StripPrefix(line[4..]);
            if (p is not null)
            {
                current.OldPath = p;
            }
        }
        else if (line.StartsWith("+++ ", StringComparison.Ordinal))
        {
            var p = StripPrefix(line[4..]);
            if (p is not null)
            {
                current.NewPath = p;
            }
        }
    }

    /// <summary>Drops git's <c>a/</c> or <c>b/</c> prefix. <c>/dev/null</c> means "no such side".</summary>
    /// <remarks>
    /// A unified diff header may carry a tab-separated field after the path, and
    /// git uses it whenever the path contains a space, precisely so the path stays
    /// unambiguous. Everything from that tab on is not part of the name.
    /// </remarks>
    private static string? StripPrefix(string path)
    {
        var tab = path.IndexOf('\t');
        if (tab >= 0)
        {
            path = path[..tab];
        }

        if (path is "/dev/null")
        {
            return null;
        }

        if (path.StartsWith("a/", StringComparison.Ordinal) || path.StartsWith("b/", StringComparison.Ordinal))
        {
            return path[2..];
        }

        return path;
    }

    private sealed class Pending(string header)
    {
        private readonly List<DiffHunk> _hunks = [];
        private List<DiffLine>? _lines;
        private DiffHunk? _hunk;
        private int _oldLine;
        private int _newLine;

        public string? OldPath { get; set; }
        public string? NewPath { get; set; }
        public FileChangeKind Kind { get; set; } = FileChangeKind.Modified;
        public bool IsBinary { get; set; }
        public bool InHunk => _hunk is not null;

        public void StartHunk(string line)
        {
            CloseHunk();

            // @@ -oldStart,oldCount +newStart,newCount @@ optional section
            var end = line.IndexOf("@@", 2, StringComparison.Ordinal);
            if (end < 0)
            {
                return;
            }

            var ranges = line[2..end].Trim();
            var section = line[(end + 2)..].Trim();

            var parts = ranges.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                return;
            }

            var (oldStart, oldCount) = ParseRange(parts[0]);
            var (newStart, newCount) = ParseRange(parts[1]);

            _oldLine = oldStart;
            _newLine = newStart;
            _lines = [];
            _hunk = new DiffHunk
            {
                OldStart = oldStart,
                OldCount = oldCount,
                NewStart = newStart,
                NewCount = newCount,
                Section = section.Length > 0 ? section : null,
            };
        }

        public void AddLine(string line)
        {
            if (_lines is null)
            {
                return;
            }

            var text = line.Length > 1 ? line[1..] : "";
            switch (line[0])
            {
                case ' ':
                    _lines.Add(new DiffLine(DiffLineKind.Context, _oldLine++, _newLine++, text));
                    break;
                case '-':
                    _lines.Add(new DiffLine(DiffLineKind.Removed, _oldLine++, null, text));
                    break;
                case '+':
                    _lines.Add(new DiffLine(DiffLineKind.Added, null, _newLine++, text));
                    break;
            }
        }

        public DiffFile Build()
        {
            CloseHunk();

            var path = NewPath ?? OldPath ?? FallbackPath();
            var kind = Kind;
            if (kind == FileChangeKind.Modified)
            {
                if (NewPath is null)
                {
                    kind = FileChangeKind.Deleted;
                }
                else if (OldPath is null)
                {
                    kind = FileChangeKind.Added;
                }
            }

            return new DiffFile
            {
                Path = path,
                OldPath = kind == FileChangeKind.Renamed ? OldPath : null,
                Kind = kind,
                IsBinary = IsBinary,
                Hunks = _hunks,
            };
        }

        private void CloseHunk()
        {
            if (_hunk is not null && _lines is not null)
            {
                _hunks.Add(_hunk with { Lines = _lines });
            }

            _hunk = null;
            _lines = null;
        }

        /// <summary>
        /// Last resort for a diff with neither <c>---</c>/<c>+++</c> nor rename
        /// lines, which is what a mode-only change looks like. The header holds
        /// both paths with no unambiguous separator, so prefer the reading where
        /// they are equal, which they are for everything but a rename.
        /// </summary>
        private string FallbackPath()
        {
            const string prefix = "diff --git ";
            if (!header.StartsWith(prefix, StringComparison.Ordinal))
            {
                return header;
            }

            var rest = header[prefix.Length..];
            if (!rest.StartsWith("a/", StringComparison.Ordinal))
            {
                return rest;
            }

            var last = -1;
            for (var i = rest.IndexOf(" b/", StringComparison.Ordinal);
                 i >= 0;
                 i = rest.IndexOf(" b/", i + 1, StringComparison.Ordinal))
            {
                last = i;
                var left = rest[2..i];
                var right = rest[(i + 3)..];
                if (left == right)
                {
                    return right;
                }
            }

            return last >= 0 ? rest[(last + 3)..] : rest[2..];
        }

        private static (int Start, int Count) ParseRange(string spec)
        {
            // "-12,7" or "+12" (a count of 1 is omitted).
            var body = spec.Length > 0 && spec[0] is '-' or '+' ? spec[1..] : spec;
            var comma = body.IndexOf(',');
            if (comma < 0)
            {
                return (ParseInt(body), 1);
            }

            return (ParseInt(body[..comma]), ParseInt(body[(comma + 1)..]));
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
