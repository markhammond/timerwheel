using System.Numerics;
using System.Runtime.CompilerServices;

namespace TimerWheel;

/// <summary>Receives expired timers from <see cref="TimerWheel{TPayload}.Poll{TSink}(long, ref TSink)"/>: a struct
/// (or ref struct), so delivery is a direct call with no allocation.</summary>
public interface ITimerSink<TPayload>
{
    /// <summary>Timer <paramref name="timerId"/> is due; <paramref name="payload"/> is what it was scheduled with.</summary>
    void OnExpired(long timerId, in TPayload payload);
}

/// <summary>
/// Single-threaded hashed timing wheel:
/// <list type="bullet">
/// <item>Timers live in a slab of parallel arrays, linked into spokes as intrusive doubly-linked lists, first in
/// first out: O(1) schedule and cancel, no per-spoke capacity, no reallocation of the wheel (the slab grows
/// geometrically and is reused; no allocation once warm).</item>
/// <item>Ids are <c>generation &lt;&lt; 32 | (index + 1)</c>: a stale id (fired or cancelled timer, slot reused)
/// fails the generation check instead of cancelling someone else's timer. 0 is never a valid id.</item>
/// <item>Poll walks only the spokes in [current tick, now tick] that the occupancy bitmap marks non-empty, and at
/// most the whole wheel once: a large time jump (backtests) costs O(spokes / 64 + timers visited), not O(ticks).</item>
/// <item>Expired timers are collected first and delivered after the scan, so a sink scheduling new timers (a
/// recurring timer, even with a zero period) never fires them within the same poll. A sink may cancel a timer
/// collected but not yet delivered: it is then not delivered.</item>
/// <item>A poll can be bounded: past the limit, due timers wait for the next poll (delayed, never lost), the oldest
/// first, so a steady stream of new due timers never starves an old one.</item>
/// <item>A sink that throws leaves the wheel consistent: the timers it had not reached yet are delivered first by
/// the next poll. A sink must not poll.</item>
/// </list>
/// Time is a <c>long</c> (UTC ticks); resolution is 2^shift ticks. A timer fires in the first poll whose
/// <c>now</c> is at or past its deadline.
/// </summary>
public sealed class TimerWheel<TPayload>
{
    private readonly int _shift;
    private readonly int _mask;
    private const int Unused = -1, Collected = -2, CollectedCancelled = -3;   // _spoke values off the wheel

    private readonly int[] _heads, _tails;
    private readonly ulong[] _occupied;
    private long _tick;                 // every timer with deadline tick < _tick has fired

    private long[] _deadline;
    private int[] _next, _prev, _spoke;  // _spoke: a spoke, or Unused (_next chains the free list), Collected or CollectedCancelled
    private uint[] _gen;
    private TPayload[] _payload;
    private int _free = -1, _used;
    private readonly List<int> _expired = new();  // collected, not yet delivered (in delivery order)
    private bool _polling;

    /// <summary>An empty wheel whose time starts at <paramref name="start"/>.</summary>
    /// <param name="start">The current time (e.g. UTC ticks); polls before it fire nothing.</param>
    /// <param name="resolutionShift">Timer resolution: 2^shift time units per tick (10: ~0.1 ms in 100 ns ticks).</param>
    /// <param name="spokes">Slots on the wheel: a power of two, at least 64. A timer more than spokes x resolution away
    /// waits in its slot through whole turns of the wheel.</param>
    /// <param name="initialCapacity">Timers before the slab first grows (it grows geometrically and is reused).</param>
    public TimerWheel(long start, int resolutionShift = 10, int spokes = 512, int initialCapacity = 64)
    {
        if (!BitOperations.IsPow2(spokes) || spokes < 64) throw new ArgumentOutOfRangeException(nameof(spokes), "Power of two, >= 64.");
        ArgumentOutOfRangeException.ThrowIfNegative(resolutionShift);
        _shift = resolutionShift;
        _mask = spokes - 1;
        _heads = new int[spokes];
        _tails = new int[spokes];
        Array.Fill(_heads, -1);
        Array.Fill(_tails, -1);
        _occupied = new ulong[spokes / 64];
        _tick = start >> _shift;
        _deadline = new long[initialCapacity];
        _next = new int[initialCapacity];
        _prev = new int[initialCapacity];
        _spoke = new int[initialCapacity];
        _gen = new uint[initialCapacity];
        _payload = new TPayload[initialCapacity];
    }

    /// <summary>Timers scheduled and not yet fired or cancelled.</summary>
    public int Count { get; private set; }

    /// <summary>Schedules <paramref name="payload"/> for <paramref name="deadline"/> (a deadline already past fires on
    /// the next poll) and returns its id: never 0, and invalid once the timer has fired or been cancelled.</summary>
    public long Schedule(long deadline, in TPayload payload)
    {
        int i = Alloc();
        _deadline[i] = deadline;
        _payload[i] = payload;
        long tick = Math.Max(deadline >> _shift, _tick);
        int s = (int)(tick & _mask);
        Link(i, s);
        Count++;
        return ((long)_gen[i] << 32) | (uint)(i + 1);
    }

    /// <summary>Cancels timer <paramref name="timerId"/>; false if it already fired, was cancelled or never existed
    /// (a stale id never cancels another timer that reused its slot). From a sink, it also cancels a timer the same
    /// poll collected but has not delivered yet.</summary>
    public bool Cancel(long timerId)
    {
        int i = (int)(uint)timerId - 1;
        if ((uint)i >= (uint)_used || _gen[i] != (uint)(timerId >> 32)) return false;
        int state = _spoke[i];
        if (state == Collected)
        {
            _spoke[i] = CollectedCancelled;   // off the wheel already: delivery skips it, then frees it
        }
        else if (state >= 0)
        {
            Unlink(i);
            Free(i);
        }
        else
        {
            return false;
        }
        Count--;
        return true;
    }

