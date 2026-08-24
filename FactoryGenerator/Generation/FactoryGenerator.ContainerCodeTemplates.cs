using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace FactoryGenerator
{
    /// <summary>
    /// Renders the generated <c>DependencyInjectionContainer</c>/<c>LifetimeScope</c> partial-class
    /// source: the dictionary-based lookup, constructors, member declarations, and collection
    /// (<c>IEnumerable&lt;T&gt;</c>) declarations.
    ///
    /// <c>LifetimeScope</c> is a thin subclass of <c>DependencyInjectionContainer</c>, not a
    /// hand-duplicated sibling: every declaration emitted here (lookup dictionary, factory members,
    /// collection accessors) is written once and inherited by both. The only place root-vs-scope
    /// behavior actually differs is singleton storage, which <see cref="SymbolUtility.SingletonFactory"/>
    /// resolves per-instance via <c>m_singletonOwner</c> rather than via virtual dispatch or a second
    /// copy of every member.
    /// </summary>
    public partial class FactoryGenerator
    {
        private static void GenerateCode(InjectionAnalysis analysis, Compilation compilation, ILogger log, SourceProductionContext context)
        {
            var ordered = analysis.Ordered;
            var interfaceInjectors = analysis.InterfaceInjectors;
            var interfaceMemberNames = analysis.InterfaceMemberNames;
            var availableInterfaceFullNames = analysis.AvailableInterfaceFullNames;

            // Computed once and reused by both cycle-detection and the declarations loop below,
            // instead of each independently re-selecting a constructor/lambda member per injection.
            var resolutions = ResolveInjections(ordered, availableInterfaceFullNames);

            CheckForCycles(interfaceInjectors, availableInterfaceFullNames, resolutions);
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

            var lookup = $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable enable
#pragma warning disable CS0169, CS0414
// Not sealed: {LifetimeName} is a thin subclass (see Constructor.g.cs) that reuses every member
// declared here instead of duplicating them. Root-vs-scope singleton ownership is resolved via
// m_singletonOwner (see SymbolUtility.SingletonFactory), not virtual dispatch.
public partial class {ClassName} : IContainer, IContainerScopeFactory, IContainerRegistrationMetadata, IContainerCacheInvalidator, IAsyncDisposable, IContainerLocalCollectionResolver
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
    private readonly {ClassName} m_singletonOwner;
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
            context.AddSource($"FactoryGenerator.{ClassName}/Lookup.g.cs", lookup);

            var booleanKeys = analysis.RawInjections.Select(inj => inj.BooleanInjection)
                                            .Where(b => b is not null)
                                            .Select(b => b!.Key)
                                            .Distinct()
                                            .ToArray();

            var declarations = new Dictionary<string, string>();
            var constructorParameters = new List<ParameterData>();
            var seenConstructorParameterKeys = new HashSet<string>();

            foreach (var injection in ordered)
            {
                var resolution = resolutions[injection];
                declarations[injection.Name] = Declaration(injection, resolution.Creation);

                foreach (var param in resolution.MissingParameters)
                {
                    var key = param.TypeFullName + " " + param.Name;
                    if (seenConstructorParameterKeys.Add(key))
                        constructorParameters.Add(param);
                }
            }

            // A single partitioning pass instead of two ToArray()+List.Remove() loops: each Remove()
            // is an O(n) scan of its own, so removing k matches from an n-item list cost O(n*k) for
            // no reason. Order matters here — a collection-typed parameter is classified as
            // "localized" before the IContainer/self check even runs, matching the original two-pass
            // precedence (a parameter can't reach the IContainer check once collection-classified).
            var externalParameterCandidates = constructorParameters;
            constructorParameters = new List<ParameterData>(externalParameterCandidates.Count);
            var localizedParameters = new List<ParameterData>();
            foreach (var parameter in externalParameterCandidates)
            {
                if (parameter.IsCollection && parameter.CollectionElementFullName is not null)
                {
                    localizedParameters.Add(parameter);
                    continue;
                }

                if (parameter.TypeFullName.Contains("IContainer"))
                {
                    log.Log(LogLevel.Debug, $"Registering {parameter.Name} as Self");
                    declarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                    continue;
                }

                constructorParameters.Add(parameter);
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
                                                                "InitializeLookup",
                                                                "m_resolvedInstances",
                                                                "m_localCollectionLookup",
                                                                "m_lock",
                                                                "m_lookup",
                                                                "m_booleans",
                                                                "m_singletonOwner",
                                                                "singletonOwner",
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
                    if (ifaceMethodName == chosen.Name) continue;
                    if (declarations.ContainsKey(ifaceMethodName)) continue;
                    log.Log(LogLevel.Information, $"Selecting {chosen.Name} for {ifaceFull}");
                    declarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {chosen.Name};";
                }
                else
                {
                    var ternary = BuildBooleanSelectionExpression(
                        ifaceFull,
                        possibilities,
                        booleanIdentifiers,
                        possibility => possibility.Name);

                    if (declarations.ContainsKey(ifaceMethodName)) continue;
                    log.Log(LogLevel.Information, $"Selecting {ternary} for {ifaceFull}");
                    declarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {ternary};";
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

            var constructor = "(" + string.Join(", ", allArguments) + ")";

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
            context.AddSource($"FactoryGenerator.{ClassName}/Constructor.g.cs", Constructor(usingStatements, constructorFields,
                                                                                            constructor, constructorAssignments,
                                                                                            dictSize, interfacePairs, localizedPairs, enumerablePairs, constructorPairs, localCollectionPairs,
                                                                                            externalParameters, resolvedConstructorAssignments, booleanParameters, allArguments));
            Declarations(usingStatements, declarations, ClassName, context);
            ArrayDeclarations(usingStatements, arrayDeclarations, ClassName, context);

            // LifetimeScope: a thin subclass of {ClassName} (see the Constructor above for the
            // protected scope constructor it forwards to). It inherits the lookup dictionary, every
            // factory member, and every collection accessor unchanged — no duplicate declarations.
            var scopeConstructorParameters = string.Join(", ",
                                                         new[] {$"{ClassName} singletonOwner", "IContainer? baseContainer"}.Concat(allArguments));
            var scopeBaseArguments = string.Join(", ",
                                                 new[] {"singletonOwner", "baseContainer"}.Concat(allArguments.Select(arg => arg.Split(' ').Last())));
            context.AddSource($"FactoryGenerator.{LifetimeName}/{LifetimeName}.g.cs", $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable enable
internal sealed class {LifetimeName} : {ClassName}
{{
    internal {LifetimeName}({scopeConstructorParameters}) : base({scopeBaseArguments})
    {{
    }}
}}
");

            // Emit the static factory + module initializer for plugin container registration
            context.AddSource("FactoryGenerator.Helpers/Entrypoint.g.cs", $@"
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
");
        }

        /// <summary>
        /// Builds the <c>Constructor.g.cs</c> fragment for <see cref="ClassName"/>: the constructor
        /// fields, the three constructor overloads (root, cross-assembly "merging", and the
        /// protected scope overload used only by <see cref="LifetimeName"/>), and the shared
        /// <c>BeginLifetimeScope</c> implementation. A single <c>InitializeLookup</c> helper builds
        /// <c>m_lookup</c>/<c>m_localCollectionLookup</c> identically for all three, since the
        /// factory closures they capture already resolve correctly against whichever instance
        /// (<see cref="ClassName"/> or <see cref="LifetimeName"/>) constructed them.
        /// </summary>
        private static string Constructor(string usingStatements, string constructorFields, string constructor, string constructorAssignments, int dictSize,
                                          List<(string TypeName, string MemberName)> interfaceTypePairs, List<(string TypeName, string Expression)> localizedParamPairs,
                                          List<(string TypeName, string Expression)> enumerablePairs, List<(string TypeName, string Expression)> constructorParamPairs,
                                          List<(string TypeName, string Expression)> localCollectionPairs,
                                          List<ParameterData> externalParameters, string resolvingConstructorAssignments,
                                          IReadOnlyList<(string Key, string Identifier)> booleans, List<string> allArguments)
        {
            var booleanDictionaryEntries = string.Join("\n", booleans.Select(boolean => $"\t\t{{ \"{boolean.Key}\", {boolean.Identifier} }},"));
            var booleanFieldsFromBase = string.Join("\n", booleans.Select(boolean => $"\t this.{boolean.Identifier} = Base.GetBoolean(\"{boolean.Key}\");"));

            var scopeArgumentValues = new List<string> {"m_singletonOwner", "baseContainer"};
            scopeArgumentValues.AddRange(booleans.Select(boolean => boolean.Identifier));
            scopeArgumentValues.AddRange(externalParameters.Select(parameter => $"baseContainer != null ? baseContainer.Resolve<{parameter.TypeFullName}>() : {parameter.Name}"));

            return $@"{usingStatements}
#pragma warning disable CS8618 // m_lookup/m_localCollectionLookup are always assigned by InitializeLookup(), called from every constructor.
public partial class {ClassName}
{{
    {constructorFields}

    public {ClassName}{constructor}
    {{
        m_singletonOwner = this;
        {constructorAssignments}

        m_booleans = new({booleans.Count}) {{
{booleanDictionaryEntries}
        }};
        InitializeLookup();
    }}

    /// <summary>
    /// Cross-assembly composition constructor: chains this assembly's own container on top of
    /// another (possibly different assembly's) <see cref=""IContainer""/> via <see cref=""Base""/>,
    /// inheriting its booleans and resolving any of this assembly's external parameters from it.
    /// Unrelated to lifetime scoping.
    /// </summary>
    public {ClassName}(IContainer Base)
    {{
        m_singletonOwner = this;
        this.Base = Base;
        AttachToBase(Base);
        {resolvingConstructorAssignments}

{booleanFieldsFromBase}

        m_booleans = new();
        foreach(var (key, value) in Base.GetBooleans())
        {{
            m_booleans[key] = value;
        }}
        InitializeLookup();
    }}

    /// <summary>
    /// Used only by <see cref=""{LifetimeName}""/>. <paramref name=""singletonOwner""/> is always the
    /// original root container (see <see cref=""BeginLifetimeScope(IContainer?)""/>), so every scope —
    /// however many are created, and regardless of nesting — shares exactly one singleton owner.
    /// </summary>
    protected {ClassName}({ClassName} singletonOwner, IContainer? baseContainer{(allArguments.Count > 0 ? ", " + string.Join(", ", allArguments) : string.Empty)})
    {{
        m_singletonOwner = singletonOwner;
        this.Base = baseContainer;
        if (baseContainer is not null)
        {{
            AttachToBase(baseContainer);
            TrackResolvedInstance(baseContainer);
        }}
        {constructorAssignments}

        m_booleans = new({booleans.Count}) {{
{booleanDictionaryEntries}
        }};
        InitializeLookup();
    }}

    public ILifetimeScope BeginLifetimeScope()
    {{
        var baseContainer = Base?.BeginLifetimeScope() as IContainer;
        return BeginLifetimeScope(baseContainer);
    }}

    public ILifetimeScope BeginLifetimeScope(IContainer? baseContainer)
    {{
        var scope = new {LifetimeName}({string.Join(", ", scopeArgumentValues)});
        TrackResolvedInstance(scope);
        return scope;
    }}

    private void InitializeLookup()
    {{
        m_lookup = new({dictSize}) {{
{MakeDictionaryFromTypes(interfaceTypePairs)}
{MakeDictionaryFromParams(localizedParamPairs)}
{MakeDictionaryFromParams(enumerablePairs)}
{MakeDictionaryFromParams(constructorParamPairs)}
        }};
        m_localCollectionLookup = new({localCollectionPairs.Count}) {{
{MakeDictionaryFromParams(localCollectionPairs)}
        }};
    }}
}}
#pragma warning restore CS8618";
        }


        private static void ArrayDeclarations(string usingStatements, Dictionary<string, string> arrayDeclarations, string className, SourceProductionContext context)
        {
            var cacheInvalidations = string.Join("\n        ", arrayDeclarations.Keys.Select(name => $"m_{name} = null;"));
            foreach (var group in GroupByHintName(arrayDeclarations))
            {
                context.AddSource($"FactoryGenerator.Collection.Declarations/{group.HintName}.g.cs", $@"{usingStatements}
public partial class {className}
{{
    {string.Join("\n    ", group.Values)}
}}");
            }

            context.AddSource($"FactoryGenerator.{className}/Collection_Invalidation.g.cs", $@"{usingStatements}
public partial class {className}
{{
    public void InvalidateCollectionCaches()
    {{
        {cacheInvalidations}
    }}
}}");
        }

        private static void Declarations(string usingStatements, Dictionary<string, string> declarations, string className, SourceProductionContext context)
        {
            foreach (var group in GroupByHintName(declarations))
            {
                context.AddSource($"FactoryGenerator.Declarations/{group.HintName}.g.cs",
                                  $$"""
                                    {{usingStatements}}
                                    public partial class {{className}}
                                    {
                                        {{string.Join("\n    ", group.Values)}}
                                    };
                                    """);
            }
        }

        /// <summary>
        /// Groups declaration-dictionary entries by a hint name Roslyn would treat as identical.
        /// <c>SourceProductionContext.AddSource</c> compares hint names with
        /// <see cref="StringComparer.OrdinalIgnoreCase"/> (matching how the names typically also map
        /// to files on a case-insensitive file system), but the dictionaries this feeds are keyed
        /// with the default (ordinal, case-SENSITIVE) comparer — because the keys are also valid,
        /// distinct C# member/field names, and C# identifiers genuinely are case-sensitive (e.g. two
        /// different injections independently choosing external "IContainer"-typed constructor
        /// parameters named "lifeTimeScope" and "lifetimeScope" are both completely valid, unrelated
        /// C# programs on their own). Naively emitting one hint name per dictionary key would crash
        /// the entire generator with "hintName ... must be unique within a generator" as soon as two
        /// such keys collided only by case. Since a partial class's members can be split across any
        /// number of source files (or consolidated into one) with zero effect on the compiled
        /// result, the fix is to detect that case-only collision here and merge the colliding
        /// declarations into a single generated file/hint name — preserving every declaration
        /// instead of losing one to a silent dictionary overwrite or crashing outright.
        /// </summary>
        private static List<(string HintName, List<string> Values)> GroupByHintName(Dictionary<string, string> declarations)
        {
            var indexByHintName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var groups = new List<(string HintName, List<string> Values)>();
            foreach (var declaration in declarations)
            {
                if (indexByHintName.TryGetValue(declaration.Key, out var index))
                {
                    groups[index].Values.Add(declaration.Value);
                }
                else
                {
                    indexByHintName[declaration.Key] = groups.Count;
                    groups.Add((declaration.Key, new List<string> {declaration.Value}));
                }
            }

            return groups;
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
    }
}