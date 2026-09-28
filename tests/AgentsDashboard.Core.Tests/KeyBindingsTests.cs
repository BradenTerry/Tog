using AgentsDashboard.Core.Input;
using AgentsDashboard.Core.Repos;
using AgentsDashboard.Core.Tests.Support;

namespace AgentsDashboard.Core.Tests;

public class KeyBindingsTests
{
    [Theory]
    [InlineData("Ctrl+P", "ctrl+p")]
    [InlineData("shift+ctrl+p", "ctrl+shift+p")]
    [InlineData("cmd+alt+b", "alt+cmd+b")]
    [InlineData("Command+Option+B", "alt+cmd+b")]
    [InlineData("ctrl+shift+-", "ctrl+shift+-")]
    [InlineData("ctrl++", "ctrl++")]
    [InlineData("ctrl+ArrowLeft", "ctrl+left")]
    [InlineData("f1", "f1")]
    public void Normalizes_chords(string raw, string expected)
    {
        Assert.Equal(expected, KeyChord.Normalize(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ctrl")]
    [InlineData("ctrl+ctrl+p")]
    [InlineData("hyper+p")]
    [InlineData("ctrl+")]
    public void Rejects_what_is_not_a_chord(string raw)
    {
        Assert.Null(KeyChord.Normalize(raw));
    }

    [Theory]
    [InlineData("cmd+p", true, "⌘P")]
    [InlineData("ctrl+shift+p", true, "⌃⇧P")]
    [InlineData("alt+cmd+b", true, "⌥⌘B")]
    [InlineData("ctrl+shift+p", false, "Ctrl+Shift+P")]
    [InlineData("alt+left", false, "Alt+Left")]
    [InlineData("ctrl+-", false, "Ctrl+-")]
    public void Displays_chords_per_platform(string chord, bool mac, string expected)
    {
        Assert.Equal(expected, KeyChord.Display(chord, mac));
    }

    [Fact]
    public void Defaults_follow_the_platform()
    {
        Assert.Equal(["cmd+p", "ctrl+t"], KeyMap.Resolve(null, mac: true).For(Commands.QuickOpen));
        Assert.Equal(["ctrl+p", "ctrl+t"], KeyMap.Resolve(null, mac: false).For(Commands.QuickOpen));
    }

    [Fact]
    public void A_users_choice_replaces_the_defaults_whole()
    {
        var map = KeyMap.Resolve(new Dictionary<string, IReadOnlyList<string>>
        {
            [Commands.QuickOpen] = ["Ctrl+E"],
            [Commands.ToggleLeft] = [],
        }, mac: true);

        Assert.Equal(["ctrl+e"], map.For(Commands.QuickOpen));
        Assert.True(map.IsChanged(Commands.QuickOpen));
        Assert.Empty(map.For(Commands.ToggleLeft));
        Assert.False(map.IsChanged(Commands.GoToLine));
        Assert.Equal("ctrl+g", map.For(Commands.GoToLine)[0]);
    }

    [Fact]
    public void Chords_resolve_to_the_first_command_and_clashes_are_listed()
    {
        var map = KeyMap.Resolve(new Dictionary<string, IReadOnlyList<string>>
        {
            [Commands.GoToLine] = ["cmd+p"],
        }, mac: true);

        Assert.Equal(Commands.QuickOpen, map.ByChord()["cmd+p"]);
        Assert.Equal([Commands.QuickOpen, Commands.GoToLine], map.CommandsFor("cmd+p"));
    }

    [Fact]
    public void Hint_is_the_first_key_as_printed()
    {
        Assert.Equal("⌘P", KeyMap.Resolve(null, mac: true).Hint(Commands.QuickOpen));
        Assert.Null(KeyMap.Resolve(null, mac: true).Hint(Commands.NewAgent));
    }

    [Fact]
    public void Every_command_has_a_unique_id_and_valid_defaults()
    {
        Assert.Equal(Commands.All.Count, Commands.All.Select(c => c.Id).Distinct().Count());
        foreach (var chord in Commands.All.SelectMany(c => c.Mac.Concat(c.Other)))
        {
            Assert.Equal(chord, KeyChord.Normalize(chord));
        }
    }

    [Fact]
    public void Settings_round_trip_the_bindings_and_quick_open_choices()
    {
        using var temp = new TempDir();
        var store = new SettingsStore(new AppPaths(temp.Path));

        store.Save(new Settings
        {
            QuickOpenPreview = true,
            QuickOpenHistory = false,
            KeyBindings = new Dictionary<string, IReadOnlyList<string>>
            {
                [Commands.QuickOpen] = ["ctrl+e"],
                [Commands.ToggleLeft] = [],
            },
        });

        var loaded = store.Load();

        Assert.True(loaded.QuickOpenPreview);
        Assert.False(loaded.QuickOpenHistory);
        Assert.True(loaded.QuickOpenCloseOnBlur);
        Assert.Equal(["ctrl+e"], loaded.KeyBindings[Commands.QuickOpen]);
        Assert.Empty(loaded.KeyBindings[Commands.ToggleLeft]);
    }
}
