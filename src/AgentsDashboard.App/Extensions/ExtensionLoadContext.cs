using System.Reflection;
using System.Runtime.Loader;

namespace AgentsDashboard.App.Extensions;

/// <summary>
/// One load of one extension: its own assemblies, and the app's for everything
/// the two have to agree on.
/// </summary>
/// <remarks>
/// Anything shared has to be the app's own copy, or the extension's
/// <c>IComponent</c> would be a different type from the app's and nothing would
/// cast. Everything else the extension brings resolves from its own folder, so
/// two extensions can carry different versions of the same library.
/// Collectible so a reload can at least try to let the old copy go. Blazor keeps
/// per-type caches that hold a rendered component's type, so in practice an old
/// copy often stays until the app restarts; nothing here depends on it going.
/// </remarks>
public sealed class ExtensionLoadContext(string entryPath, string name)
    : AssemblyLoadContext(name, isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryPath);

    private static readonly string[] SharedPrefixes =
    [
        "System.",
        "Microsoft.AspNetCore.",
        "Microsoft.Extensions.",
        "Microsoft.JSInterop",
        "AgentsDashboard.Extensions",
    ];

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var simple = assemblyName.Name ?? "";
        if (simple is "System" or "mscorlib" or "netstandard"
            || SharedPrefixes.Any(p => simple.StartsWith(p, StringComparison.Ordinal)))
        {
            return null;
        }

        return _resolver.ResolveAssemblyToPath(assemblyName) is { } path
            ? LoadFromAssemblyPath(path)
            : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName) =>
        _resolver.ResolveUnmanagedDllToPath(unmanagedDllName) is { } path
            ? LoadUnmanagedDllFromPath(path)
            : IntPtr.Zero;
}
