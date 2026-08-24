using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FactoryGenerator
{
    public class LoggingOptions
    {
        public LogLevel LogLevel { get; set; }
        public string? FileName { get; set; }
    }

    [Generator]
    public partial class FactoryGenerator : IIncrementalGenerator
    {
        private const string ToolName = nameof(FactoryGenerator);
        private const string Version = "2.1.0";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var logProvider = SetupLog(context);
            var scanScopes = context.CompilationProvider.Select(GetInjectionScanScope);
            var rest = scanScopes.SelectMany(FindMethods);
            var attributes = rest.Collect();
            var compilation = context.CompilationProvider;

            // Ordering + interface-indexing is identical work for both consumers below (the
            // dictionary-based container and the static-extensions generator). Computing it once
            // here means it's derived exactly once per compilation instead of once per consumer —
            // see InjectionAnalysis/BuildInjectionAnalysis in FactoryGenerator.InjectionOrdering.cs.
            var analysis = attributes.Combine(compilation)
                .Select(static (pair, token) => BuildInjectionAnalysis(pair.Left, pair.Right, token));

            var combined = analysis.Combine(compilation).Combine(logProvider);
            context.RegisterSourceOutput(combined, MakeAutofacModule);

            var supportsStaticExtensions = context.ParseOptionsProvider.Select(IsAtLeastCSharp14);
            var emitStaticExtensions = context.AnalyzerConfigOptionsProvider.Select(GetEmitStaticExtensions);
            var staticExtensionsEnabled = supportsStaticExtensions.Combine(emitStaticExtensions)
                .Select(static (pair, _) => pair.Left && pair.Right);
            var extensionData = analysis.Combine(compilation).Combine(staticExtensionsEnabled);
            context.RegisterSourceOutput(extensionData, MakeStaticExtensions);
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
                                       ((InjectionAnalysis Analysis, Compilation Compilation) Left, LoggingOptions? log) data)
        {
            var analysis = data.Left.Analysis;
            var compilation = data.Left.Compilation;
            var log = data.log?.FileName == null ? NullLogger.Instance : new Logger(data.log.FileName, data.log.LogLevel);

            foreach (var injection in analysis.Ordered)
                log.Log(LogLevel.Debug, $"Traversing {injection.Name} from {injection.AssemblyName} with priority {injection.AssemblyPriority}");

            GenerateCode(analysis, compilation, log, context);
        }

        private const string ClassName = "DependencyInjectionContainer";
        private const string LifetimeName = "LifetimeScope";
    }
}
