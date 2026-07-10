namespace PADS.MoneyFlow.Api.Services;

internal sealed class DatabaseMaintenanceGate
{
    private readonly object _sync = new();
    private bool _maintenanceActive;
    private int _activeRequests;
    private TaskCompletionSource? _requestsDrained;

    public IDisposable? TryEnterRequest()
    {
        lock (_sync)
        {
            if (_maintenanceActive)
            {
                return null;
            }

            _activeRequests++;
            return new RequestLease(this);
        }
    }

    public async Task<IAsyncDisposable?> TryBeginMaintenanceAsync(CancellationToken cancellationToken)
    {
        Task waitForRequests;
        lock (_sync)
        {
            if (_maintenanceActive)
            {
                return null;
            }

            _maintenanceActive = true;
            if (_activeRequests == 0)
            {
                waitForRequests = Task.CompletedTask;
            }
            else
            {
                _requestsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                waitForRequests = _requestsDrained.Task;
            }
        }

        try
        {
            await waitForRequests.WaitAsync(cancellationToken);
            return new MaintenanceLease(this);
        }
        catch
        {
            EndMaintenance();
            throw;
        }
    }

    private void ExitRequest()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (_activeRequests <= 0)
            {
                return;
            }

            _activeRequests--;
            if (_maintenanceActive && _activeRequests == 0)
            {
                drained = _requestsDrained;
                _requestsDrained = null;
            }
        }

        drained?.TrySetResult();
    }

    private void EndMaintenance()
    {
        lock (_sync)
        {
            _maintenanceActive = false;
            _requestsDrained = null;
        }
    }

    private sealed class RequestLease(DatabaseMaintenanceGate owner) : IDisposable
    {
        private DatabaseMaintenanceGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ExitRequest();
    }

    private sealed class MaintenanceLease(DatabaseMaintenanceGate owner) : IAsyncDisposable
    {
        private DatabaseMaintenanceGate? _owner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _owner, null)?.EndMaintenance();
            return ValueTask.CompletedTask;
        }
    }
}
