using System.Reflection;

namespace Tog.Core;

/// <summary>
/// The version this build carries, as <c>Directory.Build.targets</c> stamped it
/// from git: <c>0.2.0</c> on the tag, <c>0.2.0+3.1a2b3c4</c> three commits
/// past it, <c>.dirty</c> on the end when the tree had uncommitted changes.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            is { Length: > 0 } version ? version : "0.0.0";
}
