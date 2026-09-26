using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace AgentsDashboard.Core.Presentation;

/// <summary>
/// Turns a Markdown file from a worktree into HTML for the Files tab's preview.
/// </summary>
/// <remarks>
/// <para>
/// Raw HTML in the file is not passed through. The preview is drawn inside the
/// app, and a file in a repository is not trusted content: an agent writes these
/// files, and a script tag in one would run with the whole dashboard's reach.
/// Markdown gives up nothing a README needs by losing it.
/// </para>
/// <para>
/// Links are sorted three ways. One to a web page opens outside the app. One to
/// another file in the repository is marked with <c>data-file</c>, the path it
/// resolves to from the file being previewed, and its target is taken away, so a
/// click opens that file in the Files tab instead of the router treating the path
/// as a page of this app. An anchor within the page is left alone.
/// </para>
/// <para>
/// Fenced <c>mermaid</c> blocks come out as <c>pre.mermaid</c>, which the page
/// draws; other fenced blocks keep their <c>language-</c> class for colouring.
/// </para>
/// </remarks>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    /// <param name="markdown">The text to render.</param>
    /// <param name="filePath">The file's worktree-relative path, which relative links are resolved from.</param>
    public static string Render(string markdown, string? filePath = null)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var directory = filePath is null ? "" : Parent(filePath);

        foreach (var link in document.Descendants<LinkInline>())
        {
            if (link.IsImage || string.IsNullOrEmpty(link.Url))
            {
                continue;
            }

            var url = link.Url;
            var attributes = link.GetAttributes();

            if (IsExternal(url))
            {
                attributes.AddPropertyIfNotExist("target", "_blank");
                attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
                continue;
            }

            if (url.StartsWith('#'))
            {
                continue;
            }

            var target = Resolve(directory, url);
            if (target is null)
            {
                continue;
            }

            attributes.AddProperty("data-file", target);
            attributes.AddProperty("title", target);
            link.Url = "#";
        }

        return document.ToHtml(Pipeline);
    }

    private static bool IsExternal(string url) =>
        url.Contains("://", StringComparison.Ordinal)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// A relative link as a worktree-relative path. The anchor and query are
    /// dropped: the Files tab opens files, not places in them. A link that climbs
    /// out of the worktree resolves to nothing.
    /// </summary>
    public static string? Resolve(string directory, string url)
    {
        var path = url.Split('#', '?')[0];
        if (path.Length == 0)
        {
            return null;
        }

        path = Uri.UnescapeDataString(path);
        var parts = new List<string>();
        var start = path.StartsWith('/') ? path.TrimStart('/') : directory.Length == 0 ? path : directory + "/" + path;

        foreach (var segment in start.Split('/'))
        {
            if (segment is "" or ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (parts.Count == 0)
                {
                    return null;
                }

                parts.RemoveAt(parts.Count - 1);
                continue;
            }

            parts.Add(segment);
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static string Parent(string path)
    {
        var at = path.Replace('\\', '/').LastIndexOf('/');
        return at < 0 ? "" : path[..at];
    }
}
