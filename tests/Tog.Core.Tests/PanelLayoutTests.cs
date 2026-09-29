using Tog.Core.Presentation;
using Tog.Core.Repos;
using Tog.Core.Tests.Support;

namespace Tog.Core.Tests;

public class PanelLayoutTests
{
    private static readonly PanelView[] Views =
    [
        new("files", PanelSide.Left),
        new("scm", PanelSide.Right),
        new("chat", PanelSide.Bottom),
        new("tests", PanelSide.Right),
        new("outline", PanelSide.Left),
    ];

    private static string[][] Strips(PanelLayout layout, PanelSide side, IReadOnlyList<PanelView>? available = null) =>
        layout.Arrange(side, available ?? Views).Select(s => s.Views.ToArray()).ToArray();

    [Fact]
    public void Unplaced_views_go_where_they_ask_in_one_section()
    {
        var layout = new PanelLayout();

        Assert.Equal([["files", "outline"]], Strips(layout, PanelSide.Left));
        Assert.Equal([["scm", "tests"]], Strips(layout, PanelSide.Right));
        Assert.Equal([["chat"]], Strips(layout, PanelSide.Bottom));
        Assert.False(layout.Arranged);
    }

    [Fact]
    public void Moving_a_view_to_another_panel_takes_it_out_of_its_own()
    {
        var layout = new PanelLayout();

        layout.Move("tests", PanelSide.Left, 0, before: null, Views);

        Assert.Equal([["files", "outline", "tests"]], Strips(layout, PanelSide.Left));
        Assert.Equal([["scm"]], Strips(layout, PanelSide.Right));
        Assert.Equal("tests", layout.Arrange(PanelSide.Left, Views)[0].Active);
    }

    [Fact]
    public void Dropping_on_a_tab_puts_the_view_before_it()
    {
        var layout = new PanelLayout();

        layout.Move("outline", PanelSide.Left, 0, before: "files", Views);

        Assert.Equal([["outline", "files"]], Strips(layout, PanelSide.Left));
    }

    [Fact]
    public void Splitting_makes_a_section_above_or_below_sharing_the_height()
    {
        var layout = new PanelLayout();

        layout.Split("tests", PanelSide.Left, 0, below: true, Views);
        layout.Split("scm", PanelSide.Left, 1, below: false, Views);
        layout.Split("chat", PanelSide.Left, 1, below: true, Views);

        Assert.Equal([["files", "outline"], ["scm"], ["chat"], ["tests"]], Strips(layout, PanelSide.Left));
        Assert.Empty(Strips(layout, PanelSide.Bottom));
        Assert.Equal([0.5, 0.125, 0.125, 0.25], layout.Left.Sections.Select(s => s.Weight));
    }

    [Fact]
    public void Only_the_first_section_is_tabbed()
    {
        var layout = new PanelLayout();
        layout.Split("scm", PanelSide.Right, 0, below: true, Views);

        layout.Move("chat", PanelSide.Right, 1, before: null, Views);
        layout.Move("files", PanelSide.Right, 1, before: "scm", Views);

        Assert.Equal([["tests"], ["files"], ["scm"], ["chat"]], Strips(layout, PanelSide.Right));
        Assert.All(layout.Right.Sections.Skip(1), s => Assert.Equal(s.Views[0], s.Active));
        Assert.Equal([0.5, 0.125, 0.125, 0.25], layout.Right.Sections.Select(s => s.Weight));
    }

    [Fact]
    public void Splitting_above_the_first_section_leaves_one_strip()
    {
        var layout = new PanelLayout();

        layout.Split("chat", PanelSide.Left, 0, below: false, Views);

        Assert.Equal([["chat"], ["files"], ["outline"]], Strips(layout, PanelSide.Left));
    }

    [Fact]
    public void The_menu_moves_a_view_into_the_other_panels_tabs()
    {
        var layout = new PanelLayout();
        layout.Split("tests", PanelSide.Right, 0, below: true, Views);

        layout.MoveToPanel("chat", PanelSide.Right, Views);

        Assert.Equal([["scm", "chat"], ["tests"]], Strips(layout, PanelSide.Right));
    }

    [Fact]
    public void A_section_left_empty_goes_and_its_height_passes_on()
    {
        var layout = new PanelLayout();
        layout.Split("outline", PanelSide.Left, 0, below: true, Views);

        layout.Move("outline", PanelSide.Right, 0, before: null, Views);

        Assert.Single(layout.Left.Sections);
        Assert.Equal(1, layout.Left.Sections[0].Weight);
        Assert.Equal([["scm", "tests", "outline"]], Strips(layout, PanelSide.Right));
    }

    [Fact]
    public void A_panel_keeps_an_empty_section_to_drop_into()
    {
        var layout = new PanelLayout();

        layout.MoveToPanel("chat", PanelSide.Right, Views);

        Assert.Single(layout.Bottom.Sections);
        Assert.Empty(Strips(layout, PanelSide.Bottom));
        layout.Move("chat", PanelSide.Bottom, 0, before: null, Views);
        Assert.Equal([["chat"]], Strips(layout, PanelSide.Bottom));
    }

