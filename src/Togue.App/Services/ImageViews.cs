using System.Collections.Concurrent;
using Togue.Core.Presentation;

namespace Togue.App.Services;

/// <summary>
/// Hands the editor a URL for an image on disk, and serves it.
/// </summary>
/// <remarks>
/// <para>
/// A URL rather than a data URI in the page: a screenshot is megabytes, and the
/// layout re-renders every second. The browser fetches it once and caches it by
/// the file's write time, which is in the URL, so a file written again shows
/// again.
/// </para>
/// <para>
/// The URL names a random token, never a path. The route only serves files a
/// component already asked to show, and only images, so it is no way to read
/// anything else off the disk, from this page or any other. An SVG is served
/// sandboxed with no script, since opening its URL directly would otherwise run
/// it on the app's own origin.
/// </para>
/// </remarks>
public sealed class ImageViews
{
    private readonly ConcurrentDictionary<string, string> _byPath = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byToken = new(StringComparer.Ordinal);

    /// <summary>The URL an <c>img</c> shows <paramref name="absolutePath"/> from, or null when it is not an image.</summary>
    public string? UrlFor(string absolutePath)
    {
        if (!ImageTypes.IsImage(absolutePath))
        {
            return null;
        }

        var token = _byPath.GetOrAdd(absolutePath, path =>
        {
            var fresh = Guid.NewGuid().ToString("n");
            _byToken[fresh] = path;
            return fresh;
        });

        var version = File.Exists(absolutePath) ? File.GetLastWriteTimeUtc(absolutePath).Ticks : 0;
        return $"/_image/{token}/{Uri.EscapeDataString(Path.GetFileName(absolutePath))}?v={version}";
    }

    internal static void Map(WebApplication app) =>
        app.MapGet("/_image/{token}/{name}", (string token, HttpContext http, ImageViews views) =>
        {
            if (!views._byToken.TryGetValue(token, out var path)
                || ImageTypes.ContentType(path) is not { } type
                || !File.Exists(path))
            {
                return Results.NotFound();
            }

            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; sandbox";
            return Results.File(path, type);
        });
}
