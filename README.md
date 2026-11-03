# TimerWheel

[![NuGet](https://img.shields.io/nuget/v/TimerWheel.svg)](https://www.nuget.org/packages/TimerWheel)

A caller-driven hashed timing wheel for .NET, allocation-free once warm. No background threads, no internal scheduling and no clock ownership.

Designed for single-threaded event loops, simulations and deterministic testing where the host controls time and execution. Not thread-safe: schedule, cancel and poll from one thread (the loop's).

- **O(1) scheduling and cancellation.** Timers occupy a reusable slab of parallel arrays, linked into wheel spokes. Scheduling is amortized O(1): the slab doubles when full, so allocations stop once it is warm (`initialCapacity` sizes it up front).
- **Generation-safe handles.** Timer IDs carry a generation, preventing stale handles from canceling timers assigned to reused slots.
- **Efficient time advancement.** An occupancy bitmap skips empty spokes, making large time jumps cost O(spokes / 64 + timers in the spokes visited), rather than O(ticks).
- **Optimizing for responsiveness.** Bound callbacks per poll to prevent timer bursts monopolizing the event loop. Deferred timers retain deadline order, to the spoke; newer timers never overtake older due timers.
- **Bounded, lossless delivery.** Per-poll delivery limits defer excess timers without losing them. When timers fall due faster than polls deliver them, the backlog drains oldest first, and later-due timers wait until it clears.
- **Safe callbacks.** Timers scheduled during delivery, including zero-period recurring timers, are deferred to the next poll. Cancellation takes effect immediately, even for timers already collected for delivery. A callback must not poll the wheel delivering to it.
- **Exception-safe delivery.** If a delivery sink throws, the wheel remains consistent. Undelivered timers retain their priority on the next poll.

## Design philosophy

TimerWheel deliberately separates time management from execution.

The host owns the clock, decides when the wheel advances, and determines where callbacks execute. TimerWheel manages deadlines and expiration without imposing a threading model, event loop or scheduling discipline.

This makes it suitable for embedding into existing runtimes rather than competing with them.

## Usage

Install [TimerWheel from NuGet](https://www.nuget.org/packages/TimerWheel):

```bash
dotnet add package TimerWheel
```

Time is a `long` (e.g. UTC ticks) supplied by you. A timer fires on the first poll at or past its exact deadline; 2^shift time units set the width of a spoke, trading scan cost against how far one revolution of the wheel reaches.

```csharp
using TimerWheel;

var wheel = new TimerWheel<string>(start: now, resolutionShift: 10, spokes: 512);
long id = wheel.Schedule(now + TimeSpan.FromSeconds(1).Ticks, "flush batch");
// wheel.Cancel(id);

var sink = new Dispatch();
wheel.Poll(now + TimeSpan.FromSeconds(2).Ticks, ref sink);   // delivers "flush batch"

struct Dispatch : ITimerSink<string>
{
    public void OnExpired(long timerId, in string payload) => Console.WriteLine(payload);
}
```

## Why not a priority queue?

The .NET Base Class Library (BCL) already has a `PriorityQueue<TElement, TPriority>`. It is well tested, and answers which deadline is next. A well-tuned priority queue also tracks each timer's position, so it can cancel in O(log n) steps. But timers rarely need the next deadline, and even a well-tuned queue is slower at what timers do most.

- **Most timers never fire.** Timeouts, retries and watchdogs are scheduled in advance, in the expectation of being cancelled. The BCL's queue can't cancel cheaply, so cancelled timers are left in place until their deadline; a well-tuned queue cancels in O(log n) steps. Here, cancelling takes constant time.
- **Firing is cheaper.** Each timer a priority queue fires costs O(log n) steps, each likely a cache miss once the queue is large. Here, it costs constant time, plus a scan of the spokes passed (see below).
- **Better tail performance.** Adding is a draw on average, but a short deadline among long ones climbs the whole queue: O(log n). Here, scheduling and cancelling take constant time, every time, once storage is warm (or sized with `initialCapacity`).

### When a priority queue is better

The wheel has real costs too:

- **Exact order.** A priority queue fires in deadline order. This wheel orders by spoke: within a spoke (2^shift time units), timers fire in the order they were scheduled.
- **Knowing when to wake.** A priority queue can `Peek` the next deadline. This wheel can't without a scan, so it expects to be polled at a steady rate.
- **Far-off timers.** A timer more than one turn away (spokes × 2^shift) is checked on every pass of its spoke; a priority queue never looks at it early. Size the wheel so most deadlines fall within one turn.
- **Polling isn't free.** Each poll scans the spokes passed since the last, even for one far-off timer; a priority queue checks a single item.
- **Small queues.** Constant time is not zero time: a timer's fields span several cache lines, while a small heap fits in cache. With a few hundred timers, a well-tuned queue may well be faster.
- **Memory isn't given back.** Storage grows to the most timers ever held, and stays there.
- **These are arguments, not measurements.** If your timers are rarely cancelled, wait many turns, or need exact order, use a priority queue. Otherwise, measure your workload before trusting either side, including this README.

## Build

```bash
dotnet test TimerWheel.slnx
```

## License

MIT. See [LICENSE](https://github.com/markhammond/timerwheel/blob/main/LICENSE).
