using System.Text.Json.Serialization;

namespace AgentsDashboard.Core.Presentation;

/// <summary>The three panels around the editor.</summary>
public enum PanelSide
{
    Left,
    Right,
    Bottom,
}

/// <summary>
/// One stretch of a panel: a strip of views, the one in front, and whether it is
/// folded down to its strip. A panel is a column of these, split top to bottom.
/// </summary>
public sealed class PanelSection
{
    /// <summary>The views placed here by hand, in strip order. A view placed nowhere joins its default panel's first section.</summary>
    public List<string> Views { get; set; } = [];

    public string? Active { get; set; }

    public bool Collapsed { get; set; }

    /// <summary>The section's share of the panel's height, against its neighbours'. Any scale; app.js writes pixels.</summary>
    public double Weight { get; set; } = 1;
}

/// <summary>One panel: whether it is showing, and its sections top to bottom.</summary>
public sealed class DockedPanel
{
    public bool Open { get; set; } = true;

    public List<PanelSection> Sections { get; set; } = [new()];
}

/// <summary>A view the panels can show now, and the panel it asks for.</summary>
public readonly record struct PanelView(string Key, PanelSide Default);

/// <summary>A section as drawn: its place in the panel, and the views it shows now in strip order.</summary>
public sealed record ArrangedSection(int Index, PanelSection Section, IReadOnlyList<string> Views)
{
    /// <summary>The view in front: the one last picked, or the first when that one is not here now.</summary>
    public string Active => Section.Active is { } active && Views.Contains(active) ? active : Views[0];
}

/// <summary>
/// Where every view sits in the panels, and the sizes of the borders between
/// them: how you like the window, so it is kept per machine.
/// </summary>
/// <remarks>
/// A view is placed by hand or not at all. One never placed goes where it asks
/// to (an extension's <c>DefaultLocation</c>, or the app's own view's panel),
/// at the end of that panel's first section, so an extension installed later
/// still turns up without anyone arranging it. The first move writes every view
/// down where it is, so nothing already on screen shifts when the rest are
/// placed. A view placed by hand that is not here now (an extension removed, or
/// Chat with no agent) keeps its place and comes back to it.
/// </remarks>
public sealed class PanelLayout
{
    public DockedPanel Left { get; set; } = new();

    public DockedPanel Right { get; set; } = new();

    public DockedPanel Bottom { get; set; } = new();

    /// <summary>The panel borders app.js drags, by name (<c>wb-left</c>, <c>wb-split</c>), as it stores them.</summary>
    public Dictionary<string, string> Sizes { get; set; } = new(StringComparer.Ordinal);

    public DockedPanel Panel(PanelSide side) => side switch
    {
        PanelSide.Left => Left,
        PanelSide.Right => Right,
        _ => Bottom,
    };

    /// <summary>
    /// Mends a layout read from disk: every panel has a section, and a view is in
    /// one place only, the first it was found in.
    /// </summary>
    public PanelLayout Normalize()
    {
        Sizes ??= new(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var side in Enum.GetValues<PanelSide>())
        {
            var panel = Panel(side) ?? new DockedPanel();
            switch (side)
            {
                case PanelSide.Left:
                    Left = panel;
                    break;
                case PanelSide.Right:
                    Right = panel;
                    break;
                default:
                    Bottom = panel;
                    break;
            }

            panel.Sections ??= [];
            panel.Sections.RemoveAll(s => s is null);
            if (panel.Sections.Count == 0)
            {
                panel.Sections.Add(new PanelSection());
            }

            foreach (var section in panel.Sections)
            {
                section.Views = (section.Views ?? []).Where(v => !string.IsNullOrEmpty(v) && seen.Add(v)).ToList();
                if (!double.IsFinite(section.Weight) || section.Weight <= 0)
                {
                    section.Weight = 1;
                }
            }
        }

        return this;
    }

    /// <summary>Where a view is: the section it was placed in, or its default panel's first.</summary>
    public (PanelSide Side, int Section) Locate(string key, PanelSide fallback)
    {
        foreach (var side in Enum.GetValues<PanelSide>())
        {
            var sections = Panel(side).Sections;
            for (var i = 0; i < sections.Count; i++)
            {
                if (sections[i].Views.Contains(key))
                {
                    return (side, i);
                }
            }
        }

        return (fallback, 0);
    }

    /// <summary>
    /// The sections of one panel as they are drawn now, given the views there are,
    /// in the order they would come with nothing placed. Sections with none of
    /// them here are left out.
    /// </summary>
    public IReadOnlyList<ArrangedSection> Arrange(PanelSide side, IReadOnlyList<PanelView> available)
    {
        var here = available.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        var sections = Panel(side).Sections;
        var result = new List<ArrangedSection>();
        for (var i = 0; i < sections.Count; i++)
        {
            var views = sections[i].Views.Where(here.Contains).ToList();
            if (i == 0)
            {
                views.AddRange(available
                    .Where(v => v.Default == side && Locate(v.Key, v.Default) == (side, 0) && !sections[0].Views.Contains(v.Key))
                    .Select(v => v.Key));
            }

            if (views.Count > 0)
            {
                result.Add(new ArrangedSection(i, sections[i], views));
            }
        }

        return result;
    }

