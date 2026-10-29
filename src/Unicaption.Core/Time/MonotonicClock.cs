using System.Diagnostics;

namespace Unicaption.Core.Time;

/// <summary>
/// Monotonic time source (TIME 01). On Windows <see cref="Stopwatch"/> is backed by
/// QueryPerformanceCounter, so it is unaffected by wall-clock changes (AC 12).
/// </summary>
public interface IMonotonicClock
{
    /// <summary>Monotonic timestamp in milliseconds since an arbitrary origin.</summary>
    double NowMs { get; }
}

public sealed class SystemMonotonicClock : IMonotonicClock
{
    public static readonly SystemMonotonicClock Instance = new();

    public double NowMs => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// Elapsed-time accumulator for a single live session. The UI tick only reads it;
/// display and billing come from the same source. Supports excluding intervals
/// (e.g. system suspend) by stopping it.
/// </summary>
public sealed class SessionClock
{
    private readonly IMonotonicClock _clock;
    private readonly object _gate = new();
    private double _accumulatedMs;
    private double? _startedAt;

    public SessionClock(IMonotonicClock clock) => _clock = clock;

    public bool IsRunning
    {
        get { lock (_gate) return _startedAt.HasValue; }
    }

    public void Start()
    {
        lock (_gate)
        {
            _accumulatedMs = 0;
            _startedAt = _clock.NowMs;
        }
    }

    /// <summary>Stops the clock and returns the frozen elapsed time (Stop snapshot).</summary>
    public TimeSpan Stop()
    {
        lock (_gate)
        {
            if (_startedAt is { } s)
            {
                _accumulatedMs += Math.Max(0, _clock.NowMs - s);
                _startedAt = null;
            }
            return TimeSpan.FromMilliseconds(_accumulatedMs);
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                var running = _startedAt is { } s ? Math.Max(0, _clock.NowMs - s) : 0;
                return TimeSpan.FromMilliseconds(_accumulatedMs + running);
            }
        }
    }

    /// <summary>Whole seconds, floored (TIME 01).</summary>
    public int ElapsedWholeSeconds => (int)Math.Floor(Elapsed.TotalSeconds);

    /// <summary>"mm:ss"; minutes keep growing past 59 (TIME 01).</summary>
    public static string Format(int totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        return $"{totalSeconds / 60:00}:{totalSeconds % 60:00}";
    }
}
