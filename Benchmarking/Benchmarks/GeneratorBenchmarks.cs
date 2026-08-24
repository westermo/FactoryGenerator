using System.Collections.Immutable;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using FactoryGenerator.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Benchmarks;

[MemoryDiagnoser]
[Config(typeof(AccurateColdStartConfig))]
[JsonExporterAttribute.Full]
[JsonExporterAttribute.FullCompressed]
public class GeneratorBenchmarks
{
    private ColdGeneratorScenario m_constructorGraph = null!;
    private ColdGeneratorScenario m_constructorGraphWithStaticExtensions = null!;
    private ColdGeneratorScenario m_noiseHeavyProject = null!;
    private ColdGeneratorScenario m_featureRichStaticExtensionsDisabled = null!;
    private ColdGeneratorScenario m_featureRichStaticExtensionsEnabled = null!;
    private ColdGeneratorScenario m_multiAssemblyOverrideGraph = null!;
    private ColdGeneratorScenario m_manyAssembliesGraph = null!;
    private FeatureRichIncrementalScenario m_featureRichIncremental = null!;
    private IncrementalGeneratorScenario m_referenceAssemblyIncremental = null!;

    [GlobalSetup]
    public void Setup()
    {
        m_constructorGraph = GeneratorBenchmarkScenarioFactory.CreateConstructorGraph(serviceCount: 250);
        m_constructorGraphWithStaticExtensions = GeneratorBenchmarkScenarioFactory.CreateConstructorGraphWithStaticExtensions(serviceCount: 250);
        m_noiseHeavyProject = GeneratorBenchmarkScenarioFactory.CreateNoiseHeavyProject(serviceCount: 64, noiseTypeCount: 2000);
        m_featureRichStaticExtensionsDisabled = GeneratorBenchmarkScenarioFactory.CreateFeatureRichGraph(emitStaticExtensions: false);
        m_featureRichStaticExtensionsEnabled = GeneratorBenchmarkScenarioFactory.CreateFeatureRichGraph(emitStaticExtensions: true);
        m_multiAssemblyOverrideGraph = GeneratorBenchmarkScenarioFactory.CreateMultiAssemblyOverrideGraph(baseServiceCount: 128, overrideCount: 16);
        m_manyAssembliesGraph = GeneratorBenchmarkScenarioFactory.CreateManyAssembliesGraph(assemblyCount: 25, typesPerAssembly: 2);
        m_featureRichIncremental = GeneratorBenchmarkScenarioFactory.CreateFeatureRichIncrementalScenario();
        m_referenceAssemblyIncremental = GeneratorBenchmarkScenarioFactory.CreateReferenceAssemblyIncrementalScenario();

        GeneratorBenchmarkHarness.Validate(m_constructorGraph);
        GeneratorBenchmarkHarness.Validate(m_constructorGraphWithStaticExtensions);
        GeneratorBenchmarkHarness.Validate(m_noiseHeavyProject);
        GeneratorBenchmarkHarness.Validate(m_featureRichStaticExtensionsDisabled);
        GeneratorBenchmarkHarness.Validate(m_featureRichStaticExtensionsEnabled);
        GeneratorBenchmarkHarness.Validate(m_multiAssemblyOverrideGraph);
        GeneratorBenchmarkHarness.Validate(m_manyAssembliesGraph);
    }

    [Benchmark]
    public int Cold_ConstructorGraph() => GeneratorBenchmarkHarness.RunCold(m_constructorGraph);

    /// <summary>
    /// Same 250-service linear dependency chain as <see cref="Cold_ConstructorGraph"/>, but with
    /// static extensions enabled — isolates PropagateStaticExtensionRequirements's fixed-point-loop
    /// cost (a hypothesized, previously-untested scaling risk for long chains) from the ordinary
    /// per-injection processing cost already captured by Cold_ConstructorGraph.
    /// </summary>
    [Benchmark]
    public int Cold_ConstructorGraph_StaticExtensionsEnabled() => GeneratorBenchmarkHarness.RunCold(m_constructorGraphWithStaticExtensions);

    [Benchmark]
    public int Cold_NoiseHeavyProject() => GeneratorBenchmarkHarness.RunCold(m_noiseHeavyProject);

    [Benchmark]
    public int Cold_FeatureRichGraph_StaticExtensionsDisabled() => GeneratorBenchmarkHarness.RunCold(m_featureRichStaticExtensionsDisabled);