    [Fact]
    public void Splitting_off_a_sections_only_view_changes_nothing()
    {
        var layout = new PanelLayout();

        layout.Split("chat", PanelSide.Bottom, 0, below: true, Views);

        Assert.Equal([["chat"]], Strips(layout, PanelSide.Bottom));
        Assert.Single(layout.Bottom.Sections);
    }

    [Fact]
    public void A_placed_view_that_is_not_here_keeps_its_place()
    {
        var layout = new PanelLayout();
        layout.Split("tests", PanelSide.Left, 0, below: true, Views);
        var withoutTests = Views.Where(v => v.Key != "tests").ToList();

        Assert.Equal([["files", "outline"]], Strips(layout, PanelSide.Left, withoutTests));
        Assert.Equal([["files", "outline"], ["tests"]], Strips(layout, PanelSide.Left));
    }

    [Fact]
    public void A_view_that_turns_up_later_joins_its_default_panel()
    {
        var layout = new PanelLayout();
        layout.Move("scm", PanelSide.Left, 0, before: null, Views);

        var more = Views.Append(new PanelView("coverage", PanelSide.Right)).ToList();

        Assert.Equal([["tests", "coverage"]], Strips(layout, PanelSide.Right, more));
    }

    [Fact]
    public void Showing_a_view_unfolds_its_section_and_opens_its_panel()
    {
        var layout = new PanelLayout();
        layout.Split("tests", PanelSide.Left, 0, below: true, Views);
        layout.ToggleCollapsed(PanelSide.Left, 1);
        layout.Left.Open = false;

        Assert.True(layout.Show("tests", PanelSide.Right));

        Assert.True(layout.Left.Open);
        Assert.False(layout.Left.Sections[1].Collapsed);
        Assert.False(layout.Show("tests", PanelSide.Right));
    }

    [Fact]
    public void Reset_puts_every_view_back_and_keeps_which_panels_show()
    {
        var layout = new PanelLayout();
        layout.Split("tests", PanelSide.Left, 0, below: true, Views);
        layout.Right.Open = false;

        layout.ResetViews();

        Assert.False(layout.Arranged);
        Assert.False(layout.Right.Open);
        Assert.Equal([["scm", "tests"]], Strips(layout, PanelSide.Right));
    }

    [Fact]
    public void Normalize_mends_a_hand_edited_file()
    {
        var layout = new PanelLayout
        {
            Left = new DockedPanel { Sections = [] },
            Right = new DockedPanel { Sections = [new PanelSection { Views = ["scm", "scm", "files"], Weight = -3 }] },
            Bottom = null!,
            Sizes = null!,
        }.Normalize();

        Assert.Single(layout.Left.Sections);
        Assert.Equal(["scm", "files"], layout.Right.Sections[0].Views);
        Assert.Equal(1, layout.Right.Sections[0].Weight);
        Assert.Single(layout.Bottom.Sections);
        Assert.NotNull(layout.Sizes);
    }

    [Fact]
    public void Normalize_gives_each_view_below_the_first_section_its_own()
    {
        var layout = new PanelLayout
        {
            Right = new DockedPanel
            {
                Sections =
                [
                    new PanelSection { Views = ["scm"] },
                    new PanelSection { Views = ["tests", "chat"], Active = "chat", Weight = 4 },
                ],
            },
        }.Normalize();

        Assert.Equal([["scm"], ["tests"], ["chat"]], Strips(layout, PanelSide.Right));
        Assert.Equal([1, 2, 2], layout.Right.Sections.Select(s => s.Weight));
    }

    [Fact]
    public void The_store_round_trips_the_layout()
    {
        using var temp = new TempDir();
        var store = new PanelLayoutStore(new AppPaths(temp.Path));
        var layout = new PanelLayout();
        layout.Split("tests", PanelSide.Left, 0, below: true, Views);
        layout.ToggleCollapsed(PanelSide.Left, 1);
        layout.Bottom.Open = false;
        layout.Sizes["wb-left"] = "312";

        store.Save(layout);
        var read = new PanelLayoutStore(new AppPaths(temp.Path)).Load();

        Assert.Equal([["files", "outline"], ["tests"]], Strips(read, PanelSide.Left));
        Assert.True(read.Left.Sections[1].Collapsed);
        Assert.False(read.Bottom.Open);
        Assert.Equal("312", read.Sizes["wb-left"]);
    }

    [Fact]
    public void A_torn_layout_file_loads_as_the_default()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.LayoutFile, "{\"Left\":");

        var layout = new PanelLayoutStore(paths).Load();

        Assert.False(layout.Arranged);
    }

    [Fact]
    public void The_window_bounds_round_trip()
    {
        using var temp = new TempDir();
        var bounds = new WindowBounds(120, 80, 1600, 1000, Maximized: true);

        new WindowBoundsStore(new AppPaths(temp.Path)).Save(bounds);

        Assert.Equal(bounds, new WindowBoundsStore(new AppPaths(temp.Path)).Load());
    }

    [Fact]
    public void Window_bounds_with_no_size_load_as_nothing()
    {
        using var temp = new TempDir();
        var paths = new AppPaths(temp.Path);
        paths.EnsureCreated();
        File.WriteAllText(paths.WindowFile, "{\"X\":0,\"Y\":0,\"Width\":0,\"Height\":0,\"Maximized\":false}");

        Assert.Null(new WindowBoundsStore(paths).Load());
    }
}
