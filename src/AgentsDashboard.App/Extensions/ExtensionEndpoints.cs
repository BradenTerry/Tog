using Microsoft.AspNetCore.StaticFiles;

namespace AgentsDashboard.App.Extensions;

public static class ExtensionEndpoints
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Serves each loaded extension's <c>assets</c> folder at <c>/_ext/{id}/</c>.</summary>
    public static void MapExtensionAssets(this WebApplication app) =>
        app.MapGet("/_ext/{id}/{**path}", (string id, string path, ExtensionHost host) =>
        {
            if (host.AssetsDirectory(id) is not { } root)
            {
                return Results.NotFound();
            }

            // The path comes from the URL, so it must not climb out of the folder.
            var full = Path.GetFullPath(Path.Combine(root, path));
            if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || !File.Exists(full))
            {
                return Results.NotFound();
            }

            var type = ContentTypes.TryGetContentType(full, out var known) ? known : "application/octet-stream";
            return Results.File(full, type);
        });
}
