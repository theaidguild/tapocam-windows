namespace TapoCam.Core;

public sealed record CameraHealthUpdate(CameraProbeResult Result, bool Changed);

public sealed class CameraHealthMonitor(ICameraHealthProbe probe, TimeProvider clock)
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private long? _lastProbeTimestamp;
    private bool _inProgress;
    private bool? _reachable;
    private int _generation;

    public bool? Reachable
    {
        get { lock (_gate) return _reachable; }
    }

    public async Task<CameraHealthUpdate?> CheckAsync(
        string host, int port, CancellationToken cancellationToken = default)
    {
        int generation;
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            if (_inProgress ||
                (_lastProbeTimestamp is { } last && clock.GetElapsedTime(last, now) < Interval))
                return null;

            _lastProbeTimestamp = now;
            _inProgress = true;
            generation = _generation;
        }

        try
        {
            var result = await probe.ProbeAsync(host, port, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation)
                    return null;

                var changed = _reachable is { } previous && previous != result.Reachable;
                _reachable = result.Reachable;
                return new(result, changed);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                    _inProgress = false;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _lastProbeTimestamp = null;
            _reachable = null;
            _inProgress = false;
        }
    }
}
