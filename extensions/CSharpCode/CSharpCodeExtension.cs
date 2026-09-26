using Microsoft.Extensions.DependencyInjection;

namespace AgentsDashboard.Extensions.CSharpCode;

/// <summary>Registers Roslyn as the editor's code intelligence for C#.</summary>
/// <remarks>
/// Singletons, because the point of a loaded solution is that it stays warm
/// while the user moves between agents: a load costs seconds and hundreds of
/// megabytes. They live as long as the extension does.
/// </remarks>
public sealed class CSharpCodeExtension : IDashboardExtension
{
    public void Configure(IExtensionBuilder builder)
    {
        builder.Services.AddSingleton<SolutionLoader>();
        builder.AddCodeIntelligence<RoslynCodeIntelligence>();
    }
}
