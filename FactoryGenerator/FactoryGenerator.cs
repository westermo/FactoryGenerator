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
    public class FactoryGenerator : IIncrementalGenerator
    {
        private const string ToolName = nameof(FactoryGenerator);
        private const string Version = "1.0.0";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var logProvider = SetupLog(context);
            var references = context.CompilationProvider.Select(GetGlobalNamespace);
            var rest = references.SelectMany(FindMethods);
            var attributes = rest.Collect();
            var compilation = context.CompilationProvider;
            var combined = attributes.Combine(compilation).Combine(logProvider);
            context.RegisterSourceOutput(combined, MakeAutofacModule);

            var supportsStaticExtensions = context.ParseOptionsProvider.Select(IsAtLeastCSharp14);
            var emitStaticExtensions = context.AnalyzerConfigOptionsProvider.Select(GetEmitStaticExtensions);
            var staticExtensionsEnabled = supportsStaticExtensions.Combine(emitStaticExtensions)
                .Select(static (pair, _) => pair.Left && pair.Right);
            var extensionData = attributes.Combine(compilation).Combine(staticExtensionsEnabled);
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
                                       ((ImmutableArray<InjectionData> Injections, Compilation Compilation) Left, LoggingOptions? log) data)
        {
            var injections = data.Left.Injections;
            var compilation = data.Left.Compilation;
            var log = data.log?.FileName == null ? NullLogger.Instance : new Logger(data.log.FileName, data.log.LogLevel);

            var source = GenerateCode(injections, compilation, log).ToArray();
            context.AddSource("DependencyInjectionContainer.Lookup.g.cs", source[0]);
            context.AddSource("DependencyInjectionContainer.Constructor.g.cs", source[1]);
            context.AddSource("DependencyInjectionContainer.Declarations.g.cs", source[2]);
            context.AddSource("DependencyInjectionContainer.EnumerableDeclarations.g.cs", source[3]);
            context.AddSource("LifetimeScope.Lookup.g.cs", source[4]);
            context.AddSource("LifetimeScope.Constructor.g.cs", source[5]);
            context.AddSource("LifetimeScope.Declarations.g.cs", source[6]);
            context.AddSource("LifetimeScope.EnumerableDeclarations.g.cs", source[7]);
            context.AddSource("ContainerEntryPoint.g.cs", source[8]);
        }

        private static IEnumerable<InjectionData> FindMethods(INamespaceSymbol namespaceSymbol, CancellationToken token)
        {
            foreach (var type in SymbolUtility.GetAllTypes(namespaceSymbol))
            {
                token.ThrowIfCancellationRequested();
                if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Interface) continue;
                var typeAttributes = type.GetAttributes().Concat(type.AllInterfaces.SelectMany(i => i.GetAttributes()))
                                         .ToImmutableArray();
                if (typeAttributes.Any(IsInjection))
                {
                    var info = Injection.Create(type, typeAttributes, token);
                    if (info is not null) yield return info;
                }

                foreach (var method in type.GetMembers().OfType<IMethodSymbol>()
                                           .Where(method => method.DeclaredAccessibility == Accessibility.Public))
                {
                    var attributes = method.GetAttributes();
                    if (!attributes.Any(IsInjection))
                        continue;
                    var info = Injection.Create(method, attributes, token);
                    if (info is not null) yield return info;
                }

                foreach (var property in type.GetMembers().OfType<IPropertySymbol>()
                                             .Where(property => property.DeclaredAccessibility == Accessibility.Public))
                {
                    var attributes = property.GetAttributes();
                    if (!attributes.Any(IsInjection))
                        continue;
                    var info = Injection.Create(property, attributes, token);
                    if (info is not null) yield return info;
                }
            }

            bool IsInjection(AttributeData attribute)
            {
                return attribute.AttributeClass?.Name.Contains("Inject") == true && attribute.AttributeClass.ToString().StartsWith("FactoryGenerator.Attributes");
            }
        }

        private static INamespaceSymbol GetGlobalNamespace(Compilation compilation, CancellationToken token)
        {
            return compilation.GlobalNamespace;
        }

        private const string ClassName = "DependencyInjectionContainer";
        private const string LifetimeName = "LifetimeScope";

        private static IEnumerable<string> GenerateCode(ImmutableArray<InjectionData> dataInjections,
                                                        Compilation compilation, ILogger log)
        {
            CheckForCycles(dataInjections, compilation);
            log.Log(LogLevel.Debug, "Starting Code Generation");
            var usingStatements = $@"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;
using FactoryGenerator;
using System.CodeDom.Compiler;
namespace {compilation.Assembly.Name}.Generated;
#nullable enable";

            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable enable
#pragma warning disable CS0169, CS0414
public sealed partial class {ClassName} : IContainer, IContainerScopeFactory, IContainerRegistrationMetadata, IContainerCacheInvalidator, IAsyncDisposable, IContainerLocalCollectionResolver
{{
    
#pragma warning restore CS0169, CS0414
    private IContainer GetRoot()
    {{
        IContainer root = this;
        while(root.Base != null)
        {{
            root = root.Base;
        }}
        return root;
    }}
    private IContainer GetTop()
    {{
        IContainer top = this;
        while(top.Inheritor != null)
        {{
            top = top.Inheritor;
        }}
        return top;
    }}
    private void AttachToBase(IContainer baseContainer)
    {{
        if (baseContainer.Inheritor is null)
        {{
            baseContainer.Inheritor = this;
            InvalidateCollectionCachesInChain();
            return;
        }}

        var current = baseContainer.Inheritor;
        while (current!.Inheritor is not null)
        {{
            current = current.Inheritor;
        }}

        current.Inheritor = this;
        InvalidateCollectionCachesInChain();
    }}
    private void DetachFromBase()
    {{
        if (Base is null)
            return;

        if (Base.Inheritor == this)
        {{
            Base.Inheritor = Inheritor;
            Inheritor = null;
            InvalidateCollectionCachesInChain();
            return;
        }}

        var current = Base.Inheritor;
        while (current is not null && current.Inheritor != this)
        {{
            current = current.Inheritor;
        }}

        if (current is null)
            return;

        current.Inheritor = Inheritor;
        Inheritor = null;
        InvalidateCollectionCachesInChain();
    }}
    private void InvalidateCollectionCachesInChain()
    {{
        var current = GetRoot();
        while (current is not null)
        {{
            if (current is IContainerCacheInvalidator invalidator)
                invalidator.InvalidateCollectionCaches();

            current = current.Inheritor;
        }}
    }}
    public string AssemblyName => ""{compilation.Assembly.Name}"";
    public IContainer? Base {{ get; }}
    public IContainer? Inheritor {{ get; set; }}
    internal readonly object m_lock = new();
    private Dictionary<Type,Func<object>> m_lookup;
    private Dictionary<Type,Func<object>> m_localCollectionLookup;
    private Dictionary<string,bool> m_booleans;
    private readonly ResolvedInstanceTracker m_resolvedInstances = new();

    internal void TrackResolvedInstance(object instance) => m_resolvedInstances.Track(instance);

    public bool TryResolveLocalCollection(Type type, out object? resolved)
    {{
        if (m_localCollectionLookup.TryGetValue(type, out var factory))
        {{
            resolved = factory();
            return true;
        }}

        resolved = default;
        return false;
    }}

    public T Resolve<T>()
    {{
        if (m_lookup.TryGetValue(typeof(T), out var factory))
            return (T)factory();
        if (Base is not null)
            return Base.Resolve<T>();
        throw new KeyNotFoundException($""The type {{typeof(T)}} has not been registered, and thus cannot be resolved"");
    }}

    public object Resolve(Type type)
    {{
        if (m_lookup.TryGetValue(type, out var factory))
            return factory();
        if (Base is not null)
            return Base.Resolve(type);
        throw new KeyNotFoundException($""The type {{type}} has not been registered, and thus cannot be resolved"");
    }}

    public void Dispose()
    {{
        DetachFromBase();
        m_resolvedInstances.Dispose();
    }}

    public ValueTask DisposeAsync()
    {{
        DetachFromBase();
        return m_resolvedInstances.DisposeAsync();
    }}

    public bool TryResolve(Type type, out object? resolved)
    {{
        if(m_lookup.TryGetValue(type, out var factory))
        {{
            resolved = factory();
            return true;
        }}
        if(Base is not null)
            return Base.TryResolve(type, out resolved);
        resolved = default;
        return false;
    }}

    public bool TryResolve<T>(out T? resolved)
    {{
        if(m_lookup.TryGetValue(typeof(T), out var factory))
        {{
            resolved = (T)factory();
            return true;
        }}
        if(Base is not null)
            return Base.TryResolve<T>(out resolved);
        resolved = default;
        return false;
    }}
    public bool IsRegistered(Type type)
    {{
        return m_lookup.ContainsKey(type) || Base?.IsRegistered(type) == true;
    }}
    public bool IsRegistered<T>() => IsRegistered(typeof(T));
    public bool GetBoolean(string key)
    {{
        return m_booleans.TryGetValue(key, out var value) && value; 
    }}
    public IEnumerable<(string Key, bool Value)> GetBooleans()
    {{
        foreach(var pair in m_booleans)
        {{
            yield return (pair.Key, pair.Value);
        }}
    }}
}}";

            var booleanKeys = dataInjections.Select(inj => inj.BooleanInjection).Where(b => b is not null)
                                            .Select(b => b!.Key).Distinct().ToArray();
            var ordered = OrderInjections(dataInjections, compilation, log);
            var (interfaceInjectors, interfaceMemberNames) = BuildInterfaceInjectors(ordered);

            var declarations = new Dictionary<string, string>();
            var scopedDeclarations = new Dictionary<string, string>();
            var availableInterfaceFullNames = interfaceInjectors.Keys.ToImmutableArray();
            var constructorParameters = new List<ParameterData>();

            foreach (var injection in ordered)
            {
                declarations[injection.Name] = Declaration(injection, availableInterfaceFullNames, false);
                scopedDeclarations[injection.Name] = Declaration(injection, availableInterfaceFullNames, true);

                var missing = GetInjectionMissingParameters(injection, availableInterfaceFullNames);
                foreach (var param in missing)
                {
                    var key = param.TypeFullName + " " + param.Name;
                    if (constructorParameters.All(p => p.TypeFullName + " " + p.Name != key))
                        constructorParameters.Add(param);
                }
            }

            var localizedParameters = new List<ParameterData>();
            foreach (var parameter in constructorParameters.ToArray())
            {
                if (!parameter.IsCollection) continue;
                if (parameter.CollectionElementFullName is null) continue;
                constructorParameters.Remove(parameter);
                localizedParameters.Add(parameter);
            }

            foreach (var parameter in constructorParameters.ToArray())
            {
                if (!parameter.TypeFullName.Contains("IContainer")) continue;
                log.Log(LogLevel.Debug, $"Registering {parameter.Name} as Self");
                declarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                scopedDeclarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                constructorParameters.Remove(parameter);
            }

            ValidateExternalParameterTypes(constructorParameters);

            var booleanReservedNames = constructorParameters.Select(parameter => parameter.Name)
                .Concat(localizedParameters.Select(parameter => "coll_" + parameter.CollectionElementMemberName!))
                .Concat(interfaceMemberNames.Values.Select(name => "local_coll_" + name))
                .Concat(localizedParameters.Select(parameter => "m_coll_" + parameter.CollectionElementMemberName!))
                .Concat(interfaceMemberNames.Values)
                .Concat(ordered.Select(injection => injection.Name.Replace("()", string.Empty)))
                .Concat(ordered.Select(injection => injection.LazyFieldName))
                .Concat(new[]
                {
                    "Base",
                    "Inheritor",
                    "GetRoot",
                    "GetTop",
                    "Dispose",
                    "Resolve",
                    "TryResolve",
                    "TryResolveLocalCollection",
                    "IsRegistered",
                    "GetBoolean",
                    "GetBooleans",
                    "BeginLifetimeScope",
                    "DisposeAsync",
                    "TrackResolvedInstance",
                    "m_resolvedInstances",
                    "m_localCollectionLookup",
                    "m_lock",
                    "m_lookup",
                    "m_booleans",
                    "m_fallback",
                    "fallback",
                    "baseContainer"
                });
            var booleanIdentifiers = BuildBooleanParameterIdentifiers(booleanKeys, booleanReservedNames);
            var booleanParameters = booleanKeys.Select(key => (Key: key, Identifier: booleanIdentifiers[key])).ToList();

            foreach (var ifaceFull in interfaceInjectors.Keys)
            {
                var possibilities = interfaceInjectors[ifaceFull];
                var ifaceMember = interfaceMemberNames[ifaceFull];
                var ifaceMethodName = ifaceMember + "()";

                if (possibilities.All(i => i.BooleanInjection == null))
                {
                    var chosen = possibilities.Last();
                    if (ifaceMethodName != chosen.Name)
                    {
                        if (!declarations.ContainsKey(ifaceMethodName))
                        {
                            log.Log(LogLevel.Information, $"Selecting {chosen.Name} for {ifaceFull}");
                            declarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {chosen.Name};";
                            scopedDeclarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {chosen.Name};";
                        }
                    }
                }
                else
                {
                    var ternary = BuildBooleanSelectionExpression(
                        ifaceFull,
                        possibilities,
                        booleanIdentifiers,
                        possibility => possibility.Name);

                    if (!declarations.ContainsKey(ifaceMethodName))
                    {
                        log.Log(LogLevel.Information, $"Selecting {ternary} for {ifaceFull}");
                        declarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {ternary};";
                        scopedDeclarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {ternary};";
                    }
                }
            }

            var arrayDeclarations = new Dictionary<string, string>();
            foreach (var pair in interfaceInjectors)
            {
                var name = "coll_" + interfaceMemberNames[pair.Key];
                if (arrayDeclarations.ContainsKey(name))
                    continue;
                log.Log(LogLevel.Debug, $"Creating Collection: {name} of element type {pair.Key}");
                MakeArray(arrayDeclarations, name, pair.Key, interfaceInjectors, booleanIdentifiers);
            }

            foreach (var parameter in localizedParameters)
            {
                var name = "coll_" + parameter.CollectionElementMemberName!;
                if (arrayDeclarations.ContainsKey(name))
                    continue;
                log.Log(LogLevel.Debug, $"Creating Collection: {name} of element type {parameter.CollectionElementFullName}");
                MakeArray(arrayDeclarations, name, parameter.CollectionElementFullName!, interfaceInjectors, booleanIdentifiers);
            }

            var externalParameters = constructorParameters.OrderBy(parameter => parameter.TypeFullName).ToList();
            var allArguments = booleanParameters.Select(parameter => $"bool {parameter.Identifier}").ToList();
            allArguments.AddRange(externalParameters.Select(parameter => $"{parameter.TypeFullName} {parameter.Name}").Distinct());

            var lifetimeArguments = allArguments.ToList();
            lifetimeArguments.Insert(0, "IContainer? baseContainer");
            lifetimeArguments.Insert(0, $"{ClassName} fallback");
            var lifetimeParameters = new List<string> { "this", "baseContainer" };
            lifetimeParameters.AddRange(booleanParameters.Select(parameter => parameter.Identifier));
            lifetimeParameters.AddRange(externalParameters.Select(parameter => $"baseContainer != null ? baseContainer.Resolve<{parameter.TypeFullName}>() : {parameter.Name}"));

            var constructor = "(" + string.Join(", ", allArguments) + ")";
            var lifetimeConstructor = "(" + string.Join(", ", lifetimeArguments) + ")";
            var lifetimeParameterValues = string.Join(", ", lifetimeParameters);

            log.Log(LogLevel.Debug, $"Resulting Constructor: {constructor}");
            var constructorFields = string.Join("\n\t", allArguments.Select(arg => "internal " + arg + ";"));
            var constructorAssignments = string.Join("\n\t\t",
                allArguments.Select(arg => arg.Split(' ').Last()).Select(arg => $"this.{arg} = {arg};"));
            var resolvedConstructorAssignments = string.Join("\n\t\t",
                externalParameters.Select(parameter => $"this.{parameter.Name} = Base.Resolve<{parameter.TypeFullName}>();"));

            var interfacePairs = interfaceInjectors.Keys.Select(k => (TypeName: k, MemberName: interfaceMemberNames[k])).ToList();
            // ReadOnlySpan is a ref struct and cannot be placed in the lookup dictionary
            var localizedForDict = localizedParameters.Where(p => p.CollectionKind != CollectionKind.ReadOnlySpan).ToList();
            var localizedPairs = DistinctByTypeName(localizedForDict
                .Select(p => (TypeName: p.TypeFullName, Expression: CollectionDictExpression(p.CollectionKind, "coll_" + p.CollectionElementMemberName!)))
                .ToList(), pair => pair.TypeName);
            var localizedTypes = new HashSet<string>(localizedPairs.Select(pair => pair.TypeName));
            var enumerablePairs = interfaceInjectors.Keys
                .Select(key => (TypeName: $"System.Collections.Generic.IEnumerable<{key}>", Expression: "coll_" + interfaceMemberNames[key]))
                .Where(pair => !localizedTypes.Contains(pair.TypeName))
                .ToList();
            var localCollectionPairs = interfaceInjectors.Keys
                .Select(key => (TypeName: $"System.Collections.Generic.IEnumerable<{key}>", Expression: "local_coll_" + interfaceMemberNames[key]))
                .ToList();
            var constructorPairs = DistinctByTypeName(externalParameters.Select(p => (TypeName: p.TypeFullName, Expression: p.Name)).ToList(), pair => pair.TypeName);

            var dictSize = interfacePairs.Count + localizedPairs.Count + enumerablePairs.Count + constructorPairs.Count;
            yield return Constructor(usingStatements, constructorFields,
                                     constructor, constructorAssignments,
                                     dictSize, interfacePairs, localizedPairs, enumerablePairs, constructorPairs, localCollectionPairs,
                                     true, ClassName, lifetimeInvocationValues: lifetimeParameterValues,
                                     resolvingConstructorAssignments: resolvedConstructorAssignments, booleans: booleanParameters);
            yield return Declarations(usingStatements, declarations, ClassName);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations, ClassName);
            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable enable
