using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FactoryGenerator
{
    public class LoggingOptions : IEquatable<LoggingOptions>
    {
        public LogLevel LogLevel { get; set; }
        public string? FileName { get; set; }

        public bool Equals(LoggingOptions? other) =>
            other is not null && LogLevel == other.LogLevel && string.Equals(FileName, other.FileName, StringComparison.Ordinal);

        public override bool Equals(object? obj) => Equals(obj as LoggingOptions);

        public override int GetHashCode() => ((int)LogLevel * 397) ^ (FileName == null ? 0 : StringComparer.Ordinal.GetHashCode(FileName));
    }

    [Generator]
    public partial class FactoryGenerator : IIncrementalGenerator
    {
        private const string ToolName = nameof(FactoryGenerator);
        private static readonly string Version = GetToolVersion();

        internal const string ScanStepName = "Scan";
        internal const string AnalysisStepName = "Analysis";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var logProvider = SetupLog(context);

            // The compilation is only touched in the scan step, which reduces it to an equatable,
            // symbol-free snapshot. Every later step (ordering, indexing, code emission) is keyed
            // on that snapshot, so edits that don't change the set of injections — the common case
            // while typing — are served from the incremental cache instead of regenerating code.
            var scan = context.CompilationProvider
                .Select(static (compilation, token) => ScanCompilation(compilation, token))
                .WithTrackingName(ScanStepName);

            // Ordering + interface-indexing is identical work for both consumers below (the
            // dictionary-based container and the static-extensions generator). Computing it once
            // here means it's derived exactly once per scan result instead of once per consumer —
            // see InjectionAnalysis/BuildInjectionAnalysis in FactoryGenerator.InjectionOrdering.cs.
            var analysis = scan
                .Select(static (result, token) => BuildInjectionAnalysis(result, token))
                .WithTrackingName(AnalysisStepName);

            var combined = analysis.Combine(logProvider);
            context.RegisterSourceOutput(combined, MakeAutofacModule);

            var supportsStaticExtensions = context.ParseOptionsProvider.Select(IsAtLeastCSharp14);
            var emitStaticExtensions = context.AnalyzerConfigOptionsProvider.Select(GetEmitStaticExtensions);
            var staticExtensionsEnabled = supportsStaticExtensions.Combine(emitStaticExtensions)
                .Select(static (pair, _) => pair.Left && pair.Right);
            var extensionData = analysis.Combine(staticExtensionsEnabled);
            context.RegisterSourceOutput(extensionData, MakeStaticExtensions);
        }

        private static string GetToolVersion()
        {
            var assembly = typeof(FactoryGenerator).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational!.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }

            return assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        private IncrementalValueProvider<LoggingOptions?> SetupLog(IncrementalGeneratorInitializationContext context)
        {
            return context.AnalyzerConfigOptionsProvider.Select(LogOptionsProvider);
        }

        private LoggingOptions? LogOptionsProvider(AnalyzerConfigOptionsProvider provider, CancellationToken token)
        {
            if (!provider.GlobalOptions.TryGetValue($"build_property.{nameof(FactoryGenerator)}_FileName", out var fileName)) return default;
            if (!provider.GlobalOptions.TryGetValue($"build_property.{nameof(FactoryGenerator)}_LogLevel", out var logLevel)) return default;
            if (!Enum.TryParse<LogLevel>(logLevel, out var level)) return default;
            return new LoggingOptions
            {
                FileName = fileName,
                LogLevel = level
            };
        }

        private void MakeAutofacModule(SourceProductionContext context,
                                       (InjectionAnalysis Analysis, LoggingOptions? Log) data)
        {
            var analysis = data.Analysis;
            var log = data.Log?.FileName == null ? NullLogger.Instance : new Logger(data.Log.FileName, data.Log.LogLevel);

            foreach (var injection in analysis.Ordered)
                log.Log(LogLevel.Debug, $"Traversing {injection.Name} from {injection.AssemblyName} with priority {injection.AssemblyPriority}");

            try
            {
                GenerateCode(analysis, analysis.AssemblyName, log, context);
            }
            catch (GeneratorDiagnosticException e)
            {
                context.ReportDiagnostic(e.ToDiagnostic());
            }
        }

        private const string ClassName = "DependencyInjectionContainer";
        private const string LifetimeName = "LifetimeScope";
    }
}