    [Benchmark]
    public int Cold_FeatureRichGraph_StaticExtensionsEnabled() => GeneratorBenchmarkHarness.RunCold(m_featureRichStaticExtensionsEnabled);

    [Benchmark]
    public int Cold_MultiAssemblyOverrideGraph() => GeneratorBenchmarkHarness.RunCold(m_multiAssemblyOverrideGraph);

    /// <summary>
    /// 25 small assemblies where each layer cumulatively references every prior layer
    /// (O(n^2) reference edges), isolating <c>GetRelevantAssemblies</c>'s assembly-reachability
    /// BFS/DFS cost from per-type scanning cost (already covered by <see cref="Cold_NoiseHeavyProject"/>).
    /// </summary>
    [Benchmark]
    public int Cold_ManyAssembliesGraph() => GeneratorBenchmarkHarness.RunCold(m_manyAssembliesGraph);

    /// <summary>
    /// Re-runs the warmed driver against the exact same compilation it was warmed with. Floor/
    /// reference point for the Incremental_* benchmarks below: since nothing at all changed, this
    /// is the fastest possible incremental re-run and isolates Roslyn's own driver-level overhead
    /// from any FactoryGenerator-specific recomputation.
    /// </summary>
    [Benchmark]
    public int Incremental_NoOpRerun() => GeneratorBenchmarkHarness.RunIncremental(m_featureRichIncremental.WarmDriver, m_featureRichIncremental.BaselineCompilation);

    /// <summary>
    /// Re-runs the warmed driver after only <c>Utilities.cs</c> changed — a file with zero
    /// injectable types, entirely unrelated to dependency injection. In a well-incrementalized
    /// generator this should cost close to <see cref="Incremental_NoOpRerun"/>; if
    /// FactoryGenerator.Initialize()'s direct use of context.CompilationProvider (threaded through
    /// GetInjectionScanScope, and combined in again for the analysis/RegisterSourceOutput stages)
    /// poisons Roslyn's per-stage caching, this should instead cost close to a full cold run.
    /// </summary>
    [Benchmark]
    public int Incremental_UnrelatedEdit() => GeneratorBenchmarkHarness.RunIncremental(m_featureRichIncremental.WarmDriver, m_featureRichIncremental.UnrelatedEditCompilation);

    /// <summary>
    /// Re-runs the warmed driver after an injected constructor's parameters/defaults changed — a
    /// legitimate, relevant edit that should cost something regardless of pipeline architecture.
    /// </summary>
    [Benchmark]
    public int Incremental_InjectedSignatureEdit() => GeneratorBenchmarkHarness.RunIncremental(m_featureRichIncremental.WarmDriver, m_featureRichIncremental.InjectedSignatureEditCompilation);

    /// <summary>
    /// Re-runs the warmed driver after a new <c>[Inject]</c> attribute was added — another
    /// legitimate, relevant edit that should cost something regardless of pipeline architecture.
    /// </summary>
    [Benchmark]
    public int Incremental_AddInjection() => GeneratorBenchmarkHarness.RunIncremental(m_featureRichIncremental.WarmDriver, m_featureRichIncremental.AddInjectCompilation);

    /// <summary>
    /// Re-runs a warmed driver after a *referenced assembly's* source changed (not the current
    /// compilation's own source). Exercises the metadata-symbol scanning path (GetRelevantAssemblies/
    /// GetCandidateTypes over referenced assemblies) rather than the own-compilation discovery path.
    /// </summary>
    [Benchmark]
    public int Incremental_ReferenceAssemblyChange() => GeneratorBenchmarkHarness.RunIncremental(m_referenceAssemblyIncremental.WarmDriver, m_referenceAssemblyIncremental.ChangedCompilation);
}

internal sealed class ColdGeneratorScenario(CSharpCompilation compilation, AnalyzerConfigOptionsProvider optionsProvider)
{
    public CSharpCompilation Compilation { get; } = compilation;
    public AnalyzerConfigOptionsProvider OptionsProvider { get; } = optionsProvider;
}

