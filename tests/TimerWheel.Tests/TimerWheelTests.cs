using TimerWheel;

namespace TimerWheel.Tests;

public class TimerWheelTests
{
    private struct Collect(List<(long Id, int Payload)> fired) : ITimerSink<int>
    {
        public void OnExpired(long timerId, in int payload) => fired.Add((timerId, payload));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Wheel_MatchesNaiveModel(int seed)
    {
        var rnd = new Random(seed);
        long now = 1_000_000;
        var wheel = new TimerWheel<int>(now, resolutionShift: 4, spokes: 64, initialCapacity: 4);
        var model = new Dictionary<long, (long Deadline, int Payload)>();
        var stale = new List<long>();
        var fired = new List<(long, int)>();
        int payload = 0;
        for (int step = 0; step < 20_000; step++)
        {
            int r = rnd.Next(100);
            if (r < 45)
            {
                long deadline = now + rnd.Next(-50, 3000) * (rnd.Next(10) == 0 ? 1000L : 1);  // some far, some past
                long id = wheel.Schedule(deadline, ++payload);
                Assert.NotEqual(0, id);
                Assert.DoesNotContain(id, model.Keys);
                model[id] = (deadline, payload);
            }
            else if (r < 60 && model.Count > 0)
            {
                long id = model.Keys.ElementAt(rnd.Next(model.Count));
                Assert.True(wheel.Cancel(id));
                model.Remove(id);
                stale.Add(id);
            }
            else if (r < 65 && stale.Count > 0)
            {
                Assert.False(wheel.Cancel(stale[rnd.Next(stale.Count)]));   // stale ids never hit a live timer
            }
            else
            {
                now += rnd.Next(10) == 0 ? rnd.Next(100_000, 5_000_000) : rnd.Next(0, 200);   // occasional huge jumps
                fired.Clear();
                var sink = new Collect(fired);
                wheel.Poll(now, ref sink);
                var due = model.Where(kv => kv.Value.Deadline <= now).Select(kv => (kv.Key, kv.Value.Payload)).OrderBy(x => x.Key).ToArray();
                Assert.Equal(due, fired.OrderBy(x => x.Item1).ToArray());
                foreach (var (id, _) in due)
                {
                    model.Remove(id);
                    stale.Add(id);
                }
            }
            Assert.Equal(model.Count, wheel.Count);
        }
    }

    private struct Reschedule(TimerWheel<int> wheel, List<long> fired, long period) : ITimerSink<int>
    {
        public void OnExpired(long timerId, in int payload)
        {
            fired.Add(timerId);
            wheel.Schedule(0 + period, payload);   // zero/past deadline from inside the sink: next poll, not this one
        }
    }

    [Fact]
    public void Wheel_SinkScheduling_NeverRefiresWithinTheSamePoll()
    {
        var wheel = new TimerWheel<int>(0, resolutionShift: 0, spokes: 64);
        wheel.Schedule(5, 1);
        var fired = new List<long>();
        var sink = new Reschedule(wheel, fired, 0);
        Assert.Equal(1, wheel.Poll(10, ref sink));
        Assert.Single(fired);
        Assert.Equal(1, wheel.Count);
        Assert.Equal(1, wheel.Poll(10, ref sink));   // the rescheduled one fires on the next poll
    }

    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Wheel_BoundedPolls_DelayButNeverLoseOrFireEarly(int seed)
    {
        var rnd = new Random(seed);
        long now = 0;
        var wheel = new TimerWheel<int>(now, resolutionShift: 3, spokes: 64, initialCapacity: 4);
        var model = new Dictionary<long, long>();          // id -> deadline
        var fired = new List<(long Id, int Payload)>();
        int payload = 0, limited = 0;
        for (int step = 0; step < 20_000; step++)
        {
            if (rnd.Next(100) < 55)
            {
                long deadline = now + rnd.Next(-20, 2000) * (rnd.Next(8) == 0 ? 500L : 1);
                model[wheel.Schedule(deadline, ++payload)] = deadline;
                continue;
            }
            now += rnd.Next(5) == 0 ? rnd.Next(10_000, 2_000_000) : rnd.Next(0, 100);
            int limit = rnd.Next(1, 6);
            fired.Clear();
            var sink = new Collect(fired);
            int n = wheel.Poll(now, ref sink, limit, out bool more);
            int due = model.Values.Count(d => d <= now);
            Assert.All(fired, f => Assert.True(model[f.Id] <= now, "fired before its deadline"));
            Assert.Equal(Math.Min(limit, due), n);
            Assert.Equal(due > limit, more);                // more is reported exactly when due timers remain
            if (more) limited++;
            foreach (var (id, _) in fired) model.Remove(id);
        }
        // Draining with a tiny limit eventually delivers everything that is due.
        now += 10_000_000;
        int total = 0;
        bool pending;
        do
        {
            fired.Clear();
            var sink = new Collect(fired);
            total += wheel.Poll(now, ref sink, 3, out pending);
        } while (pending);
        Assert.Equal(model.Count, total);
        Assert.Equal(0, wheel.Count);
        Assert.True(limited > 100);
    }

