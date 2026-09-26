namespace AgentsDashboard.Core.Presentation;

/// <summary>A file's icon in a tree: a short glyph and the class that colours it.</summary>
/// <param name="Glyph">The text drawn as the icon, or empty for the plain page outline.</param>
/// <param name="Css">The colour class, <c>fi-</c> followed by the family.</param>
public readonly record struct FileIcon(string Glyph, string Css);

/// <summary>
/// Which icon a file gets, from its name.
/// </summary>
/// <remarks>
/// Glyphs in a language's colour, the way VS Code's default Seti theme draws them,
/// rather than a vendored icon font: an icon font is another asset to ship and
/// keep loading offline, and a two-letter glyph reads just as fast in a list.
/// Whole names are checked before extensions, since a Dockerfile or a .gitignore
/// has no extension that says what it is.
/// </remarks>
public static class FileIcons
{
    private static readonly FileIcon Plain = new("", "fi-plain");

    private static readonly Dictionary<string, FileIcon> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = new("D", "fi-docker"),
        [".gitignore"] = new("G", "fi-git"),
        [".gitattributes"] = new("G", "fi-git"),
        [".editorconfig"] = new("E", "fi-config"),
        ["LICENSE"] = new("L", "fi-doc"),
    };

    private static readonly Dictionary<string, FileIcon> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = new("C#", "fi-csharp"),
        [".csx"] = new("C#", "fi-csharp"),
        [".razor"] = new("@", "fi-razor"),
        [".cshtml"] = new("@", "fi-razor"),
        [".csproj"] = new("P", "fi-project"),
        [".sln"] = new("S", "fi-project"),
        [".slnx"] = new("S", "fi-project"),
        [".props"] = new("P", "fi-project"),
        [".targets"] = new("P", "fi-project"),
        [".fs"] = new("F#", "fi-fsharp"),
        [".js"] = new("JS", "fi-js"),
        [".mjs"] = new("JS", "fi-js"),
        [".cjs"] = new("JS", "fi-js"),
        [".jsx"] = new("JS", "fi-react"),
        [".ts"] = new("TS", "fi-ts"),
        [".tsx"] = new("TS", "fi-react"),
        [".json"] = new("{}", "fi-json"),
        [".jsonc"] = new("{}", "fi-json"),
        [".css"] = new("#", "fi-css"),
        [".scss"] = new("#", "fi-sass"),
        [".html"] = new("<>", "fi-html"),
        [".htm"] = new("<>", "fi-html"),
        [".xml"] = new("<>", "fi-xml"),
        [".svg"] = new("<>", "fi-image"),
        [".md"] = new("M", "fi-markdown"),
        [".mdx"] = new("M", "fi-markdown"),
        [".txt"] = new("", "fi-plain"),
        [".py"] = new("Py", "fi-python"),
        [".go"] = new("Go", "fi-go"),
        [".rs"] = new("Rs", "fi-rust"),
        [".java"] = new("J", "fi-java"),
        [".kt"] = new("K", "fi-kotlin"),
        [".rb"] = new("Rb", "fi-ruby"),
        [".php"] = new("P", "fi-php"),
        [".swift"] = new("S", "fi-swift"),
        [".c"] = new("C", "fi-c"),
        [".h"] = new("H", "fi-c"),
        [".cpp"] = new("C+", "fi-c"),
        [".hpp"] = new("H", "fi-c"),
        [".sh"] = new("$", "fi-shell"),
        [".bash"] = new("$", "fi-shell"),
        [".zsh"] = new("$", "fi-shell"),
        [".ps1"] = new("$", "fi-shell"),
        [".yml"] = new("Y", "fi-config"),
        [".yaml"] = new("Y", "fi-config"),
        [".toml"] = new("T", "fi-config"),
        [".ini"] = new("I", "fi-config"),
        [".sql"] = new("DB", "fi-sql"),
        [".png"] = new("Im", "fi-image"),
        [".jpg"] = new("Im", "fi-image"),
        [".jpeg"] = new("Im", "fi-image"),
        [".gif"] = new("Im", "fi-image"),
        [".ico"] = new("Im", "fi-image"),
        [".lock"] = new("L", "fi-plain"),
    };

    public static FileIcon For(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        var name = slash < 0 ? path : path[(slash + 1)..];

        if (ByName.TryGetValue(name, out var named))
        {
            return named;
        }

        var dot = name.LastIndexOf('.');
        return dot >= 0 && ByExtension.TryGetValue(name[dot..], out var icon) ? icon : Plain;
    }
}
