using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FactoryGenerator;
using Inherited;
using Inheritor;
using Inheritor.Generated;

namespace Benchmarks;

// ── Dictionary-based resolution (existing path) ──────────────────────────────

[MemoryDiagnoser]
[Config(typeof(AccurateMicroBenchmarkConfig))]
[JsonExporterAttribute.Full]
[JsonExporterAttribute.FullCompressed]
public class ResolveBenchmarks
{
    private readonly DependencyInjectionContainer m_container = new(default, default, new NonInjectedClass());
    private ILifetimeScope m_scope = null!;

    [GlobalSetup]
    public void Setup() => m_scope = m_container.BeginLifetimeScope();

    [GlobalCleanup]
    public void Cleanup() => m_scope.Dispose();

    [Benchmark]
    public ChainA ResolveChain() => m_container.Resolve<ChainA>();

    [Benchmark]
    public IScoped ResolveScoped() => m_container.Resolve<IScoped>();

    [Benchmark]
    public ISingleton ResolveSingleton() => m_container.Resolve<ISingleton>();

    [Benchmark]
    public IOverridable ResolveTransient() => m_container.Resolve<IOverridable>();

    [Benchmark]
    public List<IRequestedArray> ResolveArray() => (List<IRequestedArray>) m_container.Resolve<IEnumerable<IRequestedArray>>();

    [Benchmark]
    public IContainer Create() => new DependencyInjectionContainer(default, default, default!);

    [Benchmark]
    public void CreateFromSelf()
    {
        // Child containers attach to their base until disposed, so each benchmark
        // invocation must clean up or the inheritor chain grows across operations.
        using var child = new DependencyInjectionContainer(m_container);
    }

    [Benchmark]
    public void CreateLifetimeScope()
    {
        // Like CreateFromSelf above, a scope attaches to m_container's Inheritor chain until
        // disposed, so each invocation must clean up or the chain grows across operations.
        // LifetimeScope is now a thin subclass of DependencyInjectionContainer (it inherits every
        // factory/lookup member instead of duplicating them) — this measures that construction path.
        using var scope = m_container.BeginLifetimeScope();
    }

    // ── Resolution through a LifetimeScope ───────────────────────────────────────
    //
    // m_scope is a single long-lived scope (created in Setup, disposed in Cleanup), so these
    // benchmark steady-state resolve cost, not scope creation (see CreateLifetimeScope above).
    //
    // ResolveSingletonThroughScope exercises the owner-forwarding check added to singleton members
    // (`if (m_singletonOwner != this) return m_singletonOwner.X();`) so every singleton resolves to
    // the one instance owned by the root container, regardless of which scope resolves it.
    // ResolveScopedThroughScope is the control case: [Scoped] members are cached per-scope-instance
    // and never forward, so this exercises the unchanged local-cache path for comparison.
    [Benchmark]
    public ISingleton ResolveSingletonThroughScope() => m_scope.Resolve<ISingleton>();

    [Benchmark]
    public IScoped ResolveScopedThroughScope() => m_scope.Resolve<IScoped>();

    // ── Static-extension resolution (C# 14 / .NET 10+ path) ─────────────────────
    //
    // Each Resolve(container?) call inlines the full construction chain directly —
    // no dictionary lookup, no factory-method indirection.
    //
    // Null-container variants bypass the singleton cache entirely and perform a
    // fresh allocation on every call, exposing the raw construction cost.
    [Benchmark]
    public ISingleton ExtensionResolveSingleton() => ISingleton.Resolve(m_container);

    [Benchmark]
    public ISingleton ExtensionResolveSingletonNullContainer() => ISingleton.Resolve(null);

    [Benchmark]
    public IOverridable ExtensionResolveTransient() => IOverridable.Resolve(m_container);

    [Benchmark]
    public ChainA ExtensionResolveChain() => ChainA.Resolve(m_container);

    [Benchmark]
    public ChainA ExtensionResolveChainNullContainer() => ChainA.Resolve(null);

    [Benchmark]
    public ArrayConsumer ExtensionResolveWithCollection() => ArrayConsumer.Resolve(m_container);

    [Benchmark]
    public ArrayConsumer ExtensionResolveWithCollectionNullContainer() => ArrayConsumer.Resolve(null);
}

internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}