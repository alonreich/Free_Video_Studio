using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using System;

namespace FreeVideoStudio.App.Controls;

public sealed class KineticScrubController : IDisposable
{
    private const double PhysicsHz = 60.0;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1.0 / PhysicsHz);
    private const double FrictionPerTick = 0.92;
    private const double MinVelocityMs = 2.0;
    private const double VelocityThreshold = 30.0;

    private readonly DispatcherTimer _timer;
    private double _velocityMsPerTick;
    private double _currentMs;
    private double _minMs;
    private double _maxMs;

    private bool _isFlinging;
    private bool _disposed;

    public event Action<double>? SeekRequested;

    public event Action? FlingSettled;

    public KineticScrubController()
    {
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += OnTick;
    }

    public void Release(double currentMs, double minMs, double maxMs, bool isPlaying)
    {
        _currentMs = currentMs;
        _minMs = minMs;
        _maxMs = maxMs;

        if (isPlaying)
        {
            _velocityMsPerTick = 0;
            return;
        }

        if (Math.Abs(_velocityMsPerTick) < VelocityThreshold)
        {
            _velocityMsPerTick = 0;
            return;
        }

        _isFlinging = true;
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _isFlinging = false;
        _velocityMsPerTick = 0;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isFlinging) return;

        _currentMs += _velocityMsPerTick;

        if (_currentMs <= _minMs)
        {
            _currentMs = _minMs;
            _velocityMsPerTick = 0;
        }
        else if (_currentMs >= _maxMs)
        {
            _currentMs = _maxMs;
            _velocityMsPerTick = 0;
        }

        SeekRequested?.Invoke(_currentMs);

        _velocityMsPerTick *= FrictionPerTick;

        if (Math.Abs(_velocityMsPerTick) < MinVelocityMs)
        {
            _velocityMsPerTick = 0;
            _isFlinging = false;
            _timer.Stop();
            FlingSettled?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}