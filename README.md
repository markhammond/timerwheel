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

## Build

```bash
dotnet test TimerWheel.slnx
```

## License

MIT. See [LICENSE](https://github.com/markhammond/timerwheel/blob/main/LICENSE).