    private struct CancelAnother(TimerWheel<int> wheel, List<int> fired, long[] ids) : ITimerSink<int>
    {
        public bool Cancelled;

        public void OnExpired(long timerId, in int payload)
        {
            fired.Add(payload);
            if (fired.Count == 1) Cancelled = wheel.Cancel(Array.Find(ids, id => id != timerId));
        }
    }

    [Fact]
    public void Wheel_SinkCancelsATimerCollectedInTheSamePoll_ItIsNotDelivered()
    {
        var wheel = new TimerWheel<int>(0, resolutionShift: 0, spokes: 64);
        long[] ids = [wheel.Schedule(5, 1), wheel.Schedule(5, 2), wheel.Schedule(5, 3)];
        var fired = new List<int>();
        var sink = new CancelAnother(wheel, fired, ids);
        Assert.Equal(2, wheel.Poll(10, ref sink));
        Assert.True(sink.Cancelled);
        Assert.Equal([1, 3], fired);                     // 2, cancelled from the sink, not delivered
        Assert.Equal(0, wheel.Count);
        Assert.False(wheel.Cancel(ids[1]));              // cancelled once only

        // The slab is intact: new timers take distinct slots, and each fires once.
        var slots = Enumerable.Range(0, 6).Select(n => wheel.Schedule(20, 10 + n)).Select(id => (uint)id).ToArray();
        Assert.Equal(slots.Length, slots.Distinct().Count());
        var later = new List<(long, int)>();
        var collect = new Collect(later);
        Assert.Equal(6, wheel.Poll(30, ref collect));
        Assert.Equal([10, 11, 12, 13, 14, 15], later.Select(f => f.Item2));
        Assert.Equal(0, wheel.Count);
    }

    private sealed class Boom : Exception;

    private struct ThrowAt(List<int> fired, int at) : ITimerSink<int>
    {
        public void OnExpired(long timerId, in int payload)
        {
            fired.Add(payload);
            if (fired.Count == at) throw new Boom();
        }
    }

    [Fact]
    public void Wheel_SinkThrows_TheRestAreDeliveredOnceByTheNextPolls()
    {
        var wheel = new TimerWheel<int>(0, resolutionShift: 0, spokes: 64);
        for (int n = 1; n <= 5; n++) wheel.Schedule(5, n);
        var fired = new List<int>();
        var throwing = new ThrowAt(fired, 2);
        Assert.Throws<Boom>(() => wheel.Poll(10, ref throwing));
        Assert.Equal([1, 2], fired);
        Assert.Equal(3, wheel.Count);                    // 3, 4 and 5 still to come

        var next = new ThrowAt(fired, int.MaxValue);
        Assert.Equal(2, wheel.Poll(10, ref next, limit: 2, out bool more));   // the pending ones first, within the limit
        Assert.True(more);
        Assert.Equal(1, wheel.Poll(10, ref next, limit: 2, out more));
        Assert.False(more);
        Assert.Equal([1, 2, 3, 4, 5], fired);            // each exactly once, in order
        Assert.Equal(0, wheel.Count);
    }

    [Fact]
    public void Wheel_BoundedPolls_DeliverTheOldestFirst_ANewStreamNeverStarvesIt()
    {
        var wheel = new TimerWheel<int>(start: 1, resolutionShift: 0, spokes: 64);
        for (int n = 0; n < 4; n++) wheel.Schedule(1, n);   // a backlog in the current spoke
        var fired = new List<(long, int)>();
        var sink = new Collect(fired);
        for (long now = 2; now < 12; now++)
        {
            wheel.Schedule(0, 100 + (int)now);          // a new timer every poll, its deadline long past: into the same spoke
            wheel.Poll(now, ref sink, limit: 1, out _);
        }
        Assert.Equal([0, 1, 2, 3, 102, 103, 104, 105, 106, 107], fired.Select(f => f.Item2));
    }

    [Fact]
    public void Wheel_ASpokeDeliversInSchedulingOrder()
    {
        var wheel = new TimerWheel<int>(0, resolutionShift: 4, spokes: 64);
        for (int n = 0; n < 10; n++) wheel.Schedule(3 + (n % 4), n);   // one tick (resolution 16), deadlines interleaved
        var fired = new List<(long, int)>();
        var sink = new Collect(fired);
        wheel.Poll(15, ref sink);
        Assert.Equal(Enumerable.Range(0, 10), fired.Select(f => f.Item2));
    }

