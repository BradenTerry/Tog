using AgentsDashboard.Extensions;

namespace MyExtension;

public sealed class MyExtensionExtension : IDashboardExtension
{
    public void Configure(IExtensionBuilder builder)
    {
        builder.AddView<MyExtensionView>(
            "main",
            "MyExtension",
            appliesTo: agent => agent.Worktree is not null);
    }
}
