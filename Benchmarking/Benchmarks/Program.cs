using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using FactoryGenerator;
using Inherited;
using Inheritor;
using Inheritor.Generated;

namespace Benchmarks;

[JsonExporterAttribute.Full]
[JsonExporterAttribute.FullCompressed]
public class ResolveBenchmarks
{
    private readonly IContainer m_container = new DependencyInjectionContainer(default, default, default!);

    [Benchmark]
    public ChainA ResolveChain() => m_container.Resolve<ChainA>();

    [Benchmark]
    public IScoped ResolveScoped() => m_container.Resolve<IScoped>();

    [Benchmark]
    public ISingleton ResolveSingleton() => m_container.Resolve<ISingleton>();

    [Benchmark]
    public IOverridable ResolveTransient() => m_container.Resolve<IOverridable>();

    [Benchmark]
    public IEnumerable<IRequestedArray> ResolveArray() => m_container.Resolve<IEnumerable<IRequestedArray>>();

    [Benchmark]
    public IContainer Create() => new DependencyInjectionContainer(default, default, default!);
}

internal static class Program
{
    private static void Main(string[] args)
    {
        var summary = BenchmarkRunner.Run<ResolveBenchmarks>();
    }
}