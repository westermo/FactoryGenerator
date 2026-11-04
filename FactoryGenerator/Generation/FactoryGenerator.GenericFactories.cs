using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FactoryGenerator
{
    /// <summary>
    /// Specializes open generic <c>[Inject]</c> factory methods (e.g. <c>ILogger&lt;T&gt; Create&lt;T&gt;(ILoggerFactory)</c>)
    /// into closed registrations for every closed type that injected code actually depends on.
    /// Runs inside the scan step, the only stage with symbol access, so the scan result stays symbol-free.
    /// </summary>
    public partial class FactoryGenerator
    {
        /// <summary>
        /// A symbol found by <see cref="FindMethods"/>. <see cref="Data"/> is null for open generic factory
        /// templates, which only become injections once specialized by <see cref="ExpandGenericFactories"/>.
        /// </summary>
        private readonly struct DiscoveredInjection
        {
            public DiscoveredInjection(ISymbol symbol, InjectionData? data)
            {
                Symbol = symbol;
                Data = data;
            }

            public ISymbol Symbol { get; }
            public InjectionData? Data { get; }
        }

        private static ImmutableArray<InjectionData> ExpandGenericFactories(
            List<DiscoveredInjection> discovered,
            Compilation compilation,
            CancellationToken token)
        {
            var expanded = discovered
                .Where(injection => injection.Data is not null)
                .Select(injection => injection.Data!)
                .ToList();
            var templates = discovered
                .Where(injection => injection.Data is null)
                .Select(injection => (IMethodSymbol)injection.Symbol)
                .Where(template => template.ContainingType.TypeParameters.Length == 0)
                .ToArray();
            if (templates.Length == 0)
                return expanded.ToImmutableArray();

            var registeredTypes = new HashSet<string>(
                expanded.SelectMany(injection => injection.InterfaceFullNames),
                StringComparer.Ordinal);
            var pendingTypes = new Queue<ITypeSymbol>();
            var visitedTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var expandedFactories = new HashSet<string>(StringComparer.Ordinal);

            foreach (var injection in discovered)
            {
                if (injection.Data is not null)
                    AddDependencyTypes(injection.Symbol, pendingTypes, visitedTypes);
            }

            while (pendingTypes.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var serviceType = pendingTypes.Dequeue();
                if (ContainsTypeParameter(serviceType))
                    continue;

                var serviceTypeName = serviceType.ToString();
                if (registeredTypes.Contains(serviceTypeName))
                    continue;

                foreach (var template in templates)
                {
                    if (!TryCloseGenericFactory(template, serviceType, compilation, out var closedMethod))
                        continue;

                    if (!expandedFactories.Add(template.ToDisplayString() + "=>" + serviceTypeName))
                        continue;

                    var closedInjection = Injection.Create(closedMethod, closedMethod.GetAttributes(), token);
                    if (closedInjection is null)
                        continue;

                    expanded.Add(closedInjection);
                    AddDependencyTypes(closedMethod, pendingTypes, visitedTypes);
                }
            }

            return expanded.ToImmutableArray();
        }

        private static void AddDependencyTypes(
            ISymbol symbol,
            Queue<ITypeSymbol> pendingTypes,
            HashSet<ITypeSymbol> visitedTypes)
        {
            // Mirrors the parameters Injection.Create extracts: constructor parameters for types,
            // method parameters for factory methods, nothing for properties.
            IEnumerable<IParameterSymbol> parameters = symbol switch
            {
                INamedTypeSymbol type => type.Constructors.SelectMany(constructor => constructor.Parameters),
                IMethodSymbol method => method.Parameters,
                _ => Enumerable.Empty<IParameterSymbol>()
            };

            foreach (var parameter in parameters)
            {
                var type = parameter.Type;
                if (SymbolUtility.GetCollectionKind(type) != CollectionKind.None)
                {
                    type = type switch
                    {
                        IArrayTypeSymbol array => array.ElementType,
                        INamedTypeSymbol named when named.TypeArguments.Length == 1 => named.TypeArguments[0],
                        _ => type
                    };
                }

                if (visitedTypes.Add(type))
                    pendingTypes.Enqueue(type);
            }
        }

        private static bool ContainsTypeParameter(ITypeSymbol type)
        {
            return type switch
            {
                ITypeParameterSymbol => true,
                IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
                INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
                _ => false
            };
        }

        private static bool TryCloseGenericFactory(
            IMethodSymbol template,
            ITypeSymbol serviceType,
            Compilation compilation,
            out IMethodSymbol closedMethod)
        {
            closedMethod = template;
            if (!template.IsGenericMethod
                || template.TypeParameters.Length == 0
                || template.ReturnType is not INamedTypeSymbol)
                return false;

            var typeArguments = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
            if (!TryUnifyGenericType(template.ReturnType, serviceType, template, typeArguments)
                || template.TypeParameters.Any(parameter => !typeArguments.ContainsKey(parameter)))
                return false;

            var arguments = template.TypeParameters.Select(parameter => typeArguments[parameter]).ToArray();
            if (!SatisfiesGenericConstraints(template, arguments, compilation))
                return false;

            closedMethod = template.Construct(arguments);
            return true;
        }

        private static bool TryUnifyGenericType(
            ITypeSymbol pattern,
            ITypeSymbol actual,
            IMethodSymbol template,
            Dictionary<ITypeParameterSymbol, ITypeSymbol> typeArguments)
        {
            if (pattern is ITypeParameterSymbol typeParameter
                && typeParameter.TypeParameterKind == TypeParameterKind.Method
                && SymbolEqualityComparer.Default.Equals(typeParameter.ContainingSymbol, template))
            {
                if (typeArguments.TryGetValue(typeParameter, out var existing))
                    return SymbolEqualityComparer.Default.Equals(existing, actual);

                typeArguments.Add(typeParameter, actual);
                return true;
            }

            if (pattern is IArrayTypeSymbol patternArray)
                return actual is IArrayTypeSymbol actualArray
                    && patternArray.Rank == actualArray.Rank
                    && TryUnifyGenericType(patternArray.ElementType, actualArray.ElementType, template, typeArguments);

            if (pattern is INamedTypeSymbol patternNamed)
            {
                if (actual is not INamedTypeSymbol actualNamed
                    || !SymbolEqualityComparer.Default.Equals(patternNamed.OriginalDefinition, actualNamed.OriginalDefinition)
                    || patternNamed.TypeArguments.Length != actualNamed.TypeArguments.Length)
                    return false;

                for (var index = 0; index < patternNamed.TypeArguments.Length; index++)
                {
                    if (!TryUnifyGenericType(patternNamed.TypeArguments[index], actualNamed.TypeArguments[index], template, typeArguments))
                        return false;
                }

                return true;
            }

            return SymbolEqualityComparer.Default.Equals(pattern, actual);
        }

        private static bool SatisfiesGenericConstraints(
            IMethodSymbol template,
            ITypeSymbol[] arguments,
            Compilation compilation)
        {
            for (var index = 0; index < template.TypeParameters.Length; index++)
            {
                var parameter = template.TypeParameters[index];
                var argument = arguments[index];

                if (parameter.HasReferenceTypeConstraint && !argument.IsReferenceType)
                    return false;
                if (parameter.HasValueTypeConstraint && !argument.IsValueType)
                    return false;
                if (parameter.HasUnmanagedTypeConstraint && !argument.IsUnmanagedType)
                    return false;
                if (parameter.HasNotNullConstraint && argument.NullableAnnotation == NullableAnnotation.Annotated)
                    return false;
                if (parameter.HasConstructorConstraint
                    && !argument.IsValueType
                    && (argument is not INamedTypeSymbol named
                        || !named.InstanceConstructors.Any(constructor =>
                            constructor.Parameters.Length == 0 && constructor.DeclaredAccessibility == Accessibility.Public)))
                    return false;

                foreach (var constraint in parameter.ConstraintTypes)
                {
                    var closedConstraint = SubstituteMethodTypeParameters(constraint, template, arguments, compilation);
                    if (!compilation.ClassifyConversion(argument, closedConstraint).IsImplicit)
                        return false;
                }
            }

            return true;
        }

        private static ITypeSymbol SubstituteMethodTypeParameters(
            ITypeSymbol type,
            IMethodSymbol template,
            ITypeSymbol[] arguments,
            Compilation compilation)
        {
            if (type is ITypeParameterSymbol typeParameter
                && typeParameter.TypeParameterKind == TypeParameterKind.Method
                && SymbolEqualityComparer.Default.Equals(typeParameter.ContainingSymbol, template))
                return arguments[typeParameter.Ordinal];

            if (type is IArrayTypeSymbol array)
                return compilation.CreateArrayTypeSymbol(
                    SubstituteMethodTypeParameters(array.ElementType, template, arguments, compilation),
                    array.Rank);

            if (type is INamedTypeSymbol named && named.TypeArguments.Length > 0)
            {
                var substitutedArguments = named.TypeArguments
                    .Select(argument => SubstituteMethodTypeParameters(argument, template, arguments, compilation))
                    .ToArray();
                return named.ConstructedFrom.Construct(substitutedArguments);
            }

            return type;
        }

        private static string GetLambdaMethodName(LambdaData lambda)
        {
            return lambda.GenericTypeArguments.Length == 0
                ? lambda.MemberName
                : $"{lambda.MemberName}<{string.Join(", ", lambda.GenericTypeArguments)}>";
        }
    }
}
