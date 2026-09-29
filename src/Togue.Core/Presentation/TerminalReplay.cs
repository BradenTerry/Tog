using System.Text;

namespace Togue.Core.Presentation;

/// <summary>
/// Turns captured terminal output back into the text a terminal would show.
/// </summary>
/// <remarks>
/// <para>
/// <c>claude logs</c> returns the raw byte stream Claude's interface wrote to its
/// terminal, and that interface draws a screen rather than printing lines. It
/// moves the cursor instead of printing spaces (<c>ESC[1C</c> is a space the
/// terminal never receives), jumps to absolute positions, and redraws a spinner in
/// place many times a second. Shown as it comes, every word runs into the next,
/// every spinner frame leaves its digits behind, and the escape characters
/// themselves draw as boxes.
/// </para>
/// <para>
/// So the stream is replayed onto a grid of cells, the way a terminal would, and
/// the grid is what is shown. Lines that scroll off the top are kept above it,
/// since that is the history someone opening the log wants. Colour and every other
/// mode change are dropped: the point is legible text, not a terminal emulator.
/// </para>
/// <para>
/// The screen height is read from the stream itself, as the lowest row it ever
/// positions the cursor on, because scrolling only happens at the bottom of the
/// screen and the log does not say how tall the terminal was. A stream that never
/// positions the cursor is plain output and never scrolls.
/// </para>
/// </remarks>
public static class TerminalReplay
{
    private const char Esc = '\u001b';

    public static string Render(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return "";
        }

        if (raw.IndexOf(Esc) < 0 && raw.IndexOf('\r') < 0 && raw.IndexOf('\b') < 0)
        {
            return raw;
        }