#pragma warning disable CS0169, CS0414
public sealed partial class LifetimeScope : IContainer, IContainerScopeFactory, IContainerRegistrationMetadata, IContainerCacheInvalidator, IAsyncDisposable, IContainerLocalCollectionResolver
{{
#pragma warning restore CS0169, CS0414
    private IContainer GetRoot()
    {{
        IContainer root = this;
        while(root.Base != null)
        {{
            root = root.Base;
        }}
        return root;
    }}
    private IContainer GetTop()
    {{
        IContainer top = this;
        while(top.Inheritor != null)
        {{
            top = top.Inheritor;
        }}
        return top;
    }}
    private void AttachToBase(IContainer baseContainer)
    {{
        if (baseContainer.Inheritor is null)
        {{
            baseContainer.Inheritor = this;
            InvalidateCollectionCachesInChain();
            return;
        }}

        var current = baseContainer.Inheritor;
        while (current!.Inheritor is not null)
        {{
            current = current.Inheritor;
        }}

        current.Inheritor = this;
        InvalidateCollectionCachesInChain();
    }}
    private void DetachFromBase()
    {{
        if (Base is null)
            return;

        if (Base.Inheritor == this)
        {{
            Base.Inheritor = Inheritor;
            Inheritor = null;
            InvalidateCollectionCachesInChain();
            return;
        }}

        var current = Base.Inheritor;
        while (current is not null && current.Inheritor != this)
        {{
            current = current.Inheritor;
        }}

        if (current is null)
            return;

        current.Inheritor = Inheritor;
        Inheritor = null;
        InvalidateCollectionCachesInChain();
    }}
    private void InvalidateCollectionCachesInChain()
    {{
        var current = GetRoot();
        while (current is not null)
        {{
            if (current is IContainerCacheInvalidator invalidator)
                invalidator.InvalidateCollectionCaches();

            current = current.Inheritor;
        }}
    }}
    public string AssemblyName => ""{compilation.Assembly.Name}"";
    
    public IContainer? Base {{ get; }}
    public IContainer? Inheritor {{ get; set; }}
    public ILifetimeScope BeginLifetimeScope()
    {{
        var baseContainer = Base?.BeginLifetimeScope() as IContainer;
        return BeginLifetimeScope(baseContainer);
    }}
    public ILifetimeScope BeginLifetimeScope(IContainer? baseContainer)
    {{
        var scope = m_fallback.BeginLifetimeScope(baseContainer);
        TrackResolvedInstance(scope);
        return scope;
    }}
    internal readonly object m_lock = new();
    private {ClassName} m_fallback;
    private Dictionary<Type,Func<object>> m_lookup;
    private Dictionary<Type,Func<object>> m_localCollectionLookup;
    private Dictionary<string,bool> m_booleans;
    private readonly ResolvedInstanceTracker m_resolvedInstances = new();

    internal void TrackResolvedInstance(object instance) => m_resolvedInstances.Track(instance);

    public bool TryResolveLocalCollection(Type type, out object? resolved)
    {{
        if (m_localCollectionLookup.TryGetValue(type, out var factory))
        {{
            resolved = factory();
            return true;
        }}

        resolved = default;
        return false;
    }}

    public T Resolve<T>()
    {{
        if (m_lookup.TryGetValue(typeof(T), out var factory))
            return (T)factory();
        if (Base is not null)
            return Base.Resolve<T>();
        throw new KeyNotFoundException($""The type {{typeof(T)}} has not been registered, and thus cannot be resolved"");
    }}

    public object Resolve(Type type)
    {{
        if (m_lookup.TryGetValue(type, out var factory))
            return factory();
        if (Base is not null)
            return Base.Resolve(type);
        throw new KeyNotFoundException($""The type {{type}} has not been registered, and thus cannot be resolved"");
    }}

    public void Dispose()
    {{
        DetachFromBase();
        m_resolvedInstances.Dispose();
    }}

    public ValueTask DisposeAsync()
    {{
        DetachFromBase();
        return m_resolvedInstances.DisposeAsync();
    }}

    public bool TryResolve(Type type, out object? resolved)
    {{
        if(m_lookup.TryGetValue(type, out var factory))
        {{
            resolved = factory();
            return true;
        }}
        if(Base is not null)
            return Base.TryResolve(type, out resolved);
        resolved = default;
        return false;
    }}

    public bool TryResolve<T>(out T? resolved)
    {{
        if(m_lookup.TryGetValue(typeof(T), out var factory))
        {{
            resolved = (T)factory();
            return true;
        }}
        if(Base is not null)
            return Base.TryResolve<T>(out resolved);
        resolved = default;
        return false;
    }}
    public bool IsRegistered(Type type)
    {{
        return m_lookup.ContainsKey(type) || Base?.IsRegistered(type) == true;
    }}
    public bool IsRegistered<T>() => IsRegistered(typeof(T));
    
    public bool GetBoolean(string key)
    {{
        return m_booleans.TryGetValue(key, out var value) && value; 
    }}
    public IEnumerable<(string Key, bool Value)> GetBooleans()
    {{
        foreach(var pair in m_booleans)
        {{
            yield return (pair.Key, pair.Value);
        }}
    }}
}}
";
            yield return Constructor(usingStatements, constructorFields,
                                     lifetimeConstructor, constructorAssignments,
                                     dictSize, interfacePairs, localizedPairs, enumerablePairs, constructorPairs, localCollectionPairs,
                                     false, LifetimeName,
                                     resolvingConstructorAssignments: resolvedConstructorAssignments, addMergingConstructor: false, booleans: booleanParameters);
            yield return Declarations(usingStatements, scopedDeclarations, LifetimeName);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations, LifetimeName);

            // Emit the static factory + module initializer for plugin container registration
            yield return $@"
