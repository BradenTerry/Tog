using Togue.Extensions;

namespace MyExtension;

public sealed class MyExtensionExtension : ITogueExtension
{
    public void Configure(IExtensionBuilder builder)
    {
        builder.AddView<MyExtensionView>(
            "main",
            "MyExtension",
            appliesTo: agent => agent.Worktree is not null);
    }
}
