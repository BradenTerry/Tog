using AgentsDashboard.Extensions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace AgentsDashboard.App.Extensions;

/// <summary>A view an extension added, as the app keeps it.</summary>
public sealed record ExtensionView(
    string ExtensionId,
    string ViewId,
    string Title,
    Type Component,
    ViewLocation DefaultLocation,
    int Order,
    Func<AgentContext, bool>? AppliesTo)
{
    /// <summary>The view's address as a panel tab and in the URL, namespaced by the extension.</summary>
    public string Key => ExtensionId + "." + ViewId;
}

/// <summary>Collects what an extension registers in <see cref="IDashboardExtension.Configure"/>.</summary>
internal sealed class ExtensionBuilder(ExtensionInfo info) : IExtensionBuilder
{
    public ExtensionInfo Info { get; } = info;

    public IServiceCollection Services { get; } = new ServiceCollection();

    public List<ExtensionView> Views { get; } = [];

    public List<(string ViewId, Type Provider)> Indicators { get; } = [];

    public List<(string Id, Type Worker)> Workers { get; } = [];

    public void AddView<TComponent>(
        string id,
        string title,
        ViewLocation defaultLocation = ViewLocation.RightPanel,
        int order = 100,
        Func<AgentContext, bool>? appliesTo = null)
        where TComponent : IComponent
    {
        if (Views.Any(v => v.ViewId == id))
        {
            throw new InvalidOperationException($"{Info.Id} adds the view \"{id}\" twice.");
        }

        Views.Add(new ExtensionView(Info.Id, id, title, typeof(TComponent), defaultLocation, order, appliesTo));
    }

    public void AddIndicator<TProvider>(string viewId) where TProvider : class, IAgentIndicator
    {
        Services.AddSingleton<TProvider>();
        Indicators.Add((viewId, typeof(TProvider)));
    }

    public void AddWorker<TWorker>(string id) where TWorker : class, IExtensionWorker
    {
        Services.AddSingleton<TWorker>();
        Workers.Add((id, typeof(TWorker)));
    }

    public List<Type> CodeIntelligence { get; } = [];

    public void AddCodeIntelligence<TProvider>() where TProvider : class, ICodeIntelligence
    {
        Services.AddSingleton<TProvider>();
        CodeIntelligence.Add(typeof(TProvider));
    }

    public List<Type> AgentTools { get; } = [];

    public void AddAgentTool<TTool>() where TTool : class, IAgentTool
    {
        Services.AddSingleton<TTool>();
        AgentTools.Add(typeof(TTool));
    }
}