    /// <summary>Fires every timer whose deadline is &lt;= <paramref name="now"/>; returns how many.</summary>
    public int Poll<TSink>(long now, ref TSink sink) where TSink : ITimerSink<TPayload>, allows ref struct =>
        Poll(now, ref sink, int.MaxValue, out _);

    /// <summary>
    /// Fires at most <paramref name="limit"/> timers whose deadline is &lt;= <paramref name="now"/>, in spoke order
    /// (scheduling order within a spoke). If more are due, the wheel stops where it was and resumes there on the next
    /// poll (<paramref name="more"/>): due timers are delayed, never lost or fired early.
    /// <para>Returns the callbacks made. A timer cancelled by the sink after this poll collected it is not delivered,
    /// yet still takes one of the poll's <paramref name="limit"/> places: fewer than <paramref name="limit"/> does not
    /// mean nothing is due, so loop on <paramref name="more"/>.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">A sink polled.</exception>
    public int Poll<TSink>(long now, ref TSink sink, int limit, out bool more) where TSink : ITimerSink<TPayload>, allows ref struct
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (_polling) throw new InvalidOperationException("A sink must not poll the wheel delivering to it.");
        _polling = true;
        try
        {
            return PollCore(now, ref sink, limit, out more);
        }
        finally
        {
            _polling = false;
        }
    }

    private int PollCore<TSink>(long now, ref TSink sink, int limit, out bool more) where TSink : ITimerSink<TPayload>, allows ref struct
    {
        long nowTick = now >> _shift;
        if (nowTick < _tick) nowTick = _tick;    // time never moves the wheel backwards
        long resume = nowTick;
        more = false;
        if (_expired.Count >= limit)             // a sink threw: what it hadn't reached fills this poll
        {
            more = true;
            resume = _tick;
        }
        else if (Count != 0)
        {
            int spokes = _mask + 1;
            long span = nowTick - _tick + 1;
            int start = (int)(_tick & _mask), count = span >= spokes ? spokes : (int)span;
            for (int k = 0; k < count && !more;)
            {
                int s = (start + k) & _mask;
                ulong w = _occupied[s >> 6] >> (s & 63);
                if (w == 0) { k += 64 - (s & 63); continue; }
                if ((w & 1) == 0) { k += BitOperations.TrailingZeroCount(w); continue; }
                for (int i = _heads[s]; i >= 0;)
                {
                    int next = _next[i];
                    if (_deadline[i] <= now)
                    {
                        if (_expired.Count == limit)
                        {
                            more = true;           // resume from this spoke next time
                            resume = _tick + k;
                            break;
                        }
                        Unlink(i);
                        _spoke[i] = Collected;
                        _expired.Add(i);
                    }
                    i = next;
                }
                k++;
            }
        }
        _tick = resume;
        int fired = 0, taken = 0, deliver = Math.Min(_expired.Count, limit);
        try
        {
            while (taken < deliver)
            {
                int i = _expired[taken++];
                if (_spoke[i] == CollectedCancelled)
                {
                    Free(i);       // cancelled by the sink after it was collected: counted off already
                    continue;
                }
                long id = ((long)_gen[i] << 32) | (uint)(i + 1);
                var payload = _payload[i];
                Free(i);           // the id is stale before the sink runs: cancelling it from the sink is a no-op
                Count--;
                fired++;
                sink.OnExpired(id, in payload);
            }
        }
        finally
        {
            _expired.RemoveRange(0, taken);   // a throw leaves the rest collected, for the next poll to deliver first
        }
        if (_expired.Count != 0) more = true;
        return fired;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Link(int i, int s)  // at the tail: a spoke delivers its timers in the order they were scheduled
    {
        int t = _tails[s];
        _prev[i] = t;
        _next[i] = -1;
        if (t >= 0) _next[t] = i;
        else
        {
            _heads[s] = i;
            _occupied[s >> 6] |= 1UL << (s & 63);
        }
        _tails[s] = i;
        _spoke[i] = s;
    }

    private void Unlink(int i)
    {
        int s = _spoke[i], p = _prev[i], n = _next[i];
        if (p >= 0) _next[p] = n;
        else _heads[s] = n;
        if (n >= 0) _prev[n] = p;
        else _tails[s] = p;
        if (_heads[s] < 0) _occupied[s >> 6] &= ~(1UL << (s & 63));
    }

    private int Alloc()
    {
        int i;
        if (_free >= 0)
        {
            i = _free;
            _free = _next[i];
        }
        else
        {
            if (_used == _deadline.Length) Grow();
            i = _used++;
            _gen[i] = 1;
        }
        return i;
    }

    private void Free(int i)
    {
        _spoke[i] = Unused;
        _gen[i] = _gen[i] == uint.MaxValue ? 1 : _gen[i] + 1;
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TPayload>()) _payload[i] = default!;
        _next[i] = _free;
        _free = i;
    }

    private void Grow()
    {
        int n = _deadline.Length * 2;
        Array.Resize(ref _deadline, n);
        Array.Resize(ref _next, n);
        Array.Resize(ref _prev, n);
        Array.Resize(ref _spoke, n);
        Array.Resize(ref _gen, n);
        Array.Resize(ref _payload, n);
    }
}
