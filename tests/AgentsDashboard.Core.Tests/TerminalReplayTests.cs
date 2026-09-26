using AgentsDashboard.Core.Presentation;

namespace AgentsDashboard.Core.Tests;

public class TerminalReplayTests
{
    private const string E = "\u001b";

    [Fact]
    public void A_cursor_step_right_is_a_space()
    {
        // Claude's interface moves the cursor rather than printing spaces; dropping
        // the escape without replaying it is what ran every word together.
        var raw = $"the{E}[1Cbackground{E}[1Cthis{E}[1Ctime";

        Assert.Equal("the background this time", TerminalReplay.Render(raw));
    }

    [Fact]
    public void A_spinner_redrawn_in_place_leaves_only_its_last_frame()
    {
        var raw =
            $"{E}[H\r\n{E}[3B{E}[38;2;215;119;87m✶{E}[26G{E}[38;2;153;153;153m9{E}[39m{E}[50;1H" +
            $"{E}[H\r\n{E}[3B{E}[38;2;215;119;87m✳{E}[25G{E}[38;2;153;153;153m31{E}[39m{E}[50;1H" +
            $"{E}[H\r\n{E}[3B✢{E}[3GThinking…{E}[K{E}[50;1H";

        var text = TerminalReplay.Render(raw);

        Assert.Equal("✢ Thinking…", text);
    }

    [Fact]
    public void Leaves_no_control_characters_behind()
    {
        var raw = $"{E}]0;title\u0007{E}[?25l{E}[1;31mred{E}[0m\r\n{E}[2Kplain{E}(B\u0007";

        var text = TerminalReplay.Render(raw);

        Assert.Equal("red\nplain", text);
        Assert.DoesNotContain(text, c => c < ' ' && c != '\n');
    }

    [Fact]
    public void Places_text_at_absolute_positions()
    {
        var raw = $"{E}[3;5Hthird{E}[1;1Hfirst";

        Assert.Equal("first\n\n    third", TerminalReplay.Render(raw));
    }

    [Fact]
    public void Erases_to_the_end_of_the_line()
    {
        var raw = $"long old text\r{E}[5Cnew{E}[K";

        Assert.Equal("long new", TerminalReplay.Render(raw));
    }

    [Fact]
    public void Keeps_the_lines_that_scroll_off_the_top()
    {
        // The stream positions on row 3 at most, so the screen is three rows tall
        // and the first two lines scroll into history rather than being lost.
        var raw = $"{E}[3;1H\r{E}[1;1Hone\r\ntwo\r\nthree\r\nfour\r\nfive";

        Assert.Equal("one\ntwo\nthree\nfour\nfive", TerminalReplay.Render(raw));
    }

    [Fact]
    public void Plain_output_passes_through_unchanged()
    {
        var raw = "Error: no session with id abc\nTry claude agents";

        Assert.Equal(raw, TerminalReplay.Render(raw));
    }

    [Fact]
    public void Plain_line_feeds_without_carriage_returns_start_a_new_line()
    {
        var raw = $"{E}[1mone{E}[0m\ntwo\nthree";

        Assert.Equal("one\ntwo\nthree", TerminalReplay.Render(raw));
    }

    [Fact]
    public void A_variation_selector_stays_with_its_character()
    {
        var raw = $"⏺️{E}[1Cdone";

        Assert.Equal("⏺️ done", TerminalReplay.Render(raw));
    }

    [Fact]
    public void A_truncated_sequence_at_the_end_is_ignored()
    {
        Assert.Equal("text", TerminalReplay.Render($"text{E}[38;2;1"));
        Assert.Equal("text", TerminalReplay.Render($"text{E}"));
    }
}
