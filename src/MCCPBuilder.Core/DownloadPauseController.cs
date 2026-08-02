namespace MCCPBuilder.Core;

public sealed class DownloadPauseController
{
    private readonly object _gate = new();
    private TaskCompletionSource<bool>? _resumeSignal;
    private bool _isPaused;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _isPaused;
            }
        }
    }

    public bool Pause()
    {
        lock (_gate)
        {
            if (_isPaused)
            {
                return false;
            }

            _isPaused = true;
            _resumeSignal = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
    }

    public bool Resume()
    {
        TaskCompletionSource<bool>? signal;
        lock (_gate)
        {
            if (!_isPaused)
            {
                return false;
            }

            _isPaused = false;
            signal = _resumeSignal;
            _resumeSignal = null;
        }

        signal?.TrySetResult(true);
        return true;
    }

    public async Task WaitWhilePausedAsync(
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? waitTask;
            lock (_gate)
            {
                if (!_isPaused)
                {
                    return;
                }

                waitTask = _resumeSignal?.Task;
            }

            if (waitTask is null)
            {
                continue;
            }

            await waitTask.WaitAsync(cancellationToken);
        }
    }
}