internal sealed class FeatureRichIncrementalScenario(
    GeneratorDriver warmDriver,
    CSharpCompilation baselineCompilation,
    CSharpCompilation unrelatedEditCompilation,
    CSharpCompilation injectedSignatureEditCompilation,
    CSharpCompilation addInjectCompilation)
{
    public GeneratorDriver WarmDriver { get; } = warmDriver;
    public CSharpCompilation BaselineCompilation { get; } = baselineCompilation;
    public CSharpCompilation UnrelatedEditCompilation { get; } = unrelatedEditCompilation;
    public CSharpCompilation InjectedSignatureEditCompilation { get; } = injectedSignatureEditCompilation;
    public CSharpCompilation AddInjectCompilation { get; } = addInjectCompilation;
}

internal sealed class IncrementalGeneratorScenario(GeneratorDriver warmDriver, CSharpCompilation changedCompilation)
{
    public GeneratorDriver WarmDriver { get; } = warmDriver;
    public CSharpCompilation ChangedCompilation { get; } = changedCompilation;
}

internal static class GeneratorBenchmarkHarness
{
    public static int RunCold(ColdGeneratorScenario scenario)
    {
        var driver = CreateDriver(scenario.Compilation, scenario.OptionsProvider);
        return RunAndSummarize(driver, scenario.Compilation);
    }

    public static int RunIncremental(GeneratorDriver warmDriver, CSharpCompilation compilation)
    {
        return RunAndSummarize(warmDriver, compilation);
    }

    public static GeneratorDriver WarmAndValidate(ColdGeneratorScenario scenario)
    {
        var driver = CreateDriver(scenario.Compilation, scenario.OptionsProvider);
        return RunAndValidate(driver, scenario.Compilation);
    }

    public static void Validate(ColdGeneratorScenario scenario)
    {
        _ = WarmAndValidate(scenario);
    }

    public static void Validate(GeneratorDriver warmDriver, CSharpCompilation compilation)
    {
        _ = RunAndValidate(warmDriver, compilation);
    }

    private static GeneratorDriver CreateDriver(CSharpCompilation compilation, AnalyzerConfigOptionsProvider optionsProvider)
    {
        return CSharpGeneratorDriver.Create(
            [new global::FactoryGenerator.FactoryGenerator().AsSourceGenerator()],
            parseOptions: (CSharpParseOptions) compilation.SyntaxTrees.First().Options,
            optionsProvider: optionsProvider);
    }

    private static GeneratorDriver RunAndValidate(GeneratorDriver driver, CSharpCompilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        var runResult = driver.GetRunResult();
        var exception = runResult.Results
                                 .Select(result => result.Exception)
                                 .FirstOrDefault(resultException => resultException is not null);

        if (exception is not null)
            throw exception;

        var errors = outputCompilation.GetDiagnostics()
                                      .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                                      .Select(diagnostic => diagnostic.ToString())
                                      .ToArray();

        if (errors.Length != 0)
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));

        return driver;
    }

    private static int RunAndSummarize(GeneratorDriver driver, CSharpCompilation compilation)
    {
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        var runResult = driver.GetRunResult();

        return runResult.Results.Sum(result => result.GeneratedSources.Sum(source => source.SourceText.Length));
    }
}

internal static class GeneratorBenchmarkScenarioFactory
{
    private static readonly ImmutableArray<MetadataReference> s_metadataReferences = CreateMetadataReferences();
    private static readonly AnalyzerConfigOptionsProvider s_staticExtensionsEnabledOptions = new BenchmarkAnalyzerConfigOptionsProvider(true);
    private static readonly AnalyzerConfigOptionsProvider s_staticExtensionsDisabledOptions = new BenchmarkAnalyzerConfigOptionsProvider(false);

    public static ColdGeneratorScenario CreateConstructorGraph(int serviceCount)
    {
        var compilation = CreateCompilation(
            "GeneratorConstructorGraphBenchmarks",
            new BenchmarkSourceDocument("ConstructorGraph.cs", BuildConstructorGraphSource("GeneratorConstructorGraphInput", serviceCount)));

        return new ColdGeneratorScenario(compilation, s_staticExtensionsDisabledOptions);
    }

    /// <summary>
    /// Same long linear dependency chain as <see cref="CreateConstructorGraph"/>, but with static
    /// extensions enabled. Exists to directly measure whether
    /// <c>PropagateStaticExtensionRequirements</c>'s fixed-point loop (which can take one iteration
    /// per hop of a dependency chain to converge) scales poorly with chain length, rather than
    /// leaving that as an untested hypothesis.
    /// </summary>
    public static ColdGeneratorScenario CreateConstructorGraphWithStaticExtensions(int serviceCount)
    {
        var compilation = CreateCompilation(
            "GeneratorConstructorGraphStaticExtensionsBenchmarks",
            new BenchmarkSourceDocument("ConstructorGraph.cs", BuildConstructorGraphSource("GeneratorConstructorGraphStaticExtensionsInput", serviceCount)));

        return new ColdGeneratorScenario(compilation, s_staticExtensionsEnabledOptions);
    }