using System;
using System.Runtime.CompilerServices;
using FactoryGenerator;

#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{{
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    internal sealed class ModuleInitializerAttribute : Attribute {{ }}
}}
#endif

namespace {compilation.Assembly.Name}.Generated
{{
    /// <summary>
    /// Provides a static factory for the generated container and auto-registers it in the ContainerRegistry on assembly load.
    /// </summary>
    public static class ContainerEntryPoint
    {{
        /// <summary>
        /// Creates a new DependencyInjectionContainer that chains on top of the given base container.
        /// </summary>
        public static IContainer Create(IContainer baseContainer)
        {{
            return new {ClassName}(baseContainer);
        }}

        /// <summary>
        /// The assembly name this container was generated for.
        /// </summary>
        public static string AssemblyName => ""{compilation.Assembly.Name}"";

        [ModuleInitializer]
        internal static void Register()
        {{
            ContainerRegistry.Register(""{compilation.Assembly.Name}"", Create);
        }}
    }}
}}
";
        }

        private static List<InjectionData> OrderInjections(ImmutableArray<InjectionData> dataInjections, Compilation compilation, ILogger? log = null)
        {
            var ordered = dataInjections.Reverse().ToList();
            var assemblyDistances = BuildAssemblyDistances(compilation, ordered.Select(injection => injection.AssemblyName));

            foreach (var injection in ordered)
                log?.Log(LogLevel.Debug, $"Traversing {injection.Name} from {injection.AssemblyName} with priority {injection.AssemblyPriority}");

            return ordered
                .OrderBy(injection => injection.AssemblyPriority)
                .ThenByDescending(injection => GetAssemblyDistance(assemblyDistances, injection.AssemblyName))
                .ThenBy(injection => injection.AssemblyName, StringComparer.Ordinal)
                .ToList();
        }

        private static Dictionary<string, int> BuildAssemblyDistances(Compilation compilation, IEnumerable<string> assemblyNames)
        {
            var relevantAssemblyNames = new HashSet<string>(assemblyNames, StringComparer.Ordinal);
            var distances = new Dictionary<string, int>(StringComparer.Ordinal);
            var visited = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<(IAssemblySymbol Assembly, int Distance)>();

            visited.Add(compilation.Assembly);
            queue.Enqueue((compilation.Assembly, 0));

            while (queue.Count > 0 && distances.Count < relevantAssemblyNames.Count)
            {
                var current = queue.Dequeue();
                if (relevantAssemblyNames.Contains(current.Assembly.Name)
                    && (!distances.TryGetValue(current.Assembly.Name, out var existingDistance)
                        || current.Distance < existingDistance))
                {
                    distances[current.Assembly.Name] = current.Distance;
                }

                foreach (var referencedAssembly in GetReferencedAssemblies(current.Assembly))
                {
                    if (!visited.Add(referencedAssembly))
                        continue;

                    queue.Enqueue((referencedAssembly, current.Distance + 1));
                }
            }

            return distances;
        }

        private static IEnumerable<IAssemblySymbol> GetReferencedAssemblies(IAssemblySymbol assembly)
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var referencedAssembly in module.ReferencedAssemblySymbols)
                    yield return referencedAssembly;
            }
        }

        private static int GetAssemblyDistance(IReadOnlyDictionary<string, int> assemblyDistances, string assemblyName)
        {
            return assemblyDistances.TryGetValue(assemblyName, out var distance)
                ? distance
                : int.MaxValue;
        }

        private static (Dictionary<string, List<InjectionData>> InterfaceInjectors, Dictionary<string, string> InterfaceMemberNames) BuildInterfaceInjectors(IEnumerable<InjectionData> ordered)
        {
            var interfaceInjectors = new Dictionary<string, List<InjectionData>>();
            var interfaceMemberNames = new Dictionary<string, string>();

            foreach (var injection in ordered)
            {
                for (var i = 0; i < injection.InterfaceFullNames.Length; i++)
                {
                    var ifaceFull = injection.InterfaceFullNames[i];
                    var ifaceMember = injection.InterfaceMemberNames[i];
                    if (!interfaceInjectors.ContainsKey(ifaceFull))
                    {
                        interfaceInjectors[ifaceFull] = new List<InjectionData>();
                        interfaceMemberNames[ifaceFull] = ifaceMember;
                    }

                    interfaceInjectors[ifaceFull].Add(injection);
                }
            }

            return (interfaceInjectors, interfaceMemberNames);
        }

        private static List<InjectionData> GetReachableImplementations(List<InjectionData> possibilities)
        {
            if (possibilities.Count == 0)
                return new List<InjectionData>();

            if (possibilities.All(i => i.BooleanInjection == null))
                return new List<InjectionData> { possibilities.Last() };

            var reachable = new List<InjectionData>();
            var fallback = possibilities.LastOrDefault(p => p.BooleanInjection == null);
            var keys = possibilities.Select(p => p.BooleanInjection?.Key).OfType<string>().Distinct();

            foreach (var key in keys)
            {
                var selected = possibilities.LastOrDefault(p => p.BooleanInjection?.Value == true && p.BooleanInjection?.Key == key) ?? fallback;
                if (selected is not null && !reachable.Contains(selected))
                    reachable.Add(selected);
            }

            if (fallback is not null && !reachable.Contains(fallback))
                reachable.Add(fallback);

            return reachable;
        }

        private static IEnumerable<string> GetCycleDependencies(InjectionData injection, ImmutableArray<string> availableInterfaceFullNames)
        {
            if (injection.Lambda is LambdaData lambda)
            {
                if (availableInterfaceFullNames.Contains(lambda.ContainingTypeFullName))
                    yield return lambda.ContainingTypeFullName;

                if (!lambda.IsMethod)
                    yield break;

                foreach (var dependency in GetParameterDependencies(lambda.MethodParameters, availableInterfaceFullNames))
                    yield return dependency;

                yield break;
            }

            HashSet<ParameterData>? missing = null;
            HashSet<ParameterData>? nullableDefaults = null;
            var ctor = GetBestConstructor(injection, availableInterfaceFullNames, ref missing, ref nullableDefaults);
            if (ctor is null)
                yield break;

            foreach (var dependency in GetParameterDependencies(ctor.Parameters, availableInterfaceFullNames))
                yield return dependency;
        }

        private static IEnumerable<string> GetParameterDependencies(
            ImmutableArray<ParameterData> parameters,
            ImmutableArray<string> availableInterfaceFullNames)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.IsCollection)
                    continue;

                var typeLookup = parameter.IsNullable
                    ? parameter.TypeFullName.TrimEnd('?')
                    : parameter.TypeFullName;
                if (!availableInterfaceFullNames.Contains(typeLookup))
                    continue;

                yield return typeLookup;
            }
        }

        private static void ValidateExternalParameterTypes(List<ParameterData> constructorParameters)
        {
            var ambiguousParameters = constructorParameters
                .GroupBy(parameter => parameter.TypeFullName)
                .Select(group => new
                {
                    TypeFullName = group.Key,
                    Names = group.Select(parameter => parameter.Name).Distinct().OrderBy(name => name).ToArray()
                })
                .Where(group => group.Names.Length > 1)
                .ToList();

            if (ambiguousParameters.Count == 0)
                return;

            var details = string.Join("; ", ambiguousParameters.Select(group => $"{group.TypeFullName} ({string.Join(", ", group.Names)})"));
            throw new InvalidOperationException(
                $"Multiple externally provided values of the same type are not supported because FactoryGenerator resolves external values by type. Conflicting parameters: {details}. Wrap the values in distinct types or inject a dedicated options object.");
        }

        private static List<T> DistinctByTypeName<T>(IEnumerable<T> values, Func<T, string> typeNameSelector)
        {
            var distinct = new List<T>();
            var seenTypes = new HashSet<string>();

            foreach (var value in values)
            {
                if (!seenTypes.Add(typeNameSelector(value)))
                    continue;

                distinct.Add(value);
            }

            return distinct;
        }

        private static Dictionary<string, string> BuildBooleanParameterIdentifiers(IEnumerable<string> booleanKeys, IEnumerable<string> reservedNames)
        {
            var identifiers = new Dictionary<string, string>();
            var usedNames = new HashSet<string>(reservedNames);

            foreach (var booleanKey in booleanKeys)
            {
                var candidate = GetBooleanParameterIdentifier(booleanKey);
                var suffix = 1;
                while (!usedNames.Add(candidate))
                {
                    candidate = $"{candidate}_{suffix}";
                    suffix++;
                }

                identifiers[booleanKey] = candidate;
            }

            return identifiers;
        }

        private static Dictionary<string, string> BuildExternalParameterIdentifiers(IEnumerable<ParameterData> parameters, IEnumerable<string> reservedNames)
        {
            var identifiers = new Dictionary<string, string>();
            var usedNames = new HashSet<string>(reservedNames);

            foreach (var parameter in parameters.OrderBy(parameter => parameter.TypeFullName, StringComparer.Ordinal))
            {
                if (identifiers.ContainsKey(parameter.TypeFullName))
                    continue;

                var candidate = GetExternalParameterIdentifier(parameter.Name);
                var suffix = 1;
                while (!usedNames.Add(candidate))
                {
                    candidate = $"{candidate}_{suffix}";
                    suffix++;
                }

                identifiers[parameter.TypeFullName] = candidate;
            }

            return identifiers;
        }

        private static string BuildBooleanSelectionExpression(
            string typeFullName,
            IReadOnlyList<InjectionData> possibilities,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            Func<InjectionData, string> implementationExpression)
        {
            if (possibilities.All(possibility => possibility.BooleanInjection is null))
                return implementationExpression(possibilities.Last());

            var keys = possibilities.Select(possibility => possibility.BooleanInjection?.Key)
                .OfType<string>()
                .Distinct()
                .Reverse()
                .ToArray();

            var fallback = possibilities.LastOrDefault(possibility => possibility.BooleanInjection is null);
            var fallbackExpression = fallback is not null
                ? implementationExpression(fallback)
                : BuildMissingImplementationExpression(typeFullName);

            if (keys.Length == 0)
                return fallbackExpression;

            var last = keys.Last();
            var selection = new StringBuilder();
            foreach (var key in keys)
            {
                var selected = possibilities.LastOrDefault(possibility =>
                    possibility.BooleanInjection?.Value == true && possibility.BooleanInjection?.Key == key) ?? fallback;
                var selectedExpression = selected is not null
                    ? implementationExpression(selected)
                    : BuildMissingImplementationExpression(typeFullName);

                selection.Append(key == last
                    ? $"{booleanIdentifiers[key]} ? {selectedExpression} : {fallbackExpression}"
                    : $"{booleanIdentifiers[key]} ? {selectedExpression} : ");
            }

            return selection.ToString();
        }

        private static string GetBooleanParameterIdentifier(string booleanKey)
        {
            return GetSanitizedIdentifier(booleanKey, "boolean_");
        }

        private static string GetExternalParameterIdentifier(string parameterName)
        {
            return GetSanitizedIdentifier(parameterName, "argument_");
        }

        private static string GetSanitizedIdentifier(string value, string prefix)
        {
            if (SyntaxFacts.IsValidIdentifier(value))
                return value;

            var builder = new StringBuilder(prefix);
            foreach (var character in value)
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');

            var candidate = builder.ToString().TrimEnd('_');
            return SyntaxFacts.IsValidIdentifier(candidate) ? candidate : prefix.TrimEnd('_');
        }

        private static void CheckForCycles(ImmutableArray<InjectionData> dataInjections, Compilation compilation)
        {
            var ordered = OrderInjections(dataInjections, compilation);
            var (interfaceInjectors, _) = BuildInterfaceInjectors(ordered);
            var availableInterfaceFullNames = interfaceInjectors.Keys.ToImmutableArray();

            var graph = new Dictionary<string, HashSet<string>>();
            var nodeOwner = new Dictionary<string, string>();

            foreach (var interfaceInjector in interfaceInjectors)
            {
                var ifaceName = interfaceInjector.Key;
                var possibilities = interfaceInjector.Value;
                var reachable = GetReachableImplementations(possibilities);
                var deps = new HashSet<string>();

                foreach (var injection in reachable)
                {
                    foreach (var dep in GetCycleDependencies(injection, availableInterfaceFullNames))
                        deps.Add(dep);
                }

                graph[ifaceName] = deps;
                nodeOwner[ifaceName] = string.Join(", ", reachable.Select(injection => injection.TypeFullName).Distinct());
            }

            var state = new Dictionary<string, int>();
            var path = new List<string>();

            foreach (var node in graph.Keys)
            {
                if (!state.TryGetValue(node, out var s) || s == 0)
                    DfsCycleCheck(node, graph, state, path, nodeOwner);
            }
        }

        private static void DfsCycleCheck(string node, Dictionary<string, HashSet<string>> graph,
                                          Dictionary<string, int> state, List<string> path,
                                          Dictionary<string, string> nodeOwner)
        {
            state[node] = 1; // in-progress
            path.Add(node);

            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    state.TryGetValue(dep, out var depState);
                    if (depState == 1)
                    {
                        // Back-edge found — extract the cycle
                        var cycleStart = path.IndexOf(dep);
                        var cycle = path.GetRange(cycleStart, path.Count - cycleStart);
                        cycle.Add(dep);
                        string owner;
                        if (!nodeOwner.TryGetValue(node, out owner))
                            owner = node;
                        throw new InvalidOperationException(
                            $"Cyclic Dependency Detected: {string.Join(" \u2192 ", cycle)} (via {owner})");
                    }

                    if (depState == 0 && graph.ContainsKey(dep))
                        DfsCycleCheck(dep, graph, state, path, nodeOwner);
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2; // done
        }

        private static string Constructor(string usingStatements, string constructorFields, string constructor, string constructorAssignments, int dictSize,
                                          IEnumerable<(string TypeName, string MemberName)> interfaceTypePairs, IEnumerable<(string TypeName, string Expression)> localizedParamPairs,
                                          IEnumerable<(string TypeName, string Expression)> enumerablePairs, IEnumerable<(string TypeName, string Expression)> constructorParamPairs,
                                          IEnumerable<(string TypeName, string Expression)> localCollectionPairs,
                                          bool addLifetimeScopeFunction, string className, string? lifetimeInvocationValues = null,
                                          string? fromConstructor = null, string? resolvingConstructorAssignments = null, bool addMergingConstructor = true,
                                          IReadOnlyList<(string Key, string Identifier)> booleans = null!)
        {
            var lifetimeScopeFunction = addLifetimeScopeFunction
                                            ? $@"
public ILifetimeScope BeginLifetimeScope()
{{
    var baseContainer = Base?.BeginLifetimeScope() as IContainer;
    return BeginLifetimeScope(baseContainer);
}}
public ILifetimeScope BeginLifetimeScope(IContainer? baseContainer)
{{
    var scope = new {LifetimeName}({lifetimeInvocationValues});
    TrackResolvedInstance(scope);
    return scope;
}}" : string.Empty;

            var mergingConstructor = addMergingConstructor ? $@"
public {className}(IContainer Base{fromConstructor})
{{
    this.Base = Base;
    AttachToBase(Base);
    {resolvingConstructorAssignments}
    
{string.Join("\n", booleans.Select(boolean => $"\t this.{boolean.Identifier} = Base.GetBoolean(\"{boolean.Key}\");"))}
    
    m_lookup = new({dictSize}) {{
{MakeDictionaryFromTypes(interfaceTypePairs)}
{MakeDictionaryFromParams(localizedParamPairs)}
{MakeDictionaryFromParams(enumerablePairs)}
{MakeDictionaryFromParams(constructorParamPairs)}
    }};
    m_localCollectionLookup = new({localCollectionPairs.Count()}) {{
{MakeDictionaryFromParams(localCollectionPairs)}
    }};
    m_booleans = new();
    foreach(var (key, value) in Base.GetBooleans())
    {{
        m_booleans[key] = value;
    }}
}}" : string.Empty;


            var extraConstruction = addLifetimeScopeFunction ? string.Empty : @"m_fallback = fallback;
        this.Base = baseContainer;
        if (baseContainer is not null)
        {
            AttachToBase(baseContainer);
            TrackResolvedInstance(baseContainer);
        }";
            return $@"{usingStatements}
public partial class {className}
{{
    {constructorFields}
    public {className}{constructor}
    {{
        {extraConstruction}
        {constructorAssignments}
        
        m_lookup = new({dictSize})  {{
{MakeDictionaryFromTypes(interfaceTypePairs)}
{MakeDictionaryFromParams(localizedParamPairs)}
{MakeDictionaryFromParams(enumerablePairs)}
{MakeDictionaryFromParams(constructorParamPairs)}
        }};
        m_localCollectionLookup = new({localCollectionPairs.Count()})  {{
{MakeDictionaryFromParams(localCollectionPairs)}
        }};
        
    m_booleans = new({booleans.Count}) {{
{string.Join("\n", booleans.Select(boolean => $"\t\t{{ \"{boolean.Key}\", {boolean.Identifier} }},"))}
    }};
    }}
    {mergingConstructor}
    {lifetimeScopeFunction}

}}";
        }

        private static string ArrayDeclarations(string usingStatements, Dictionary<string, string> arrayDeclarations, string className)
        {
            var cacheInvalidations = string.Join("\n        ", arrayDeclarations.Keys.Select(name => $"m_{name} = null;"));

            return $@"{usingStatements}
public partial class {className}
{{
    {string.Join("\n\t", arrayDeclarations.Values)}

    public void InvalidateCollectionCaches()
    {{
        {cacheInvalidations}
    }}
}}";
        }

        private static string Declarations(string usingStatements, Dictionary<string, string> declarations, string className)
        {
            return $@"{usingStatements}
public partial class {className}
{{
    {string.Join("\n\t", declarations.Values)}
}}";
        }

        private static void MakeArray(Dictionary<string, string> declarations, string name,
                                      string elementTypeFullName, Dictionary<string, List<InjectionData>> interfaceInjectors,
                                      IReadOnlyDictionary<string, string> booleanIdentifiers)
        {
            var injections = interfaceInjectors.TryGetValue(elementTypeFullName, out var foundInjections)
                ? foundInjections
                : new List<InjectionData>();
            var localFactoryName = $"CreateLocal{name}()".Replace("_", "");
            var factoryName = $"Create{name}()".Replace("_", "");
            var nonBooleanInjections = injections.Where(i => i.BooleanInjection == null).ToList();
            var booleanInjections = injections.Where(b => b.BooleanInjection != null).ToList();
            var factory = @$"
    IEnumerable<{elementTypeFullName}> {localFactoryName}
    {{
        var source = new List<{elementTypeFullName}>({nonBooleanInjections.Count}) {{ 
            {string.Join(",\n\t\t\t", nonBooleanInjections.Select(i => i.Name))} 
        }};
        {string.Join("\n\t\t\t", booleanInjections.Select(i => $"if({booleanIdentifiers[i.BooleanInjection!.Key]}) source.Add({i.Name});"))}
        return source;
    }}
    private bool Reentrant_{name};
    IEnumerable<{elementTypeFullName}> {factoryName}
    {{
        if(Reentrant_{name}) return Array.Empty<{elementTypeFullName}>();
        Reentrant_{name} = true;
        var source = new List<{elementTypeFullName}>({localFactoryName});
        var b = Base;
        var frameworkCollectionSourceSeen = false;
        while(b is not null)
        {{
            if(!(frameworkCollectionSourceSeen && b is IServiceProviderBackedContainer))
            {{
                if (b is IContainerLocalCollectionResolver localResolver)
                {{
                    if(localResolver.TryResolveLocalCollection(typeof(IEnumerable<{elementTypeFullName}>), out var localAdditional))
                        source.AddRange((IEnumerable<{elementTypeFullName}>)localAdditional!);
                }}
                else if(b.TryResolve<IEnumerable<{elementTypeFullName}>>(out var additional))
                {{
                    source.AddRange(additional!);
                }}

                if (b is IServiceProviderBackedContainer)
                    frameworkCollectionSourceSeen = true;
            }}
            b = b.Base;
        }}
        var inheritorFrameworkCollectionSourceSeen = false;
        b = Inheritor;
        while(b is not null)
        {{
            if(!(inheritorFrameworkCollectionSourceSeen && b is IServiceProviderBackedContainer))
            {{
                if (b is IContainerLocalCollectionResolver localResolver)
                {{
                    if(localResolver.TryResolveLocalCollection(typeof(IEnumerable<{elementTypeFullName}>), out var localAdditional))
                        source.AddRange((IEnumerable<{elementTypeFullName}>)localAdditional!);
                }}
                else if(b.TryResolve<IEnumerable<{elementTypeFullName}>>(out var additional))
                {{
                    source.AddRange(additional!);
                }}

                if (b is IServiceProviderBackedContainer)
                    inheritorFrameworkCollectionSourceSeen = true;
            }}
            b = b.Inheritor;
        }}
        Reentrant_{name} = false;
        return source;
    }}";
            declarations[name] = $@"
    internal IEnumerable<{elementTypeFullName}> {name}
    {{
        get
        {{
            var cached = m_{name};
            if (cached != null)
                return cached;

            lock (m_lock)
            {{
                cached = m_{name};
                if (cached != null)
                    return cached;
                return m_{name} = {factoryName};
            }}
        }}
    }} 
    internal IEnumerable<{elementTypeFullName}> local_{name} => {localFactoryName};
    internal IEnumerable<{elementTypeFullName}>? m_{name};" + factory;
        }

        private static string MakeDictionaryFromTypes(IEnumerable<(string TypeName, string MemberName)> pairs)
        {
            var builder = new StringBuilder();
            foreach (var (typeName, memberName) in pairs)
                builder.AppendLine($"\t\t\t{{ typeof({typeName}),{memberName} }},");
            return builder.ToString();
        }

        private static string MakeDictionaryFromParams(IEnumerable<(string TypeName, string Expression)> pairs)
        {
            var builder = new StringBuilder();
            foreach (var (typeName, expression) in pairs)
                builder.AppendLine($"\t\t\t{{ typeof({typeName}), () => {expression} }},");
            return builder.ToString();
        }

        private static string Declaration(InjectionData injection, ImmutableArray<string> availableInterfaceFullNames, bool forLifetimeScope)
        {
            var name = injection.Name;
            var lazyName = injection.LazyFieldName;
            var creation = CreationCall(injection, availableInterfaceFullNames);

            if (forLifetimeScope && injection.Singleton)
                return $"internal {injection.TypeFullName} {name} => m_fallback.{name};";

            if (injection.Singleton || injection.Scoped)
                return SymbolUtility.SingletonFactory(injection.TypeFullName, name, lazyName, creation, injection.Disposable || injection.AsyncDisposable);

            if (injection.Disposable || injection.AsyncDisposable)
                return SymbolUtility.DisposableFactory(injection.TypeFullName, name, creation);

            return $"internal {injection.TypeFullName} {name} => {creation};";
        }

        private static string CreationCall(InjectionData injection, ImmutableArray<string> availableInterfaceFullNames)
        {
            if (injection.Lambda is LambdaData lambda)
            {
                if (!availableInterfaceFullNames.Contains(lambda.ContainingTypeFullName))
                    throw new Exception(
                        $"Could not find any [Inject]ed implementations of {lambda.ContainingTypeFullName} to use as the source for the injection of {lambda.ContainingTypeFullName}.{lambda.MemberName}. Please provide at least one injection of the type {lambda.ContainingTypeFullName}.");

                if (lambda.IsMethod)
                {
                    HashSet<ParameterData>? lambdaMissing = null;
                    HashSet<ParameterData>? lambdaNullableDefaults = null;
                    AnalyzeParameters(lambda.MethodParameters, availableInterfaceFullNames, ref lambdaMissing, ref lambdaNullableDefaults);
                    return $"{lambda.ContainingTypeMemberName}.{lambda.MemberName}{MakeMethodCall(lambda.MethodParameters, lambdaMissing, lambdaNullableDefaults)}";
                }
                else
                    return $"{lambda.ContainingTypeMemberName}.{lambda.MemberName}";
            }

            HashSet<ParameterData>? missing = null;
            HashSet<ParameterData>? nullableDefaults = null;
            var ctor = GetBestConstructor(injection, availableInterfaceFullNames, ref missing, ref nullableDefaults);
            if (ctor is null)
                throw new Exception($"No Construction method for {injection.TypeFullName}. Lambda was null.");

            return $"new {injection.TypeFullName}{MakeConstructorCall(ctor, missing, nullableDefaults)}";
        }

        private static ConstructorData? GetBestConstructor(InjectionData injection,
            ImmutableArray<string> availableInterfaceFullNames, ref HashSet<ParameterData>? missing,
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

        private static IEnumerable<ParameterData> GetInjectionMissingParameters(InjectionData injection,
            ImmutableArray<string> availableInterfaceFullNames)
        {
            if (injection.Lambda is LambdaData lambda)
            {
                if (!lambda.IsMethod)
                    return Enumerable.Empty<ParameterData>();

                HashSet<ParameterData>? lambdaMissing = null;
                HashSet<ParameterData>? lambdaNullableDefaults = null;
                AnalyzeParameters(lambda.MethodParameters, availableInterfaceFullNames, ref lambdaMissing, ref lambdaNullableDefaults);
                return lambdaMissing ?? Enumerable.Empty<ParameterData>();
            }

            HashSet<ParameterData>? missing = null;
            HashSet<ParameterData>? nullableDefaults = null;
            GetBestConstructor(injection, availableInterfaceFullNames, ref missing, ref nullableDefaults);
            return missing ?? Enumerable.Empty<ParameterData>();
        }

        private static void AnalyzeParameters(
            ImmutableArray<ParameterData> parameters,
            ImmutableArray<string> availableInterfaceFullNames,
            ref HashSet<ParameterData>? missing,
            ref HashSet<ParameterData>? nullableDefaults)
        {
            AnalyzeParameters(parameters, availableInterfaceFullNames, ref missing, ref nullableDefaults, out _);
        }

        private static void AnalyzeParameters(
            ImmutableArray<ParameterData> parameters,
            ImmutableArray<string> availableInterfaceFullNames,
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

        // ── C# 14 static-extension generation ────────────────────────────────────

        private static bool IsAtLeastCSharp14(ParseOptions options, CancellationToken _)
        {
            if (options is not CSharpParseOptions csOptions) return false;
            // C# 14 = 1400 in Roslyn's LanguageVersion enum.
            // LanguageVersion.Preview == int.MaxValue, which is also >= 1400.
            const int CSharp14 = 1400;
            return (int)csOptions.LanguageVersion >= CSharp14;
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
            var source = GenerateStaticExtensions(data.Left.Injections, data.Left.Compilation);
            context.AddSource("DependencyInjectionContainer.StaticExtensions.g.cs", source);
        }

        private static string GenerateStaticExtensions(
            ImmutableArray<InjectionData> dataInjections, Compilation compilation)
        {
            var ordered = OrderInjections(dataInjections, compilation);
            var (interfaceInjectors, interfaceMemberNames) = BuildInterfaceInjectors(ordered);
            var availableInterfaces = interfaceInjectors.Keys.ToImmutableArray();
            var specs = BuildStaticExtensionSpecs(interfaceInjectors, interfaceMemberNames, availableInterfaces);

            var reservedNames = BuildStaticExtensionReservedNames(specs, interfaceMemberNames);
            var externalIdentifiers = BuildExternalParameterIdentifiers(specs.Values.SelectMany(spec => spec.ExternalParameters), reservedNames);
            var booleanIdentifiers = BuildBooleanParameterIdentifiers(
                specs.Values.SelectMany(spec => spec.BooleanKeys).Distinct(),
                reservedNames.Concat(externalIdentifiers.Values));

            var sb = new StringBuilder();
            sb.AppendLine($@"using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
namespace {compilation.Assembly.Name}.Generated;
#nullable enable

internal sealed class StaticResolveState
{{
    private readonly HashSet<string> m_activeCollections = new(StringComparer.Ordinal);

    public bool EnterCollection(string key)
    {{
        return m_activeCollections.Add(key);
    }}

    public void ExitCollection(string key)
    {{
        m_activeCollections.Remove(key);
    }}
}}");

            foreach (var ifaceFull in interfaceMemberNames.Keys)
            {
                var spec = specs[ifaceFull];
                var helpers = BuildStaticExtensionClass(spec, specs, availableInterfaces, booleanIdentifiers, externalIdentifiers);

                sb.AppendLine($@"[GeneratedCode(""{ToolName}"", ""{Version}"")]
public static class {spec.ExtensionClassName}
{{
{helpers}
    extension({spec.TypeFullName})
    {{
{BuildStaticPublicResolveMethods(spec, booleanIdentifiers, externalIdentifiers)}
    }}
}}");
            }

            return sb.ToString();
        }

        private static Dictionary<string, StaticExtensionSpec> BuildStaticExtensionSpecs(
            Dictionary<string, List<InjectionData>> interfaceInjectors,
            Dictionary<string, string> interfaceMemberNames,
            ImmutableArray<string> availableInterfaces)
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
            ImmutableArray<string> availableInterfaces,
            Dictionary<string, List<InjectionData>> interfaceInjectors)
        {
            var spec = new StaticExtensionSpec(typeFullName, typeMemberName, typeMemberName + "Extensions", possibilities);
            PopulateStaticExtensionDirectRequirements(spec, availableInterfaces, interfaceInjectors);
            return spec;
        }

        private static void PopulateStaticExtensionDirectRequirements(
            StaticExtensionSpec spec,
            ImmutableArray<string> availableInterfaces,
            Dictionary<string, List<InjectionData>> interfaceInjectors)
        {
            foreach (var booleanKey in spec.Possibilities.Select(possibility => possibility.BooleanInjection?.Key).OfType<string>())
                AddDistinctString(spec.BooleanKeys, booleanKey);

            foreach (var possibility in spec.Possibilities)
            {
                if (possibility.Lambda is LambdaData lambda)
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
            ImmutableArray<string> availableInterfaces,
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
                .Select(possibility => $"            if ({booleanIdentifiers[possibility.BooleanInjection!.Key]}) source.Add({BuildStaticResolveInjectionInvocation(spec, possibility, booleanIdentifiers, externalIdentifiers)});")
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
            ImmutableArray<string> availableInterfaces,
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
            ImmutableArray<string> availableInterfaces,
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

        private static string BuildMissingImplementationExpression(string typeFullName)
        {
            return $"throw new global::System.InvalidOperationException(\"Cannot resolve {typeFullName} without a matching implementation\")";
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