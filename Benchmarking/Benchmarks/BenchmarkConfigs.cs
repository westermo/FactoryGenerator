using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using Perfolizer.Mathematics.OutlierDetection;

namespace Benchmarks;

/// <summary>
/// Job configuration for the source-generator ("cold start") benchmarks in <see cref="GeneratorBenchmarks"/>.
///
/// Each Cold_* benchmark drives a full Roslyn compilation plus a generator run, costing anywhere from
/// ~1ms to ~20ms per invocation. The previous <c>[ShortRunJob]</c> preset fixed the sample count at
/// exactly 3 iterations (after 3 warmups), which is far too few at this scale: GC pauses, JIT tiering,
/// and OS thread-scheduling noise are all large relative to a single iteration — several Cold_*
/// results measured a standard error larger than the mean itself. This config instead:
///  - keeps a single process launch (<c>LaunchCount=1</c>) — relaunching the whole process mainly
///    re-pays JIT/compilation startup cost, which the warmup stage already amortizes, so a second
///    launch buys little extra accuracy for a much longer total run;
///  - increases warmup to 6 iterations so the JIT has fully tiered up before measurement begins;
///  - replaces the fixed iteration count with an adaptive 15-30 range, giving the engine enough
///    samples to converge on a stable estimate instead of stopping after 3;
///  - removes outliers on both sides (<see cref="OutlierMode.RemoveAll"/>), since GC/JIT blips can
///    push individual iterations slower (common) or faster (rarer) than the true steady-state cost.
/// </summary>
public sealed class AccurateColdStartConfig : ManualConfig
{
    public AccurateColdStartConfig()
    {
        AddJob(new Job("Accurate")
            .WithLaunchCount(1)
            .WithWarmupCount(6)
            .WithMinIterationCount(15)
            .WithMaxIterationCount(30)
            .WithOutlierMode(OutlierMode.RemoveAll));
    }
}

/// <summary>
/// Job configuration for the runtime resolve/construction micro-benchmarks in <see cref="ResolveBenchmarks"/>.
///
/// These benchmarks measure single-digit-to-low-hundreds of nanoseconds per call, so BenchmarkDotNet
/// unrolls each iteration into millions of invocations. At that scale, a single background GC
/// collection (workstation GC's concurrent/background mode can run mid-measurement) is enough to
/// visibly skew an iteration — this is exactly what showed up as periodic outlier spikes (e.g.
/// ResolveChain jumping from ~55ns to 100+ns on isolated iterations) in earlier runs. This config:
///  - disables concurrent/background GC (<c>WithGcConcurrent(false)</c>)
///    so a collection cannot preempt a measurement iteration on a background thread; workstation GC
///    still runs non-concurrently, it simply can no longer interrupt the benchmarked thread mid-iteration;
///  - widens the iteration bounds (15-25) so the dynamic stopping criteria has more samples to work
///    with before it decides the estimate has converged;
///  - removes outliers on both sides (<see cref="OutlierMode.RemoveAll"/>) to further suppress any
///    remaining scheduling noise.
/// </summary>
public sealed class AccurateMicroBenchmarkConfig : ManualConfig
{
    public AccurateMicroBenchmarkConfig()
    {
        AddJob(new Job("Accurate")
            .WithGcServer(false)
            .WithGcConcurrent(false)
            .WithMinIterationCount(15)
            .WithMaxIterationCount(25)
            .WithOutlierMode(OutlierMode.RemoveAll));
    }
}
