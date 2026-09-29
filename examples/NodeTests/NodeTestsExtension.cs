using Microsoft.Extensions.DependencyInjection;
using NodeTests.Testing;
using Tog.Extensions;

namespace NodeTests;

public sealed class NodeTestsExtension : ITogExtension
{
    public void Configure(IExtensionBuilder builder)
    {
        // One runner for every window, so a run started in one shows in all of
        // them and a reload has a single thing to stop.
        builder.Services.AddSingleton<TestRunner>();

        // A worktree view rather than an agent view: tests belong to what is on
        // disk, and should be there with no agent picked too.
        builder.AddWorktreeView<TestsView>("tests", "Tests");
    }
}