    public static ColdGeneratorScenario CreateNoiseHeavyProject(int serviceCount, int noiseTypeCount)
    {
        var compilation = CreateCompilation(
            "GeneratorNoiseHeavyBenchmarks",
            new BenchmarkSourceDocument("ConstructorGraph.cs", BuildConstructorGraphSource("GeneratorNoiseInput", serviceCount)),
            new BenchmarkSourceDocument("Noise.cs", BuildNoiseSource("GeneratorNoiseInput", noiseTypeCount)));

        return new ColdGeneratorScenario(compilation, s_staticExtensionsDisabledOptions);
    }

    public static ColdGeneratorScenario CreateFeatureRichGraph(bool emitStaticExtensions)
    {
        var compilation = CreateCompilation(
            emitStaticExtensions ? "GeneratorFeatureRichStaticExtensionsBenchmarks" : "GeneratorFeatureRichBenchmarks",
            new BenchmarkSourceDocument(
                "FeatureGraph.cs",
                BuildFeatureRichSource("GeneratorFeatureRichInput", includeAdditionalExternalParameter: false, includeExtraWidgetInjection: false, labelDefault: "default", retryCountDefault: 3)),
            new BenchmarkSourceDocument("Utilities.cs", BuildUtilitySource("GeneratorFeatureRichInput", utilitySuffix: "Baseline")));

        return new ColdGeneratorScenario(compilation, emitStaticExtensions ? s_staticExtensionsEnabledOptions : s_staticExtensionsDisabledOptions);
    }

    public static ColdGeneratorScenario CreateMultiAssemblyOverrideGraph(int baseServiceCount, int overrideCount)
    {
        const string baseAssemblyName = "GeneratorOverrideBase";
        const string derivedAssemblyName = "GeneratorOverrideDerived";

        var baseCompilation = CreateCompilation(
            baseAssemblyName,
            new BenchmarkSourceDocument("BaseServices.cs", BuildOverrideBaseSource(baseAssemblyName, baseServiceCount)));
        var baseReference = EmitReference(baseCompilation);
        var derivedCompilation = CreateCompilation(
            derivedAssemblyName,
            baseReference,
            new BenchmarkSourceDocument("DerivedServices.cs", BuildOverrideDerivedSource(baseAssemblyName, derivedAssemblyName, baseServiceCount, overrideCount)));

        return new ColdGeneratorScenario(derivedCompilation, s_staticExtensionsDisabledOptions);
    }

    /// <summary>
    /// A layered graph of many small assemblies where each layer cumulatively references every
    /// prior layer (fan-in), producing O(assemblyCount^2) reference edges rather than one edge per
    /// assembly. Exists to measure <c>GetRelevantAssemblies</c>'s assembly-reachability BFS cost in
    /// isolation, since <see cref="CreateMultiAssemblyOverrideGraph"/> only involves 2 custom
    /// assemblies and can't show a signal for that specific cost.
    /// </summary>
    public static ColdGeneratorScenario CreateManyAssembliesGraph(int assemblyCount, int typesPerAssembly)
    {
        var priorReferences = ImmutableArray<MetadataReference>.Empty;
        CSharpCompilation compilation = null!;
        for (var i = 0; i < assemblyCount; i++)
        {
            var assemblyName = $"GeneratorManyAssembliesLayer{i}";
            var references = priorReferences.IsDefaultOrEmpty ? s_metadataReferences : s_metadataReferences.AddRange(priorReferences);
            compilation = CreateCompilation(
                assemblyName,
                references,
                new BenchmarkSourceDocument($"Layer{i}.cs", BuildManyAssembliesLayerSource(assemblyName, i, typesPerAssembly)));

            // The final layer doesn't need to be emitted; only earlier layers need a real
            // MetadataReference so later layers can reference them.
            if (i < assemblyCount - 1)
                priorReferences = priorReferences.Add(EmitReference(compilation));
        }

        return new ColdGeneratorScenario(compilation, s_staticExtensionsDisabledOptions);
    }

