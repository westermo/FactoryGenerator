using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;

namespace FactoryGenerator.Tests;

public class ResolvedInstanceTrackerTests
{
    [Test]
    public void ConcurrentTrackingDuringDisposeDisposesEveryInstance()
    {
        const int count = 128;
        var tracker = new ResolvedInstanceTracker();
        var instances = new ConcurrentBag<SyncDisposableProbe>();
        var start = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, count)
            .Select(_ => Task.Run(() =>
            {
                var instance = new SyncDisposableProbe();
                instances.Add(instance);
                start.Wait();
                tracker.Track(instance);
            }))
            .ToArray();

        start.Set();
        tracker.Dispose();
        Task.WhenAll(tasks).GetAwaiter().GetResult();

        instances.Count.ShouldBe(count);
        instances.All(instance => instance.WasDisposed).ShouldBeTrue();
    }

    [Test]
    public void SynchronousDisposeWaitsForAsyncOnlyInstances()
    {
        var tracker = new ResolvedInstanceTracker();
        var instance = new AsyncDisposableProbe();
        tracker.Track(instance);

        tracker.Dispose();

        instance.WasDisposed.ShouldBeTrue();
    }

    [Test]
    public async Task AsynchronousDisposeDisposesAsyncOnlyInstances()
    {
        var tracker = new ResolvedInstanceTracker();
        var instance = new AsyncDisposableProbe();
        tracker.Track(instance);

        await tracker.DisposeAsync();

        instance.WasDisposed.ShouldBeTrue();
    }

    [Test]
    public void TrackingDoesNotKeepObjectsAlive()
    {
        var tracker = new ResolvedInstanceTracker();
        var weakReference = CreateTrackedWeakReference(tracker);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        weakReference.TryGetTarget(out _).ShouldBeFalse();
        tracker.Dispose();
    }

    private static WeakReference<object> CreateTrackedWeakReference(ResolvedInstanceTracker tracker)
    {
        var instance = new SyncDisposableProbe();
        tracker.Track(instance);
        return new WeakReference<object>(instance);
    }

    private sealed class SyncDisposableProbe : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose()
        {
            WasDisposed = true;
        }
    }

    private sealed class AsyncDisposableProbe : IAsyncDisposable
    {
        public bool WasDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            WasDisposed = true;
            return default;
        }
    }
}
