using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace FactoryGenerator;

#nullable enable

public sealed class ResolvedInstanceTracker : IDisposable, IAsyncDisposable
{
    private enum DisposalMode
    {
        Active = 0,
        Synchronous = 1,
        Asynchronous = 2
    }

    private readonly object m_lock = new object();
    private List<WeakReference<object>>? m_instances = new List<WeakReference<object>>();
    private DisposalMode m_disposalMode;

    public void Track(object? instance)
    {
        if (instance is null)
            return;

        if (instance is not IDisposable && instance is not IAsyncDisposable)
            return;

        DisposalMode disposalMode;
        lock (m_lock)
        {
            disposalMode = m_disposalMode;
            if (disposalMode == DisposalMode.Active)
            {
                m_instances!.Add(new WeakReference<object>(instance));
                return;
            }
        }

        if (disposalMode == DisposalMode.Asynchronous)
        {
            DisposeAsynchronously(instance).AsTask().GetAwaiter().GetResult();
            return;
        }

        DisposeSynchronously(instance);
    }

    public void Dispose()
    {
        var trackedInstances = BeginSynchronousDisposal();
        if (trackedInstances is null)
            return;

        for (var index = trackedInstances.Count - 1; index >= 0; index--)
        {
            if (trackedInstances[index].TryGetTarget(out var instance))
                DisposeSynchronously(instance);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var trackedInstances = BeginAsynchronousDisposal();
        if (trackedInstances is null)
            return;

        for (var index = trackedInstances.Count - 1; index >= 0; index--)
        {
            if (trackedInstances[index].TryGetTarget(out var instance))
                await DisposeAsynchronously(instance).ConfigureAwait(false);
        }
    }

    private List<WeakReference<object>>? BeginSynchronousDisposal()
    {
        lock (m_lock)
        {
            if (m_disposalMode != DisposalMode.Active)
                return null;

            m_disposalMode = DisposalMode.Synchronous;
            var instances = m_instances;
            m_instances = null;
            return instances;
        }
    }

    private List<WeakReference<object>>? BeginAsynchronousDisposal()
    {
        lock (m_lock)
        {
            if (m_disposalMode != DisposalMode.Active)
                return null;

            m_disposalMode = DisposalMode.Asynchronous;
            var instances = m_instances;
            m_instances = null;
            return instances;
        }
    }

    private static void DisposeSynchronously(object instance)
    {
        if (instance is IDisposable disposable)
        {
            disposable.Dispose();
            return;
        }

        if (instance is IAsyncDisposable asyncDisposable)
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static ValueTask DisposeAsynchronously(object instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        if (instance is IDisposable disposable)
            disposable.Dispose();

        return default;
    }
}
