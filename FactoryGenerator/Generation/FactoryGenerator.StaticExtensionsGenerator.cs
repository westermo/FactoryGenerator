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
    /// Generates the C# 14+ static-extension resolve API (<c>T.Resolve(container)</c>), an
    /// alternative to the dictionary-based container that inlines the full construction chain
    /// directly at each call site, avoiding dictionary lookups and factory-delegate indirection.
    /// </summary>
    public partial class FactoryGenerator
    {
        private static bool IsAtLeastCSharp14(ParseOptions options, CancellationToken _)
        {
            if (options is not CSharpParseOptions csOptions) return false;
            // C# 14 = 1400 in Roslyn's LanguageVersion enum.
            // LanguageVersion.Preview == int.MaxValue, which is also >= 1400.
            const int CSharp14 = 1400;
            return (int) csOptions.LanguageVersion >= CSharp14;
        }

        private static bool GetEmitStaticExtensions(AnalyzerConfigOptionsProvider provider, CancellationToken _)
        {
            if (!provider.GlobalOptions.TryGetValue($"build_property.{nameof(FactoryGenerator)}_EmitStaticExtensions", out var value))
                return true;
            return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class StaticExtensionSpec
        {
            public StaticExtensionSpec(string typeFullName, string typeMemberName, string extensionClassName, List<InjectionData> possibilities)
            {
                TypeFullName = typeFullName;
                TypeMemberName = typeMemberName;
                ExtensionClassName = extensionClassName;
                Possibilities = possibilities;
            }

            public string TypeFullName { get; }
            public string TypeMemberName { get; }
            public string ExtensionClassName { get; }
            public List<InjectionData> Possibilities { get; }
            public List<string> BooleanKeys { get; } = new List<string>();
            public List<ParameterData> ExternalParameters { get; } = new List<ParameterData>();
            public List<StaticDependencyReference> Dependencies { get; } = new List<StaticDependencyReference>();
        }

        private sealed class StaticDependencyReference
        {
            public StaticDependencyReference(string typeFullName, bool resolveAll)
            {
                TypeFullName = typeFullName;
                ResolveAll = resolveAll;
            }

            public string TypeFullName { get; }
            public bool ResolveAll { get; }
        }

        private static void MakeStaticExtensions(
            SourceProductionContext context,
            ((ImmutableArray<InjectionData> Injections, Compilation Compilation) Left, bool SupportsExtensions) data)
        {
            if (!data.SupportsExtensions) return;
            GenerateStaticExtensions(data.Left.Injections, data.Left.Compilation, context);
        }

        private static void GenerateStaticExtensions(ImmutableArray<InjectionData> dataInjections, Compilation compilation, SourceProductionContext context)
        {
            var ordered = OrderInjections(dataInjections, compilation);
            var (interfaceInjectors, interfaceMemberNames) = BuildInterfaceInjectors(ordered);
            var availableInterfaces = new HashSet<string>(interfaceInjectors.Keys);
            var specs = BuildStaticExtensionSpecs(interfaceInjectors, interfaceMemberNames, availableInterfaces);

            var reservedNames = BuildStaticExtensionReservedNames(specs, interfaceMemberNames);
            var externalIdentifiers = BuildExternalParameterIdentifiers(specs.Values.SelectMany(spec => spec.ExternalParameters), reservedNames);
            var booleanIdentifiers = BuildBooleanParameterIdentifiers(
                specs.Values.SelectMany(spec => spec.BooleanKeys).Distinct(),
                reservedNames.Concat(externalIdentifiers.Values));

            var usings = $"""
                          using System;
                          using System.CodeDom.Compiler;
                          using System.Collections.Generic;
                          using System.Collections.Immutable;
                          using System.Linq;
                          namespace {compilation.Assembly.Name}.Generated;
                          #nullable enable
                          """;
            var state = $$"""
                          {{usings}}
                          [GeneratedCode("{{ToolName}}", "{{Version}}")]
                          internal sealed class StaticResolveState
                          {
                              private readonly HashSet<string> m_activeCollections = new(StringComparer.Ordinal);

                              public bool EnterCollection(string key)
                              {
                                  return m_activeCollections.Add(key);
                              }

                              public void ExitCollection(string key)
                              {
                                  m_activeCollections.Remove(key);
                              }
                          }
                          """;
            context.AddSource("FactoryGenerator.StaticExtensions/StaticResolveState.g.cs", state);

            foreach (var ifaceFull in interfaceMemberNames.Keys)
            {
                var spec = specs[ifaceFull];
                var helpers = BuildStaticExtensionClass(spec, specs, availableInterfaces, booleanIdentifiers, externalIdentifiers);

                context.AddSource($"FactoryGenerator.StaticExtensions/{spec.ExtensionClassName}.g.cs",
                                  $$"""
                                    {{usings}}
                                    [GeneratedCode("{{ToolName}}", "{{Version}}")]
                                    public static class {{spec.ExtensionClassName}}
                                    {
                                        {{helpers}}
                                        extension({{spec.TypeFullName}})
                                        {
                                            {{BuildStaticPublicResolveMethods(spec, booleanIdentifiers, externalIdentifiers)}}
                                        }
                                    }
                                    """);
            }
        }

        private static Dictionary<string, StaticExtensionSpec> BuildStaticExtensionSpecs(
            Dictionary<string, List<InjectionData>> interfaceInjectors,
            Dictionary<string, string> interfaceMemberNames,
            HashSet<string> availableInterfaces)
        {
            var specs = interfaceInjectors.ToDictionary(
                pair => pair.Key,
                pair => CreateDirectStaticExtensionSpec(pair.Key, pair.Value, interfaceMemberNames[pair.Key], availableInterfaces, interfaceInjectors),
                StringComparer.Ordinal);

            PropagateStaticExtensionRequirements(specs);

            foreach (var spec in specs.Values)
            {
                var orderedBooleanKeys = spec.BooleanKeys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
                spec.BooleanKeys.Clear();
                spec.BooleanKeys.AddRange(orderedBooleanKeys);

                var orderedExternalParameters = spec.ExternalParameters
                                                    .OrderBy(parameter => parameter.TypeFullName, StringComparer.Ordinal)
                                                    .ToArray();
                spec.ExternalParameters.Clear();
                spec.ExternalParameters.AddRange(orderedExternalParameters);
            }

            return specs;
        }

        private static StaticExtensionSpec CreateDirectStaticExtensionSpec(
            string typeFullName,
            List<InjectionData> possibilities,
            string typeMemberName,
            HashSet<string> availableInterfaces,
            Dictionary<string, List<InjectionData>> interfaceInjectors)
        {
            var spec = new StaticExtensionSpec(typeFullName, typeMemberName, typeMemberName + "Extensions", possibilities);
            PopulateStaticExtensionDirectRequirements(spec, availableInterfaces, interfaceInjectors);
            return spec;
        }

        private static void PopulateStaticExtensionDirectRequirements(
            StaticExtensionSpec spec,
            HashSet<string> availableInterfaces,
            Dictionary<string, List<InjectionData>> interfaceInjectors)
        {
            foreach (var booleanKey in spec.Possibilities.Select(possibility => possibility.BooleanInjection?.Key).OfType<string>())
                AddDistinctString(spec.BooleanKeys, booleanKey);

            foreach (var possibility in spec.Possibilities)
            {
                if (possibility.Lambda is { } lambda)
                {
                    if (interfaceInjectors.ContainsKey(lambda.ContainingTypeFullName))
                        AddDistinctDependency(spec.Dependencies, new StaticDependencyReference(lambda.ContainingTypeFullName, false));

                    foreach (var parameter in lambda.MethodParameters)
                        AddStaticParameterRequirement(spec, parameter, interfaceInjectors);

                    continue;
                }

                HashSet<ParameterData>? missing = null;
                HashSet<ParameterData>? nullableDefaults = null;
                var constructor = GetBestConstructor(possibility, availableInterfaces, ref missing, ref nullableDefaults);
                if (constructor is null)
                    continue;

                foreach (var parameter in constructor.Parameters)
                    AddStaticParameterRequirement(spec, parameter, interfaceInjectors);
            }
        }

        private static void AddStaticParameterRequirement(
            StaticExtensionSpec spec,
            ParameterData parameter,
            Dictionary<string, List<InjectionData>> interfaceInjectors)
        {
            if (parameter.IsCollection)
            {
                if (parameter.CollectionElementFullName is not null && interfaceInjectors.ContainsKey(parameter.CollectionElementFullName))
                    AddDistinctDependency(spec.Dependencies, new StaticDependencyReference(parameter.CollectionElementFullName, true));
                return;
            }

            var typeLookup = parameter.IsNullable
                                 ? parameter.TypeFullName.TrimEnd('?')
                                 : parameter.TypeFullName;

            if (interfaceInjectors.ContainsKey(typeLookup))
            {
                AddDistinctDependency(spec.Dependencies, new StaticDependencyReference(typeLookup, false));
                return;
            }

            if (parameter.HasExplicitDefault || parameter.IsParams || parameter.IsNullable)
                return;

            AddDistinctParameter(spec.ExternalParameters, parameter);
        }

        private static void PropagateStaticExtensionRequirements(IReadOnlyDictionary<string, StaticExtensionSpec> specs)
        {
            bool changed;
            do
            {
                changed = false;

                foreach (var spec in specs.Values)
                {
                    foreach (var dependency in spec.Dependencies)
                    {
                        if (!specs.TryGetValue(dependency.TypeFullName, out var dependencySpec))
                            continue;

                        foreach (var booleanKey in dependencySpec.BooleanKeys)
                            changed |= AddDistinctString(spec.BooleanKeys, booleanKey);

                        foreach (var externalParameter in dependencySpec.ExternalParameters)
                            changed |= AddDistinctParameter(spec.ExternalParameters, externalParameter);
                    }
                }
            } while (changed);
        }

        private static bool AddDistinctString(List<string> values, string value)
        {
            if (values.Contains(value))
                return false;

            values.Add(value);
            return true;
        }

        private static bool AddDistinctParameter(List<ParameterData> values, ParameterData value)
        {
            if (values.Any(parameter => parameter.TypeFullName == value.TypeFullName))
                return false;

            values.Add(value);
            return true;
        }

        private static bool AddDistinctDependency(List<StaticDependencyReference> values, StaticDependencyReference value)
        {
            if (values.Any(existing => existing.TypeFullName == value.TypeFullName && existing.ResolveAll == value.ResolveAll))
                return false;

            values.Add(value);
            return true;
        }

        private static IEnumerable<string> BuildStaticExtensionReservedNames(
            IReadOnlyDictionary<string, StaticExtensionSpec> specs,
            IReadOnlyDictionary<string, string> interfaceMemberNames)
        {
            return interfaceMemberNames.Values
                                       .Concat(specs.Values.Select(spec => spec.ExtensionClassName))
                                       .Concat(specs.Values.SelectMany(spec => spec.Possibilities.Select(GetStaticInjectionHelperName)))
                                       .Concat(new[]
                                       {
                                           "container",
                                           "state",
                                           "cached",
                                           "value",
                                           "source",
                                           "additional",
                                           "disposable",
                                           "key",
                                           "b",
                                           "Resolve",
                                           "ResolveCore",
                                           "ResolveAllCore",
                                           "StaticResolveState",
                                           "EnterCollection",
                                           "ExitCollection"
                                       });
        }

        private static string BuildStaticExtensionClass(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, StaticExtensionSpec> specs,
            HashSet<string> availableInterfaces,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var parts = new List<string>
            {
                BuildStaticResolveCoreMethod(spec, booleanIdentifiers, externalIdentifiers),
                BuildStaticResolveAllCoreMethod(spec, booleanIdentifiers, externalIdentifiers)
            };

            foreach (var possibility in spec.Possibilities)
            {
                parts.Add(BuildStaticResolveInjectionMethod(spec, possibility, booleanIdentifiers, externalIdentifiers));
                parts.Add(BuildStaticCreateInjectionMethod(spec, possibility, specs, availableInterfaces, booleanIdentifiers, externalIdentifiers));
            }

            return string.Join("\n\n", parts);
        }

        private static string BuildStaticPublicResolveMethods(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var runtimeDeclarations = BuildStaticRuntimeArgumentDeclarations(spec, booleanIdentifiers, externalIdentifiers);
            var runtimeArguments = BuildStaticRuntimeArgumentValues(spec, booleanIdentifiers, externalIdentifiers);
            var containerSignature = string.IsNullOrEmpty(runtimeDeclarations)
                                         ? $"{ClassName}? container"
                                         : $"{ClassName}? container, {runtimeDeclarations}";
            var containerInvocation = string.IsNullOrEmpty(runtimeArguments)
                                          ? "container, new StaticResolveState()"
                                          : $"container, new StaticResolveState(), {runtimeArguments}";

            var methods = new List<string>
            {
                $@"        public static {spec.TypeFullName} Resolve({containerSignature})
        {{
            return ResolveCore({containerInvocation});
        }}"
            };

            if (!string.IsNullOrEmpty(runtimeDeclarations))
            {
                var nullInvocation = $"null, new StaticResolveState(), {runtimeArguments}";
                methods.Add($@"        public static {spec.TypeFullName} Resolve({runtimeDeclarations})
        {{
            return ResolveCore({nullInvocation});
        }}");
            }

            return string.Join("\n\n", methods);
        }

        private static string BuildStaticResolveCoreMethod(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var parameterList = BuildStaticMethodParameterList(spec, booleanIdentifiers, externalIdentifiers, includeContainer: true, includeState: true);
            var resolveExpression = BuildStaticResolveSelectionExpression(spec, booleanIdentifiers, externalIdentifiers);

            return $@"    internal static {spec.TypeFullName} ResolveCore({parameterList})
    {{
        return {resolveExpression};
    }}";
        }

        private static string BuildStaticResolveAllCoreMethod(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var parameterList = BuildStaticMethodParameterList(spec, booleanIdentifiers, externalIdentifiers, includeContainer: true, includeState: true);
            var resolveInvocations = spec.Possibilities
                                         .Where(possibility => possibility.BooleanInjection == null)
                                         .Select(possibility => BuildStaticResolveInjectionInvocation(spec, possibility, booleanIdentifiers, externalIdentifiers))
                                         .ToArray();
            var conditionalInvocations = spec.Possibilities
                                             .Where(possibility => possibility.BooleanInjection is not null)
                                             .Select(possibility =>
                                                         $"            if ({booleanIdentifiers[possibility.BooleanInjection!.Key]}) source.Add({BuildStaticResolveInjectionInvocation(spec, possibility, booleanIdentifiers, externalIdentifiers)});")
                                             .ToArray();

            return $@"    internal static IEnumerable<{spec.TypeFullName}> ResolveAllCore({parameterList})
    {{
        if (!state.EnterCollection(""{spec.TypeFullName}""))
            return Array.Empty<{spec.TypeFullName}>();

        try
        {{
            var source = new List<{spec.TypeFullName}>({resolveInvocations.Length}) {{
                {string.Join(",\n                ", resolveInvocations)}
            }};
{string.Join("\n", conditionalInvocations)}
            if (container is not null)
            {{
                var b = container.Base;
                while (b is not null)
                {{
                    if (b.TryResolve<IEnumerable<{spec.TypeFullName}>>(out var additional))
                        source.AddRange(additional!);
                    b = b.Base;
                }}

                b = container.Inheritor;
                while (b is not null)
                {{
                    if (b.TryResolve<IEnumerable<{spec.TypeFullName}>>(out var additional))
                        source.AddRange(additional!);
                    b = b.Inheritor;
                }}
            }}

            return source;
        }}
        finally
        {{
            state.ExitCollection(""{spec.TypeFullName}"");
        }}
    }}";
        }

        private static string BuildStaticResolveInjectionMethod(
            StaticExtensionSpec spec,
            InjectionData injection,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var helperName = GetStaticInjectionHelperName(injection);
            var parameterList = BuildStaticMethodParameterList(spec, booleanIdentifiers, externalIdentifiers, includeContainer: true, includeState: true);
            var createInvocation = BuildStaticCreateInjectionInvocation(spec, injection, booleanIdentifiers, externalIdentifiers);
            var tracksResolvedInstance = injection.Disposable || injection.AsyncDisposable;

            if (injection.Singleton || injection.Scoped)
            {
                if (tracksResolvedInstance)
                {
                    return $@"    private static {injection.TypeFullName} Resolve_{helperName}({parameterList})
    {{
        if (container is not null)
        {{
            var cached = container.{injection.LazyFieldName};
            if (cached is not null)
                return cached;

            lock (container.m_lock)
            {{
                cached = container.{injection.LazyFieldName};
                if (cached is not null)
                    return cached;

                var value = {createInvocation};
                container.TrackResolvedInstance(value);
                container.{injection.LazyFieldName} = value;
                return value;
            }}
        }}

        return Create_{helperName}({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "null", "state")});
    }}";
                }

                return $@"    private static {injection.TypeFullName} Resolve_{helperName}({parameterList})
    {{
        if (container is not null)
        {{
            var cached = container.{injection.LazyFieldName};
            if (cached is not null)
                return cached;

            lock (container.m_lock)
            {{
                cached = container.{injection.LazyFieldName};
                if (cached is not null)
                    return cached;

                return container.{injection.LazyFieldName} = {createInvocation};
            }}
        }}

        return Create_{helperName}({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "null", "state")});
    }}";
            }

            if (tracksResolvedInstance)
            {
                return $@"    private static {injection.TypeFullName} Resolve_{helperName}({parameterList})
    {{
        if (container is not null)
        {{
            var value = {createInvocation};
            container.TrackResolvedInstance(value);
            return value;
        }}

        return Create_{helperName}({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "null", "state")});
    }}";
            }

            return $@"    private static {injection.TypeFullName} Resolve_{helperName}({parameterList})
    {{
        return {createInvocation};
    }}";
        }

        private static string BuildStaticCreateInjectionMethod(
            StaticExtensionSpec spec,
            InjectionData injection,
            IReadOnlyDictionary<string, StaticExtensionSpec> specs,
            HashSet<string> availableInterfaces,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var helperName = GetStaticInjectionHelperName(injection);
            var parameterList = BuildStaticMethodParameterList(spec, booleanIdentifiers, externalIdentifiers, includeContainer: true, includeState: true);
            var createExpression = BuildStaticCreateExpression(injection, specs, availableInterfaces, booleanIdentifiers, externalIdentifiers);
            var returnStatement = createExpression.StartsWith("throw ", StringComparison.Ordinal)
                                      ? createExpression + ";"
                                      : "return " + createExpression + ";";

            return $@"    private static {injection.TypeFullName} Create_{helperName}({parameterList})
    {{
        {returnStatement}
    }}";
        }

        private static string BuildStaticCreateExpression(
            InjectionData injection,
            IReadOnlyDictionary<string, StaticExtensionSpec> specs,
            HashSet<string> availableInterfaces,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            if (injection.Lambda is LambdaData lambda)
            {
                if (!specs.TryGetValue(lambda.ContainingTypeFullName, out var containingSpec))
                    return BuildMissingImplementationExpression(lambda.ContainingTypeFullName);

                var containingInvocation = BuildStaticResolveInvocation(containingSpec, "ResolveCore", booleanIdentifiers, externalIdentifiers);
                if (!lambda.IsMethod)
                    return $"{containingInvocation}.{lambda.MemberName}";

                var lambdaArguments = BuildStaticArgumentList(lambda.MethodParameters, specs, booleanIdentifiers, externalIdentifiers);
                return $"{containingInvocation}.{lambda.MemberName}({lambdaArguments})";
            }

            HashSet<ParameterData>? missing = null;
            HashSet<ParameterData>? nullableDefaults = null;
            var constructor = GetBestConstructor(injection, availableInterfaces, ref missing, ref nullableDefaults);
            if (constructor is null)
                return BuildMissingImplementationExpression(injection.TypeFullName);

            var constructorArguments = BuildStaticArgumentList(constructor.Parameters, specs, booleanIdentifiers, externalIdentifiers);
            return $"new {injection.TypeFullName}({constructorArguments})";
        }

        private static string BuildStaticArgumentList(
            ImmutableArray<ParameterData> parameters,
            IReadOnlyDictionary<string, StaticExtensionSpec> specs,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var arguments = new List<string>();
            var useNamedArguments = false;

            foreach (var parameter in parameters)
            {
                var argumentExpression = BuildStaticArgumentExpression(parameter, specs, booleanIdentifiers, externalIdentifiers);
                if (argumentExpression is null)
                {
                    useNamedArguments = true;
                    continue;
                }

                arguments.Add(useNamedArguments
                                  ? $"{parameter.Name}: {argumentExpression}"
                                  : argumentExpression);
            }

            return string.Join(", ", arguments);
        }

        private static string? BuildStaticArgumentExpression(
            ParameterData parameter,
            IReadOnlyDictionary<string, StaticExtensionSpec> specs,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            if (parameter.IsCollection && parameter.CollectionElementFullName is not null)
            {
                if (!specs.TryGetValue(parameter.CollectionElementFullName, out var collectionSpec))
                {
                    var resolvedCollectionName = "resolvedCollection_" + parameter.Name;
                    var fallbackCollection = $@"container != null && container.TryResolve<IEnumerable<{parameter.CollectionElementFullName}>>(out var {resolvedCollectionName})
                    ? {resolvedCollectionName}!
                    : global::System.Array.Empty<{parameter.CollectionElementFullName}>()";
                    return BuildStaticCollectionConversion(parameter, fallbackCollection);
                }

                return BuildStaticCollectionConversion(
                    parameter,
                    BuildStaticResolveAllInvocation(collectionSpec, booleanIdentifiers, externalIdentifiers));
            }

            var typeLookup = parameter.IsNullable
                                 ? parameter.TypeFullName.TrimEnd('?')
                                 : parameter.TypeFullName;

            if (specs.TryGetValue(typeLookup, out var dependencySpec))
                return BuildStaticResolveInvocation(dependencySpec, "ResolveCore", booleanIdentifiers, externalIdentifiers);

            if (parameter.HasExplicitDefault || parameter.IsParams)
                return null;

            if (parameter.IsNullable)
                return "null";

            if (externalIdentifiers.TryGetValue(parameter.TypeFullName, out var identifier))
                return identifier;

            return BuildMissingImplementationExpression(parameter.TypeFullName);
        }

        private static string BuildStaticCollectionConversion(ParameterData parameter, string sourceExpression)
        {
            if (!parameter.IsCollection)
                return sourceExpression;

            return parameter.CollectionKind switch
            {
                CollectionKind.Array => $"{sourceExpression}.ToArray()",
                CollectionKind.List => $"{sourceExpression}.ToList()",
                CollectionKind.ImmutableArray => $"global::System.Collections.Immutable.ImmutableArray.CreateRange({sourceExpression})",
                CollectionKind.ReadOnlySpan => $"new global::System.ReadOnlySpan<{parameter.CollectionElementFullName}>({sourceExpression}.ToArray())",
                _ => sourceExpression
            };
        }

        private static string BuildStaticResolveInvocation(
            StaticExtensionSpec spec,
            string methodName,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            return $"{spec.ExtensionClassName}.{methodName}({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "container", "state")})";
        }

        private static string BuildStaticResolveAllInvocation(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            return $"{spec.ExtensionClassName}.ResolveAllCore({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "container", "state")})";
        }

        private static string BuildStaticResolveSelectionExpression(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            return BuildBooleanSelectionExpression(
                spec.TypeFullName,
                spec.Possibilities,
                booleanIdentifiers,
                possibility => BuildStaticResolveInjectionInvocation(spec, possibility, booleanIdentifiers, externalIdentifiers));
        }

        private static string BuildStaticResolveInjectionInvocation(
            StaticExtensionSpec spec,
            InjectionData injection,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            return $"Resolve_{GetStaticInjectionHelperName(injection)}({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "container", "state")})";
        }

        private static string BuildStaticCreateInjectionInvocation(
            StaticExtensionSpec spec,
            InjectionData injection,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            return $"Create_{GetStaticInjectionHelperName(injection)}({BuildStaticInternalInvocationArguments(spec, booleanIdentifiers, externalIdentifiers, "container", "state")})";
        }

        private static string BuildStaticMethodParameterList(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers,
            bool includeContainer,
            bool includeState)
        {
            var parts = new List<string>();
            if (includeContainer)
                parts.Add($"{ClassName}? container");
            if (includeState)
                parts.Add("StaticResolveState state");

            var runtimeDeclarations = BuildStaticRuntimeArgumentDeclarations(spec, booleanIdentifiers, externalIdentifiers);
            if (!string.IsNullOrEmpty(runtimeDeclarations))
                parts.Add(runtimeDeclarations);

            return string.Join(", ", parts);
        }

        private static string BuildStaticRuntimeArgumentDeclarations(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var parts = spec.BooleanKeys.Select(key => $"bool {booleanIdentifiers[key]}")
                            .Concat(spec.ExternalParameters.Select(parameter => $"{parameter.TypeFullName} {externalIdentifiers[parameter.TypeFullName]}"))
                            .ToArray();

            return string.Join(", ", parts);
        }

        private static string BuildStaticRuntimeArgumentValues(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers)
        {
            var parts = spec.BooleanKeys.Select(key => booleanIdentifiers[key])
                            .Concat(spec.ExternalParameters.Select(parameter => externalIdentifiers[parameter.TypeFullName]))
                            .ToArray();

            return string.Join(", ", parts);
        }

        private static string BuildStaticInternalInvocationArguments(
            StaticExtensionSpec spec,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            IReadOnlyDictionary<string, string> externalIdentifiers,
            string containerExpression,
            string stateExpression)
        {
            var runtimeValues = BuildStaticRuntimeArgumentValues(spec, booleanIdentifiers, externalIdentifiers);
            if (string.IsNullOrEmpty(runtimeValues))
                return $"{containerExpression}, {stateExpression}";

            return $"{containerExpression}, {stateExpression}, {runtimeValues}";
        }


        private static string GetStaticInjectionHelperName(InjectionData injection)
        {
            if (injection.Lambda is null)
                return injection.TypeMemberName;

            var containingType = injection.Lambda.ContainingTypeMemberName.Replace("()", string.Empty);
            return $"{containingType}_{injection.Lambda.MemberName}_{injection.TypeMemberName}";
        }
    }
}