    /// <summary>
    /// Brings a view to the front of its section, unfolds the section and opens
    /// the panel. Returns false when all of that already held.
    /// </summary>
    public bool Show(string key, PanelSide fallback)
    {
        var (side, index) = Locate(key, fallback);
        var panel = Panel(side);
        var section = panel.Sections[index];
        var already = panel.Open && !section.Collapsed && (section.Active == key || section.Active is null && section.Views.FirstOrDefault() == key);
        panel.Open = true;
        section.Collapsed = false;
        section.Active = key;
        return !already;
    }

    /// <summary>Picks the view in front of a section without touching anything else.</summary>
    public void Activate(PanelSide side, int section, string key)
    {
        if (Section(side, section) is { } target)
        {
            target.Active = key;
        }
    }

    /// <summary>
    /// Moves a view into a section's strip, before another view or at the end,
    /// and brings it to the front there. The section it left goes if that left
    /// it empty and the panel has others.
    /// </summary>
    /// <param name="known">Every view there is now, so the move can write down where the unplaced ones are before anything shifts.</param>
    public void Move(string key, PanelSide side, int section, string? before, IReadOnlyList<PanelView> known)
    {
        if (Section(side, section) is not { } target || before == key)
        {
            return;
        }

        Place(known);
        var from = Take(key);
        var at = before is null ? -1 : target.Views.IndexOf(before);
        if (at < 0)
        {
            target.Views.Add(key);
        }
        else
        {
            target.Views.Insert(at, key);
        }

        target.Active = key;
        target.Collapsed = false;
        Panel(side).Open = true;
        Prune(from, keep: target);
    }

    /// <summary>
    /// Moves a view into a section of its own, just above or below another.
    /// The two share the height the one had.
    /// </summary>
    public void Split(string key, PanelSide side, int section, bool below, IReadOnlyList<PanelView> known)
    {
        if (Section(side, section) is not { } target)
        {
            return;
        }

        Place(known);
        var from = Take(key);
        if (from == target && target.Views.Count == 0)
        {
            // Split off the only view of its own section: nothing moves.
            target.Views.Add(key);
            target.Active = key;
            return;
        }

        target.Weight /= 2;
        var created = new PanelSection { Views = [key], Active = key, Weight = target.Weight };
        var sections = Panel(side).Sections;
        sections.Insert(sections.IndexOf(target) + (below ? 1 : 0), created);
        Panel(side).Open = true;
        Prune(from, keep: created);
    }

    /// <summary>Moves a view to the end of another panel's last section, for the menu, which has no place to point at.</summary>
    public void MoveToPanel(string key, PanelSide side, IReadOnlyList<PanelView> known)
    {
        var sections = Panel(side).Sections;
        Move(key, side, sections.Count - 1, null, known);
    }

    public void ToggleCollapsed(PanelSide side, int section)
    {
        if (Section(side, section) is { } target)
        {
            target.Collapsed = !target.Collapsed;
        }
    }

    public void SetWeight(PanelSide side, int section, double weight)
    {
        if (Section(side, section) is { } target && double.IsFinite(weight) && weight > 0)
        {
            target.Weight = weight;
        }
    }

    /// <summary>Every view back where it asks to be, one section per panel. Which panels are showing stays.</summary>
    public void ResetViews()
    {
        foreach (var side in Enum.GetValues<PanelSide>())
        {
            Panel(side).Sections = [new PanelSection()];
        }
    }

    /// <summary>Whether any view has been placed by hand.</summary>
    [JsonIgnore]
    public bool Arranged => Enum.GetValues<PanelSide>().Any(s => Panel(s).Sections.Count > 1 || Panel(s).Sections.Any(x => x.Views.Count > 0));

    private PanelSection? Section(PanelSide side, int index)
    {
        var sections = Panel(side).Sections;
        return index >= 0 && index < sections.Count ? sections[index] : null;
    }

    /// <summary>Writes every unplaced view down where it is drawn now.</summary>
    private void Place(IReadOnlyList<PanelView> known)
    {
        foreach (var view in known)
        {
            var (side, section) = Locate(view.Key, view.Default);
            if (section == 0 && !Panel(side).Sections[0].Views.Contains(view.Key))
            {
                Panel(side).Sections[0].Views.Add(view.Key);
            }
        }
    }

    /// <summary>Takes a view out of wherever it is, and says where that was.</summary>
    private PanelSection? Take(string key)
    {
        foreach (var side in Enum.GetValues<PanelSide>())
        {
            foreach (var section in Panel(side).Sections)
            {
                if (section.Views.Remove(key))
                {
                    if (section.Active == key)
                    {
                        section.Active = null;
                    }

                    return section;
                }
            }
        }

        return null;
    }

    /// <summary>Drops a section left empty, unless it is its panel's last or the one just filled.</summary>
    private void Prune(PanelSection? section, PanelSection keep)
    {
        if (section is null || section == keep || section.Views.Count > 0)
        {
            return;
        }

        foreach (var side in Enum.GetValues<PanelSide>())
        {
            var sections = Panel(side).Sections;
            var at = sections.IndexOf(section);
            if (at >= 0 && sections.Count > 1)
            {
                sections.RemoveAt(at);

                // Its height goes to the neighbour that takes its place, so the rest keep theirs.
                var heir = sections[Math.Min(at, sections.Count - 1)];
                heir.Weight += section.Weight;
            }
        }
    }
}
