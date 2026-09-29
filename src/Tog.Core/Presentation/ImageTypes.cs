namespace Tog.Core.Presentation;

/// <summary>Which files the editor shows as a picture rather than as text, and as what.</summary>
/// <remarks>
/// Only formats every webview the app runs in can draw. The list is also the
/// limit of what the image endpoint will serve, so it stays short on purpose.
/// </remarks>
public static class ImageTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".bmp"] = "image/bmp",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
    };

    /// <summary>The content type of an image, or null when the file is not one.</summary>
    public static string? ContentType(string? path) =>
        path is { Length: > 0 } && ByExtension.TryGetValue(Path.GetExtension(path), out var type) ? type : null;

    public static bool IsImage(string? path) => ContentType(path) is not null;
}