    private struct PollAgain(TimerWheel<int> wheel) : ITimerSink<int>
    {
        public Exception? Refused;

        public void OnExpired(long timerId, in int payload)
        {
            var inner = new Collect([]);
            try { wheel.Poll(100, ref inner); }
            catch (InvalidOperationException e) { Refused = e; }
        }
    }

    [Fact]
    public void Wheel_ASinkThatPolls_IsRefused_AndTheWheelCarriesOn()
    {
        var wheel = new TimerWheel<int>(0, resolutionShift: 0, spokes: 64);
        wheel.Schedule(5, 1);
        wheel.Schedule(50, 2);
        var sink = new PollAgain(wheel);
        Assert.Equal(1, wheel.Poll(10, ref sink));
        Assert.NotNull(sink.Refused);
        var fired = new List<(long, int)>();
        var collect = new Collect(fired);
        Assert.Equal(1, wheel.Poll(60, ref collect));
        Assert.Equal(2, fired.Single().Item2);
    }

    [Fact]
    public void Wheel_BoundedPoll_ALimitBelowOne_IsRefused_NotSkippingTimers()
    {
        var wheel = new TimerWheel<int>(0, resolutionShift: 0, spokes: 64);
        wheel.Schedule(5, 1);
        var fired = new List<(long, int)>();
        var sink = new Collect(fired);
        Assert.Throws<ArgumentOutOfRangeException>(() => wheel.Poll(10, ref sink, 0, out _));
        Assert.Equal(1, wheel.Poll(11, ref sink));        // not passed over by the refused poll
    }

    private sealed class Chaos(TimerWheel<int> wheel, Dictionary<long, long> live, Random rnd)
    {
        public long Now;
        public int Delivered, CancelledInSink;

        public void Expired(long timerId)
        {
            Assert.True(live.Remove(timerId, out long deadline), "delivered: a timer cancelled, delivered already, or unknown");
            Assert.True(deadline <= Now, "fired before its deadline");
            Delivered++;
            if (rnd.Next(4) == 0 && live.Count > 0)      // cancel another, maybe one collected by this very poll
            {
                long other = live.Keys.ElementAt(rnd.Next(live.Count));
                Assert.True(wheel.Cancel(other));
                live.Remove(other);
                CancelledInSink++;
            }
            if (rnd.Next(10) == 0) live[wheel.Schedule(Now - rnd.Next(0, 50), 0)] = Now;   // due, from the sink
            if (rnd.Next(40) == 0) throw new Boom();
        }
    }

    private struct ChaosSink(Chaos chaos) : ITimerSink<int>
    {
        public void OnExpired(long timerId, in int payload) => chaos.Expired(timerId);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Wheel_SinksThatCancelScheduleAndThrow_MatchTheModel(int seed)
    {
        var rnd = new Random(seed);
        var wheel = new TimerWheel<int>(0, resolutionShift: 3, spokes: 64, initialCapacity: 4);
        var live = new Dictionary<long, long>();          // id -> deadline, scheduled and neither fired nor cancelled
        var chaos = new Chaos(wheel, live, rnd);
        int throws = 0;
        for (int step = 0; step < 20_000; step++)
        {
            if (rnd.Next(100) < 50)
            {
                long deadline = chaos.Now + rnd.Next(-20, 2000) * (rnd.Next(8) == 0 ? 500L : 1);
                long id = wheel.Schedule(deadline, 0);
                Assert.DoesNotContain(id, live.Keys);
                live[id] = deadline;
                continue;
            }
            chaos.Now += rnd.Next(5) == 0 ? rnd.Next(10_000, 2_000_000) : rnd.Next(0, 100);
            var sink = new ChaosSink(chaos);
            try
            {
                if (rnd.Next(2) == 0) wheel.Poll(chaos.Now, ref sink);
                else wheel.Poll(chaos.Now, ref sink, rnd.Next(1, 6), out _);
            }
            catch (Boom)
            {
                throws++;
            }
            Assert.Equal(live.Count, wheel.Count);
        }
        // Drained: everything due is delivered, once.
        chaos.Now += 10_000_000;
        for (bool done = false; !done;)
        {
            var sink = new ChaosSink(chaos);
            try
            {
                wheel.Poll(chaos.Now, ref sink);
                done = live.Values.All(d => d > chaos.Now);
            }
            catch (Boom)
            {
                throws++;
            }
        }
        Assert.Empty(live);
        Assert.Equal(0, wheel.Count);
        Assert.True(throws > 50 && chaos.CancelledInSink > 50, $"exercised: {throws} throws, {chaos.CancelledInSink} cancels from a sink");
    }
}
