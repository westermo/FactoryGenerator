using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FactoryGenerator;
using Inherited;
using Inheritor;
using Inheritor.Generated;

namespace Benchmarks;

// ── Dictionary-based resolution (existing path) ──────────────────────────────

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[JsonExporterAttribute.FullCompressed]
public class ResolveBenchmarks
{
    private readonly DependencyInjectionContainer m_container = new(default, default, new NonInjectedClass());

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
    public IContainer CreateFromSelf() => new DependencyInjectionContainer(m_container);
}

// ── Static-extension resolution (C# 14 / .NET 10+ path) ─────────────────────
//
// Each Resolve(container?) call inlines the full construction chain directly —
// no dictionary lookup, no factory-method indirection.
//
// Null-container variants bypass the singleton cache entirely and perform a
// fresh allocation on every call, exposing the raw construction cost.

[MemoryDiagnoser]
[JsonExporterAttribute.Full]
[JsonExporterAttribute.FullCompressed]
public class StaticExtensionBenchmarks
{
    private readonly DependencyInjectionContainer m_container = new(default, default, new NonInjectedClass());
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
