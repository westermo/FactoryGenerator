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
    /// <summary>
    /// Resolves how an injection is constructed: picks the best available constructor (or lambda
    /// member) given the interfaces available for injection, and builds the resulting creation
    /// expression plus any parameters that must be supplied externally.
    /// </summary>
    public partial class FactoryGenerator
    {
        private static string Declaration(InjectionData injection, string creation)
        {
            var name = injection.Name;
            var lazyName = injection.LazyFieldName;

            if (injection.Singleton || injection.Scoped)
                return SymbolUtility.SingletonFactory(injection.TypeFullName, name, lazyName, creation, injection.Disposable || injection.AsyncDisposable, forwardToOwner: injection.Singleton);

            if (injection.Disposable || injection.AsyncDisposable)
                return SymbolUtility.DisposableFactory(injection.TypeFullName, name, creation);

            return $"internal {injection.TypeFullName} {name} => {creation};";
        }

        /// <summary>
        /// The outcome of resolving how an injection is constructed: the expression used to create
        /// it, and any parameters it could not satisfy from the available interfaces (destined to
        /// become externally-supplied constructor parameters of the generated container).
        /// </summary>
        private readonly struct InjectionResolution
        {
            public InjectionResolution(string creation, IEnumerable<ParameterData> missingParameters)
            {
                Creation = creation;
                MissingParameters = missingParameters;
            }

            public string Creation { get; }
            public IEnumerable<ParameterData> MissingParameters { get; }
        }

        /// <summary>
        /// Resolves the creation expression and missing parameters for an injection in a single pass.
        /// This is computed once per injection and reused for both the container and lifetime-scope
        /// declarations (and for constructor-parameter discovery), avoiding repeated constructor
        /// selection/analysis for the same injection.
        /// </summary>
        private static InjectionResolution ResolveInjection(InjectionData injection, HashSet<string> availableInterfaceFullNames)
        {
            if (injection.Lambda is LambdaData lambda)
            {
                if (!availableInterfaceFullNames.Contains(lambda.ContainingTypeFullName))
                    throw new Exception(
                        $"Could not find any [Inject]ed implementations of {lambda.ContainingTypeFullName} to use as the source for the injection of {lambda.ContainingTypeFullName}.{lambda.MemberName}. Please provide at least one injection of the type {lambda.ContainingTypeFullName}.");

                if (!lambda.IsMethod)
                    return new InjectionResolution($"{lambda.ContainingTypeMemberName}.{lambda.MemberName}", Enumerable.Empty<ParameterData>());

                HashSet<ParameterData>? lambdaMissing = null;
                HashSet<ParameterData>? lambdaNullableDefaults = null;
                AnalyzeParameters(lambda.MethodParameters, availableInterfaceFullNames, ref lambdaMissing, ref lambdaNullableDefaults);
                var lambdaCreation = $"{lambda.ContainingTypeMemberName}.{lambda.MemberName}{MakeMethodCall(lambda.MethodParameters, lambdaMissing, lambdaNullableDefaults)}";
                return new InjectionResolution(lambdaCreation, (IEnumerable<ParameterData>?) lambdaMissing ?? Enumerable.Empty<ParameterData>());
            }

            HashSet<ParameterData>? missing = null;
            HashSet<ParameterData>? nullableDefaults = null;
            var ctor = GetBestConstructor(injection, availableInterfaceFullNames, ref missing, ref nullableDefaults);
            if (ctor is null)
                throw new Exception($"No Construction method for {injection.TypeFullName}. Lambda was null.");

            var creation = $"new {injection.TypeFullName}{MakeConstructorCall(ctor, missing, nullableDefaults)}";
            return new InjectionResolution(creation, (IEnumerable<ParameterData>?) missing ?? Enumerable.Empty<ParameterData>());
        }

        private static ConstructorData? GetBestConstructor(InjectionData injection,
            HashSet<string> availableInterfaceFullNames, ref HashSet<ParameterData>? missing,
            ref HashSet<ParameterData>? nullableDefaults)
        {
            missing = null;
            nullableDefaults = null;
            ConstructorData? chosen = null;
            foreach (var ctor in injection.Constructors)
            {
                HashSet<ParameterData>? localMissing = null;
                HashSet<ParameterData>? localNullableDefaults = null;
                AnalyzeParameters(ctor.Parameters, availableInterfaceFullNames, ref localMissing, ref localNullableDefaults, out var valid);

                if (valid)
                {
                    chosen = ctor;
                    missing = localMissing;
                    nullableDefaults = localNullableDefaults;
                    break;
                }

                if ((missing?.Count ?? int.MaxValue) <= (localMissing?.Count ?? 0)) continue;
                chosen = ctor;
                missing = localMissing;
                nullableDefaults = localNullableDefaults;
            }
            return chosen;
        }

        private static void AnalyzeParameters(
            ImmutableArray<ParameterData> parameters,
            HashSet<string> availableInterfaceFullNames,
            ref HashSet<ParameterData>? missing,
            ref HashSet<ParameterData>? nullableDefaults)
        {
            AnalyzeParameters(parameters, availableInterfaceFullNames, ref missing, ref nullableDefaults, out _);
        }

        private static void AnalyzeParameters(
            ImmutableArray<ParameterData> parameters,
            HashSet<string> availableInterfaceFullNames,
            ref HashSet<ParameterData>? missing,
            ref HashSet<ParameterData>? nullableDefaults,
            out bool valid)
        {
            var localMissing = new HashSet<ParameterData>();
            var localNullableDefaults = new HashSet<ParameterData>();
            valid = true;

            foreach (var parameter in parameters)
            {
                var typeLookup = parameter.IsNullable
                    ? parameter.TypeFullName.TrimEnd('?')
                    : parameter.TypeFullName;

                if (parameter.IsCollection)
                {
                    localMissing.Add(parameter);
                    continue;
                }

                if (availableInterfaceFullNames.Contains(typeLookup))
                    continue;

                if (parameter.HasExplicitDefault || parameter.IsParams)
                    continue;

                if (parameter.IsNullable)
                {
                    localNullableDefaults.Add(parameter);
                    continue;
                }

                valid = false;
                localMissing.Add(parameter);
            }

            missing = localMissing.Count > 0 ? localMissing : null;
            nullableDefaults = localNullableDefaults.Count > 0 ? localNullableDefaults : null;
        }

        private static string MakeConstructorCall(ConstructorData ctor, HashSet<ParameterData>? missing, HashSet<ParameterData>? nullableDefaults)
        {
            return MakeInvocationCall(ctor.Parameters, missing, nullableDefaults);
        }

        private static string MakeMethodCall(ImmutableArray<ParameterData> parameters, HashSet<ParameterData>? missing, HashSet<ParameterData>? nullableDefaults = null)
        {
            return MakeInvocationCall(parameters, missing, nullableDefaults);
        }

        private static string MakeInvocationCall(
            ImmutableArray<ParameterData> parameters,
            HashSet<ParameterData>? missing,
            HashSet<ParameterData>? nullableDefaults)
        {
            var args = new List<string>();
            var useNamedArguments = false;
            foreach (var parameter in parameters)
            {
                if (nullableDefaults?.Contains(parameter) == true)
                {
                    args.Add(useNamedArguments ? $"{parameter.Name}: null" : "null");
                    continue;
                }
                if (missing?.Contains(parameter) == true)
                {
                    var argument = CollectionConstructorArg(parameter);
                    args.Add(useNamedArguments ? $"{parameter.Name}: {argument}" : argument);
                    continue;
                }

                if (parameter.HasExplicitDefault || parameter.IsParams)
                {
                    useNamedArguments = true;
                    continue;
                }

                var resolvedArgument = parameter.TypeMemberName + "()";
                args.Add(useNamedArguments ? $"{parameter.Name}: {resolvedArgument}" : resolvedArgument);
            }
            return $"({string.Join(", ", args)})";
        }

        /// <summary>
        /// Returns the expression to use when passing a collection (or plain-missing) parameter
        /// in a generated constructor call. Collection params are converted from the cached
        /// IEnumerable&lt;T&gt; factory to the exact type requested.
        /// </summary>
        private static string CollectionConstructorArg(ParameterData parameter)
        {
            if (!parameter.IsCollection)
                return parameter.Name;
            var memberName = "coll_" + parameter.CollectionElementMemberName!;
            return parameter.CollectionKind switch
            {
                CollectionKind.Array        => $"{memberName}.ToArray()",
                CollectionKind.List         => $"{memberName}.ToList()",
                CollectionKind.ImmutableArray => $"ImmutableArray.CreateRange({memberName})",
                CollectionKind.ReadOnlySpan => $"new global::System.ReadOnlySpan<{parameter.CollectionElementFullName}>({memberName}.ToArray())",
                _                           => memberName, // Enumerable → use element member name directly
            };
        }

        /// <summary>
        /// Returns the expression used in the Func&lt;object&gt; lambda inside the lookup dictionary
        /// for a localized (collection) parameter.
        /// </summary>
        private static string CollectionDictExpression(CollectionKind kind, string factoryName) =>
            kind switch
            {
                CollectionKind.Array          => $"{factoryName}.ToArray()",
                CollectionKind.List           => $"{factoryName}.ToList()",
                CollectionKind.ImmutableArray => $"ImmutableArray.CreateRange({factoryName})",
                _                             => factoryName, // Enumerable → direct
            };
    }
}