        var screen = new Screen(HeightOf(raw), bareLineFeeds: raw.IndexOf('\r') < 0);
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == Esc)
            {
                i = Escape(raw, i + 1, screen);
                continue;
            }

            switch (c)
            {
                case '\r':
                    screen.Col = 0;
                    break;
                case '\n':
                    screen.LineFeed();
                    break;
                case '\b':
                    screen.Col = Math.Max(0, screen.Col - 1);
                    break;
                case '\t':
                    screen.Col = (screen.Col / 8 + 1) * 8;
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < raw.Length && char.IsLowSurrogate(raw[i + 1]))
                    {
                        screen.Print(raw.Substring(i, 2));
                        i++;
                    }
                    else if (IsJoining(c))
                    {
                        screen.Join(c);
                    }
                    else if (c >= ' ' && c != '\u007f' && !(c >= '\u0080' && c < ' '))
                    {
                        screen.Print(c.ToString());
                    }

                    // Any other control character (a bell, a stray C1 code) moves
                    // nothing and draws nothing.
                    break;
            }

            i++;
        }

        return screen.Text();
    }

    /// <summary>Marks and selectors that belong to the character before them rather than taking a cell.</summary>
    private static bool IsJoining(char c) =>
        c is '‍' or (>= '︀' and <= '️') or (>= '̀' and <= 'ͯ');

    /// <summary>
    /// The screen height the stream implies: the lowest row any absolute position
    /// names, or no limit when it names none.
    /// </summary>
    private static int HeightOf(string raw)
    {
        var max = 0;
        var i = raw.IndexOf(Esc);
        while (i >= 0 && i + 1 < raw.Length)
        {
            if (raw[i + 1] == '[')
            {
                var j = i + 2;
                var first = 0;
                while (j < raw.Length && char.IsAsciiDigit(raw[j]))
                {
                    first = Math.Min(first * 10 + (raw[j] - '0'), 10_000);
                    j++;
                }

                while (j < raw.Length && (char.IsAsciiDigit(raw[j]) || raw[j] == ';'))
                {
                    j++;
                }

                if (j < raw.Length && raw[j] is 'H' or 'f' or 'd')
                {
                    max = Math.Max(max, first);
                }
            }

            i = raw.IndexOf(Esc, i + 1);
        }

        return max == 0 ? int.MaxValue : Math.Max(max, 2);
    }

    /// <summary>Handles one escape sequence starting after the ESC, and returns where the next input starts.</summary>
    private static int Escape(string raw, int i, Screen screen)
    {
        if (i >= raw.Length)
        {
            return i;
        }

        var kind = raw[i];
        switch (kind)
        {
            case '[':
                return Csi(raw, i + 1, screen);

            // String sequences (window titles, hyperlinks, device control) run to
            // BEL or to ESC backslash and draw nothing.
            case ']' or 'P' or 'X' or '^' or '_':
                for (var j = i + 1; j < raw.Length; j++)
                {
                    if (raw[j] == '\u0007')
                    {
                        return j + 1;
                    }

                    if (raw[j] == Esc && j + 1 < raw.Length && raw[j + 1] == '\\')
                    {
                        return j + 2;
                    }
                }

                return raw.Length;

            case '7':
                screen.Save();
                return i + 1;
            case '8':
                screen.Restore();
                return i + 1;
            case 'D':
                screen.LineFeed();
                return i + 1;
            case 'E':
                screen.Col = 0;
                screen.LineFeed();
                return i + 1;
            case 'M':
                screen.Row = Math.Max(0, screen.Row - 1);
                return i + 1;
            case 'c':
                screen.EraseDisplay(2);
                screen.Row = screen.Col = 0;
                return i + 1;

            // Character set designations carry one more byte.
            case '(' or ')' or '*' or '+' or '#' or '%':
                return Math.Min(i + 2, raw.Length);

            default:
                return i + 1;
        }
    }

    private static int Csi(string raw, int i, Screen screen)
    {
        var start = i;
        while (i < raw.Length && raw[i] is >= '0' and <= '?')
        {
            i++;
        }

        var parameters = raw[start..i];
        while (i < raw.Length && raw[i] is >= ' ' and <= '/')
        {
            i++;
        }

        if (i >= raw.Length)
        {
            return i;
        }

        var final = raw[i];

        // Private sequences (mode switches, cursor visibility, bracketed paste)
        // change how a terminal behaves, not what it shows.
        if (parameters.Length > 0 && parameters[0] is '?' or '>' or '=' or '<')
        {
            return i + 1;
        }

        var args = parameters.Split(';');
        int Arg(int index, int fallback) =>
            index < args.Length && int.TryParse(args[index], out var v) && v > 0 ? Math.Min(v, 10_000) : fallback;

        switch (final)
        {
            case 'A':
                screen.Row = Math.Max(0, screen.Row - Arg(0, 1));
                break;
            case 'B' or 'e':
                screen.Row = screen.ClampRow(screen.Row + Arg(0, 1));
                break;
            case 'C' or 'a':
                screen.Col += Arg(0, 1);
                break;
            case 'D':
                screen.Col = Math.Max(0, screen.Col - Arg(0, 1));
                break;
            case 'E':
                screen.Row = screen.ClampRow(screen.Row + Arg(0, 1));
                screen.Col = 0;
                break;
            case 'F':
                screen.Row = Math.Max(0, screen.Row - Arg(0, 1));
                screen.Col = 0;
                break;
            case 'G' or '`':
                screen.Col = Arg(0, 1) - 1;
                break;
            case 'H' or 'f':
                screen.Row = screen.ClampRow(Arg(0, 1) - 1);
                screen.Col = Arg(1, 1) - 1;
                break;
            case 'd':
                screen.Row = screen.ClampRow(Arg(0, 1) - 1);
                break;
            case 'J':
                screen.EraseDisplay(args[0] is "" ? 0 : Arg(0, 0));
                break;
            case 'K':
                screen.EraseLine(args[0] is "" ? 0 : Arg(0, 0));
                break;
            case 'X':
                screen.EraseChars(Arg(0, 1));
                break;
            case 'P':
                screen.DeleteChars(Arg(0, 1));
                break;
            case '@':
                screen.InsertChars(Arg(0, 1));
                break;
            case 'L':
                screen.InsertLines(Arg(0, 1));
                break;
            case 'M':
                screen.DeleteLines(Arg(0, 1));
                break;
            case 'S':
                for (var n = Arg(0, 1); n > 0; n--)
                {
                    screen.ScrollUp();
                }

                break;
            case 's':
                screen.Save();
                break;
            case 'u':
                screen.Restore();
                break;

            // Colour (m), scroll regions (r), reports (n) and the rest draw nothing.
        }

        return i + 1;
    }

    /// <summary>The grid a stream is replayed onto.</summary>
    private sealed class Screen(int height, bool bareLineFeeds)
    {
        private readonly List<List<string?>> _rows = [[]];
        private readonly List<string> _scrollback = [];
        private int _savedRow;
        private int _savedCol;

        public int Row { get; set; }

        public int Col
        {
            get;
            set => field = Math.Clamp(value, 0, 10_000);
        }

        public int ClampRow(int row) => Math.Clamp(row, 0, height == int.MaxValue ? 100_000 : height - 1);

        private List<string?> Line(int row)
        {
            while (_rows.Count <= row)
            {
                _rows.Add([]);
            }

            return _rows[row];
        }

        public void Print(string cell)
        {
            var line = Line(Row);
            while (line.Count <= Col)
            {
                line.Add(null);
            }

            line[Col] = cell;
            Col++;
        }

        /// <summary>Attaches a combining mark or variation selector to the cell before the cursor.</summary>
        public void Join(char c)
        {
            var line = Line(Row);
            var target = Col - 1;
            if (target >= 0 && target < line.Count && line[target] is { } cell)
            {
                line[target] = cell + c;
            }
        }

        /// <summary>
        /// Down one row, scrolling at the bottom. A stream with no carriage returns
        /// at all is plain text whose line feeds mean "new line", so they also
        /// return to the first column.
        /// </summary>
        public void LineFeed()
        {
            if (bareLineFeeds)
            {
                Col = 0;
            }

            if (Row + 1 >= height)
            {
                ScrollUp();
            }
            else
            {
                Row++;
            }
        }

        public void ScrollUp()
        {
            _scrollback.Add(Render(Line(0)));
            _rows.RemoveAt(0);
            Line(Row);
        }

        public void EraseLine(int mode)
        {
            var line = Line(Row);
            switch (mode)
            {
                case 0:
                    if (Col < line.Count)
                    {
                        line.RemoveRange(Col, line.Count - Col);
                    }

                    break;
                case 1:
                    for (var c = 0; c <= Col && c < line.Count; c++)
                    {
                        line[c] = null;
                    }

                    break;
                default:
                    line.Clear();
                    break;
            }
        }

        public void EraseDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    EraseLine(0);
                    for (var r = Row + 1; r < _rows.Count; r++)
                    {
                        _rows[r].Clear();
                    }

                    break;
                case 1:
                    EraseLine(1);
                    for (var r = 0; r < Row && r < _rows.Count; r++)
                    {
                        _rows[r].Clear();
                    }

                    break;
                default:
                    foreach (var row in _rows)
                    {
                        row.Clear();
                    }

                    break;
            }
        }

        public void EraseChars(int n)
        {
            var line = Line(Row);
            for (var c = Col; c < Col + n && c < line.Count; c++)
            {
                line[c] = null;
            }
        }

        public void DeleteChars(int n)
        {
            var line = Line(Row);
            if (Col < line.Count)
            {
                line.RemoveRange(Col, Math.Min(n, line.Count - Col));
            }
        }

        public void InsertChars(int n)
        {
            var line = Line(Row);
            if (Col < line.Count)
            {
                line.InsertRange(Col, Enumerable.Repeat<string?>(null, n));
            }
        }

        public void InsertLines(int n)
        {
            Line(Row);
            for (var k = 0; k < n; k++)
            {
                _rows.Insert(Row, []);
            }

            if (height != int.MaxValue && _rows.Count > height)
            {
                _rows.RemoveRange(height, _rows.Count - height);
            }
        }

        public void DeleteLines(int n)
        {
            Line(Row);
            _rows.RemoveRange(Row, Math.Min(n, _rows.Count - Row));
        }

        public void Save() => (_savedRow, _savedCol) = (Row, Col);

        public void Restore() => (Row, Col) = (_savedRow, _savedCol);

        private static string Render(List<string?> line)
        {
            var sb = new StringBuilder(line.Count);
            foreach (var cell in line)
            {
                sb.Append(cell ?? " ");
            }

            return sb.ToString().TrimEnd();
        }

        /// <summary>History that scrolled away, then the screen, without the blank rows around them.</summary>
        public string Text()
        {
            var lines = _scrollback.Concat(_rows.Select(Render)).ToList();
            var first = lines.FindIndex(l => l.Length > 0);
            if (first < 0)
            {
                return "";
            }

            var last = lines.FindLastIndex(l => l.Length > 0);
            return string.Join('\n', lines.Skip(first).Take(last - first + 1));
        }
    }
}