    public static FeatureRichIncrementalScenario CreateFeatureRichIncrementalScenario()
    {
        const string assemblyName = "GeneratorFeatureRichIncremental";

        var baselineCompilation = CreateCompilation(
            assemblyName,
            new BenchmarkSourceDocument(
                "FeatureGraph.cs", BuildFeatureRichSource(assemblyName, includeAdditionalExternalParameter: false, includeExtraWidgetInjection: false, labelDefault: "default", retryCountDefault: 3)),
            new BenchmarkSourceDocument("Utilities.cs", BuildUtilitySource(assemblyName, utilitySuffix: "Baseline")));
        var unrelatedEditCompilation = CreateCompilation(
            assemblyName,
            new BenchmarkSourceDocument(
                "FeatureGraph.cs", BuildFeatureRichSource(assemblyName, includeAdditionalExternalParameter: false, includeExtraWidgetInjection: false, labelDefault: "default", retryCountDefault: 3)),
            new BenchmarkSourceDocument("Utilities.cs", BuildUtilitySource(assemblyName, utilitySuffix: "Edited")));
        var injectedSignatureEditCompilation = CreateCompilation(
            assemblyName,
            new BenchmarkSourceDocument(
                "FeatureGraph.cs", BuildFeatureRichSource(assemblyName, includeAdditionalExternalParameter: true, includeExtraWidgetInjection: false, labelDefault: "edited", retryCountDefault: 5)),
            new BenchmarkSourceDocument("Utilities.cs", BuildUtilitySource(assemblyName, utilitySuffix: "Baseline")));
        var addInjectCompilation = CreateCompilation(
            assemblyName,
            new BenchmarkSourceDocument(
                "FeatureGraph.cs", BuildFeatureRichSource(assemblyName, includeAdditionalExternalParameter: false, includeExtraWidgetInjection: true, labelDefault: "default", retryCountDefault: 3)),
            new BenchmarkSourceDocument("Utilities.cs", BuildUtilitySource(assemblyName, utilitySuffix: "Baseline")));

        var baselineScenario = new ColdGeneratorScenario(baselineCompilation, s_staticExtensionsEnabledOptions);
        var warmDriver = GeneratorBenchmarkHarness.WarmAndValidate(baselineScenario);
        GeneratorBenchmarkHarness.Validate(GeneratorBenchmarkHarness.WarmAndValidate(baselineScenario), unrelatedEditCompilation);
        GeneratorBenchmarkHarness.Validate(GeneratorBenchmarkHarness.WarmAndValidate(baselineScenario), injectedSignatureEditCompilation);
        GeneratorBenchmarkHarness.Validate(GeneratorBenchmarkHarness.WarmAndValidate(baselineScenario), addInjectCompilation);

        return new FeatureRichIncrementalScenario(
            warmDriver,
            baselineCompilation,
            unrelatedEditCompilation,
            injectedSignatureEditCompilation,
            addInjectCompilation);
    }

    public static IncrementalGeneratorScenario CreateReferenceAssemblyIncrementalScenario()
    {
        const string baseAssemblyName = "GeneratorReferenceBase";
        const string derivedAssemblyName = "GeneratorReferenceDerived";

        var baseCompilation = CreateCompilation(
            baseAssemblyName,
            new BenchmarkSourceDocument("BaseServices.cs", BuildReferenceBaseSource(baseAssemblyName, includeSecondBasePart: false)));
        var changedBaseCompilation = CreateCompilation(
            baseAssemblyName,
            new BenchmarkSourceDocument("BaseServices.cs", BuildReferenceBaseSource(baseAssemblyName, includeSecondBasePart: true)));

        var baselineCompilation = CreateCompilation(
            derivedAssemblyName,
            EmitReference(baseCompilation),
            new BenchmarkSourceDocument("DerivedServices.cs", BuildReferenceDerivedSource(baseAssemblyName, derivedAssemblyName)));
        var changedCompilation = CreateCompilation(
            derivedAssemblyName,
            EmitReference(changedBaseCompilation),
            new BenchmarkSourceDocument("DerivedServices.cs", BuildReferenceDerivedSource(baseAssemblyName, derivedAssemblyName)));

        var baselineScenario = new ColdGeneratorScenario(baselineCompilation, s_staticExtensionsEnabledOptions);
        var warmDriver = GeneratorBenchmarkHarness.WarmAndValidate(baselineScenario);
        GeneratorBenchmarkHarness.Validate(GeneratorBenchmarkHarness.WarmAndValidate(baselineScenario), changedCompilation);

        return new IncrementalGeneratorScenario(warmDriver, changedCompilation);
    }

