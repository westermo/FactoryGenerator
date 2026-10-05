using System;
using Microsoft.CodeAnalysis;

namespace FactoryGenerator
{
    public partial class FactoryGenerator
    {
        private const string DiagnosticCategory = "FactoryGenerator";

        internal static readonly DiagnosticDescriptor MissingLambdaSource = new(
            id: "FG001",
            title: "Missing [Inject]ed source for an injected member",
            messageFormat: "{0}",
            category: DiagnosticCategory,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor NoConstructionMethod = new(
            id: "FG002",
            title: "No usable construction method",
            messageFormat: "{0}",
            category: DiagnosticCategory,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor CyclicDependency = new(
            id: "FG003",
            title: "Cyclic dependency detected",
            messageFormat: "{0}",
            category: DiagnosticCategory,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        internal static readonly DiagnosticDescriptor AmbiguousExternalValues = new(
            id: "FG004",
            title: "Multiple externally provided values of the same type",
            messageFormat: "{0}",
            category: DiagnosticCategory,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true);
    }

    /// <summary>
    /// Raised from deep inside code generation when user input is invalid. Caught at the
    /// <see cref="SourceProductionContext"/> boundary and reported as a compiler diagnostic so the
    /// user gets an actionable error with an id instead of a generic "generator threw" warning.
    /// </summary>
    internal sealed class GeneratorDiagnosticException : Exception
    {
        public GeneratorDiagnosticException(DiagnosticDescriptor descriptor, string message) : base(message)
        {
            Descriptor = descriptor;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location.None, Message);
    }
}
