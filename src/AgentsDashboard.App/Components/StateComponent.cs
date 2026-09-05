using AgentsDashboard.Core.Monitoring;
using Microsoft.AspNetCore.Components;

namespace AgentsDashboard.App.Components;

/// <summary>
/// A view that re-renders when the monitor publishes a new snapshot.
/// </summary>
/// <remarks>
/// The monitor raises its event on its own thread, so the re-render is marshalled
/// with <c>InvokeAsync</c>. Every subscriber unsubscribes on dispose, which is
/// what keeps a closed circuit from holding the whole component graph alive
/// through the singleton's event.
/// </remarks>
public abstract class StateComponent : ComponentBase, IDisposable
{
    [Inject]
    protected DashboardState State { get; set; } = default!;

    protected DashboardSnapshotAccessor Snapshot => new(State);

    protected override void OnInitialized() => State.Changed += OnStateChanged;

    private void OnStateChanged() => _ = InvokeAsync(StateHasChanged);

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            State.Changed -= OnStateChanged;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>Sugar so a view can write <c>Snapshot.Repos</c> without a null dance.</summary>
public readonly struct DashboardSnapshotAccessor(DashboardState state)
{
    public Core.Model.DashboardSnapshot Value => state.Snapshot;

    public IReadOnlyList<Core.Model.RepoView> Repos => Value.Repos;

    public IReadOnlyList<Core.Model.WaitingAgent> Waiting => Value.Waiting;
}