    private static CSharpCompilation CreateCompilation(string assemblyName, params BenchmarkSourceDocument[] documents)
    {
        return CreateCompilation(assemblyName, s_metadataReferences, documents);
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        MetadataReference additionalReference,
        params BenchmarkSourceDocument[] documents)
    {
        return CreateCompilation(assemblyName, s_metadataReferences.Add(additionalReference), documents);
    }

    private static CSharpCompilation CreateCompilation(
        string assemblyName,
        ImmutableArray<MetadataReference> references,
        params BenchmarkSourceDocument[] documents)
    {
        var syntaxTrees = documents
                          .Select(document => CSharpSyntaxTree.ParseText(
                                      document.Source,
                                      new CSharpParseOptions(LanguageVersion.Preview),
                                      path: document.FileName))
                          .ToArray();

        return CSharpCompilation.Create(
            assemblyName,
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static MetadataReference EmitReference(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        if (!result.Success)
        {
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        }

        return MetadataReference.CreateFromImage(stream.ToArray());
    }

    private static ImmutableArray<MetadataReference> CreateMetadataReferences()
    {
        var excludedAssemblies = new HashSet<string>(StringComparer.Ordinal)
        {
            "Benchmarks",
            "FactoryGenerator",
            "FactoryGenerator.Attributes",
            "FactoryGenerator.Extensions.AspNetCore",
            "FactoryGenerator.Extensions.AspNetCore.Tests",
            "FactoryGenerator.Tests",
            "Inherited",
            "Inheritor",
            "TestWebApp"
        };

        return
        [
            .. ((string?) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
               .Split(Path.PathSeparator)
               .Where(path => !excludedAssemblies.Contains(Path.GetFileNameWithoutExtension(path)))
               .Select(path => (MetadataReference) MetadataReference.CreateFromFile(path)),

            MetadataReference.CreateFromFile(typeof(InjectAttribute).Assembly.Location)
        ];
    }

    private static string BuildConstructorGraphSource(string namespaceName, int serviceCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using FactoryGenerator.Attributes;");
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");

        for (var i = 0; i < serviceCount; i++)
        {
            sb.AppendLine($"public interface IService{i}");
            sb.AppendLine("{");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("[Inject]");
            if (i == 0)
            {
                sb.AppendLine($"public sealed class Service{i} : IService{i}");
                sb.AppendLine("{");
                sb.AppendLine("}");
            }
            else
            {
                sb.AppendLine($"public sealed class Service{i}(IService{i - 1} previous) : IService{i}");
                sb.AppendLine("{");
                sb.AppendLine("}");
            }

            sb.AppendLine();
        }

        sb.AppendLine("[Inject]");
        sb.AppendLine($"public sealed class RootConsumer(IService{serviceCount - 1} root)");
        sb.AppendLine("{");
        sb.AppendLine("}");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string BuildNoiseSource(string namespaceName, int noiseTypeCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine($"namespace {namespaceName}");
        sb.AppendLine("{");

        for (var i = 0; i < noiseTypeCount; i++)
        {
            sb.AppendLine($"public sealed class NoiseType{i}");
            sb.AppendLine("{");
            sb.AppendLine($"    public int Compute(int value) => value + {i};");
            sb.AppendLine($"    public string Name => \"NoiseType{i}\";");
            sb.AppendLine("    public DateTime Timestamp => DateTime.UnixEpoch;");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildFeatureRichSource(
        string namespaceName,
        bool includeAdditionalExternalParameter,
        bool includeExtraWidgetInjection,
        string labelDefault,
        int retryCountDefault)
    {
        var additionalExternalParameter = includeAdditionalExternalParameter ? ", AdditionalExternalDependency additional" : string.Empty;
        var widgetCAttribute = includeExtraWidgetInjection ? "[Inject]\n" : string.Empty;

        return $$"""
                 using System.Collections.Generic;
                 using FactoryGenerator.Attributes;

                 namespace {{namespaceName}}
                 {
                 public sealed class ExternalDependency
                 {
                 }

                 public sealed class AdditionalExternalDependency
                 {
                 }

                 public interface IFlaggedFeature
                 {
                 }

                 [Inject, Boolean("feature_enabled")]
                 public sealed class EnabledFeature : IFlaggedFeature
                 {
                 }

                 [Inject]
                 public sealed class FallbackFeature : IFlaggedFeature
                 {
                 }

                 public interface IWidget
                 {
                 }

                 [Inject]
                 public sealed class WidgetA : IWidget
                 {
                 }

                 [Inject]
                 public sealed class WidgetB : IWidget
                 {
                 }

                 {{widgetCAttribute}}public sealed class WidgetC : IWidget
                 {
                 }

                 public interface IPropertyResult
                 {
                 }

                 public sealed class PropertyResult(IFlaggedFeature feature) : IPropertyResult
                 {
                     public IFlaggedFeature Feature { get; } = feature;
                 }

                 public interface IPropertyFactory
                 {
                     [Inject]
                     IPropertyResult Value { get; }
                 }

                 [Inject]
                 public sealed class PropertyFactory(IFlaggedFeature feature) : IPropertyFactory
                 {
                     public IPropertyResult Value => new PropertyResult(feature);
                 }

                 public interface IMethodResult
                 {
                 }

                 public sealed class MethodResult(
                     IFlaggedFeature feature,
                     IEnumerable<IWidget> widgets,
                     ExternalDependency external,
                     string label,
                     int retryCount) : IMethodResult
                 {
                     public IFlaggedFeature Feature { get; } = feature;
                     public IEnumerable<IWidget> Widgets { get; } = widgets;
                     public ExternalDependency External { get; } = external;
                     public string Label { get; } = label;
                     public int RetryCount { get; } = retryCount;
                 }

                 public interface IFeatureFactory
                 {
                     [Inject]
                     IMethodResult Create(
                         ExternalDependency external{{additionalExternalParameter}},
                         string label = "{{labelDefault}}",
                         int retryCount = {{retryCountDefault}},
                         params IWidget[] widgets);
                 }

                 [Inject]
                 public sealed class FeatureFactory(IFlaggedFeature feature) : IFeatureFactory
                 {
                     public IMethodResult Create(
                         ExternalDependency external{{additionalExternalParameter}},
                         string label = "{{labelDefault}}",
                         int retryCount = {{retryCountDefault}},
                         params IWidget[] widgets)
                     {
                         return new MethodResult(feature, widgets, external, label, retryCount);
                     }
                 }

                 [Inject]
                 public sealed class FeatureGraphConsumer(
                     IMethodResult methodResult,
                     IPropertyResult propertyResult,
                     IEnumerable<IWidget> widgets,
                     IFlaggedFeature feature)
                 {
                     public IMethodResult MethodResult { get; } = methodResult;
                     public IPropertyResult PropertyResult { get; } = propertyResult;
                     public IEnumerable<IWidget> Widgets { get; } = widgets;
                     public IFlaggedFeature Feature { get; } = feature;
                 }
                 }
                 """;
    }

    private static string BuildUtilitySource(string namespaceName, string utilitySuffix)
    {
        return $$"""
                 namespace {{namespaceName}}
                 {
                 public static class UtilityValues
                 {
                     public const string Marker = "{{utilitySuffix}}";

                     public static string Combine(string prefix)
                     {
                         return prefix + Marker;
                     }
                 }
                 }
                 """;
    }

    private static string BuildOverrideBaseSource(string assemblyName, int baseServiceCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using FactoryGenerator.Attributes;");
        sb.AppendLine();
        sb.AppendLine("[assembly: InjectionPriority(9)]");
        sb.AppendLine();
        sb.AppendLine($"namespace {assemblyName}");
        sb.AppendLine("{");
        sb.AppendLine("public interface ISharedService");
        sb.AppendLine("{");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("[Inject]");
        sb.AppendLine("public sealed class BaseSharedService : ISharedService");
        sb.AppendLine("{");
        sb.AppendLine("}");
        sb.AppendLine();

        for (var i = 0; i < baseServiceCount; i++)
        {
            sb.AppendLine($"public interface INode{i}");
            sb.AppendLine("{");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("[Inject]");
            if (i == 0)
            {
                sb.AppendLine($"public sealed class BaseNode{i}(ISharedService sharedService) : INode{i}");
            }
            else
            {
                sb.AppendLine($"public sealed class BaseNode{i}(INode{i - 1} previous) : INode{i}");
            }

            sb.AppendLine("{");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildOverrideDerivedSource(string baseAssemblyName, string derivedAssemblyName, int baseServiceCount, int overrideCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"using {baseAssemblyName};");
        sb.AppendLine("using FactoryGenerator.Attributes;");
        sb.AppendLine();
        sb.AppendLine($"namespace {derivedAssemblyName}");
        sb.AppendLine("{");

        for (var i = 0; i < overrideCount; i++)
        {
            var serviceIndex = i * Math.Max(1, baseServiceCount / overrideCount);
            sb.AppendLine("[Inject]");
            if (serviceIndex == 0)
            {
                sb.AppendLine($"public sealed class DerivedNode{serviceIndex}(ISharedService sharedService) : INode{serviceIndex}");
            }
            else
            {
                sb.AppendLine($"public sealed class DerivedNode{serviceIndex}(INode{serviceIndex - 1} previous) : INode{serviceIndex}");
            }

            sb.AppendLine("{");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("[Inject]");
        sb.AppendLine($"public sealed class DerivedRoot(ISharedService sharedService, INode0 firstNode, INode{baseServiceCount - 1} lastNode)");
        sb.AppendLine("{");
        sb.AppendLine("}");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildManyAssembliesLayerSource(string assemblyName, int layerIndex, int typesPerLayer)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using FactoryGenerator.Attributes;");
        sb.AppendLine();
        sb.AppendLine($"namespace {assemblyName}");
        sb.AppendLine("{");

        for (var i = 0; i < typesPerLayer; i++)
        {
            sb.AppendLine("[Inject]");
            sb.AppendLine($"public sealed class Layer{layerIndex}Service{i}");
            sb.AppendLine("{");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildReferenceBaseSource(string assemblyName, bool includeSecondBasePart)
    {
        var secondPart = includeSecondBasePart
                             ? """

                               [Inject]
                               public sealed class BasePartTwo : IBasePart
                               {
                               }
                               """
                             : string.Empty;

        return $$"""
                 using System.Collections.Generic;
                 using FactoryGenerator.Attributes;

                 namespace {{assemblyName}}
                 {
                 public interface IBasePart
                 {
                 }

                 [Inject]
                 public sealed class BasePartOne : IBasePart
                 {
                 }
                 {{secondPart}}

                 public interface IBaseService
                 {
                 }

                 [Inject]
                 public sealed class BaseService(IEnumerable<IBasePart> parts) : IBaseService
                 {
                     public IEnumerable<IBasePart> Parts { get; } = parts;
                 }
                 }
                 """;
    }

    private static string BuildReferenceDerivedSource(string baseAssemblyName, string derivedAssemblyName)
    {
        return $$"""
                 using System.Collections.Generic;
                 using FactoryGenerator.Attributes;
                 using {{baseAssemblyName}};

                 namespace {{derivedAssemblyName}}
                 {
                 [Inject]
                 public sealed class DerivedPart : IBasePart
                 {
                 }

                 [Inject]
                 public sealed class DerivedConsumer(IBaseService service, IEnumerable<IBasePart> parts)
                 {
                     public IBaseService Service { get; } = service;
                     public IEnumerable<IBasePart> Parts { get; } = parts;
                 }
                 }
                 """;
    }
}

internal sealed class BenchmarkSourceDocument(string fileName, string source)
{
    public string FileName { get; } = fileName;
    public string Source { get; } = source;
}

internal sealed class BenchmarkAnalyzerConfigOptionsProvider(bool emitStaticExtensions) : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions m_globalOptions = new DictionaryAnalyzerConfigOptions(
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["build_property.FactoryGenerator_EmitStaticExtensions"] = emitStaticExtensions ? "true" : "false"
        });

    public override AnalyzerConfigOptions GlobalOptions => m_globalOptions;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => EmptyAnalyzerConfigOptions.Instance;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => EmptyAnalyzerConfigOptions.Instance;
}

internal sealed class DictionaryAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, out string value)
    {
        if (values.TryGetValue(key, out var foundValue))
        {
            value = foundValue;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

internal sealed class EmptyAnalyzerConfigOptions : AnalyzerConfigOptions
{
    public static EmptyAnalyzerConfigOptions Instance { get; } = new();

    public override bool TryGetValue(string key, out string value)
    {
        value = string.Empty;
        return false;
    }
}