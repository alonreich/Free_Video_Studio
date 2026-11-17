namespace FreeVideoStudio.Core.Media;

public sealed class AudioPlayerClock : IDisposable
{
    public const double MaxExtrapolationSec = 0.25;

    private readonly object _gate = new();
    private readonly System.Diagnostics.Stopwatch _wall = System.Diagnostics.Stopwatch.StartNew();
    private MpvIpcClient? _client;
    private double _lastPosSec = double.NaN;
    private double _lastAtSec;

    public void Attach(MpvIpcClient? client)
    {
        if (ReferenceEquals(client, _client)) return;
        if (_client != null) _client.TimePosChanged -= OnTimePos;
        _client = client;
        lock (_gate) _lastPosSec = double.NaN;
        if (client != null) client.TimePosChanged += OnTimePos;
    }

    private void OnTimePos(double positionSec)
    {
        lock (_gate)
        {
            _lastPosSec = positionSec;
            _lastAtSec = _wall.Elapsed.TotalSeconds;
        }
    }

    public void Reset() { lock (_gate) _lastPosSec = double.NaN; }

    public double PositionSec(bool running)
    {
        var client = _client;
        lock (_gate)
        {
            if (double.IsNaN(_lastPosSec)) return client?.CurrentTime ?? double.NaN;
            if (!running) return _lastPosSec;
            double since = Math.Clamp(_wall.Elapsed.TotalSeconds - _lastAtSec, 0, MaxExtrapolationSec);
            return _lastPosSec + since;
        }
    }

    public void Dispose() => Attach(null);
}
