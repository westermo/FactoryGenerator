using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            var syntaxUsages = context.SyntaxProvider.CreateSyntaxProvider(ResolveSymbols, ResolveTransformations)
                                      .Collect();
            var combined = attributes.Combine(compilation).Combine(syntaxUsages).Combine(logProvider);
            context.RegisterSourceOutput(combined, MakeAutofacModule);
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
                                       (((ImmutableArray<InjectionData> Injections, Compilation Compilation) Left, ImmutableArray<UsageData?> CompileTimeResolvedTypes) Left, LoggingOptions? log)
                                           data)
        {
            var injections = data.Left.Left.Injections;
            var compilation = data.Left.Left.Compilation;
            var usages = data.Left.CompileTimeResolvedTypes;
            var log = data.log?.FileName == null ? NullLogger.Instance : new Logger(data.log.FileName, data.log.LogLevel);

            var source = GenerateCode(injections, compilation, usages, log).ToArray();
            context.AddSource("DependencyInjectionContainer.Lookup.g.cs", source[0]);
            context.AddSource("DependencyInjectionContainer.Constructor.g.cs", source[1]);
            context.AddSource("DependencyInjectionContainer.Declarations.g.cs", source[2]);
            context.AddSource("DependencyInjectionContainer.EnumerableDeclarations.g.cs", source[3]);
            context.AddSource("LifetimeScope.Lookup.g.cs", source[4]);
            context.AddSource("LifetimeScope.Constructor.g.cs", source[5]);
            context.AddSource("LifetimeScope.Declarations.g.cs", source[6]);
            context.AddSource("LifetimeScope.EnumerableDeclarations.g.cs", source[7]);
        }

        private UsageData? ResolveTransformations(GeneratorSyntaxContext context, CancellationToken token)
        {
            var typeArguments = context.Node.DescendantNodes().OfType<TypeArgumentListSyntax>().FirstOrDefault();
            if (typeArguments is null) return null;
            var identifier = typeArguments.DescendantNodes().FirstOrDefault();
            if (identifier is null) return null;
            var info = context.SemanticModel.GetSymbolInfo(identifier, token);
            if (info.Symbol is not INamedTypeSymbol symbol) return null;
            if (!SymbolUtility.IsEnumerable(symbol)) return null;
            if (symbol.TypeArguments.Length != 1) return null;
            var elemType = symbol.TypeArguments[0];
            return new UsageData(
                fullName: symbol.ToString()!,
                memberName: SymbolUtility.MemberName(symbol).Replace("()", ""),
                elementTypeFullName: elemType.ToString()!,
                elementTypeMemberName: SymbolUtility.MemberName(elemType).Replace("()", ""));
        }

        private bool ResolveSymbols(SyntaxNode node, CancellationToken token)
        {
            if (node is not MemberAccessExpressionSyntax invocation) return false;
            return invocation.ToString().Contains("Resolve");
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
                                                        Compilation compilation, ImmutableArray<UsageData?> usages, ILogger log)
        {
            CheckForCycles(dataInjections);
            log.Log(LogLevel.Debug, "Starting Code Generation");
            var usingStatements = $@"
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Immutable;
using FactoryGenerator;
using System.CodeDom.Compiler;
namespace {compilation.Assembly.Name}.Generated;
#nullable enable";

            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable enable
#pragma warning disable CS0169, CS0414
public sealed partial class {ClassName} : IContainer
{{
    
    private bool Reentrant;
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
    public IContainer? Base {{ get; }}
    public IContainer? Inheritor {{ get; set; }}
    private readonly object m_lock = new();
    private Dictionary<Type,Func<object>> m_lookup;
    private Dictionary<string,bool> m_booleans;
    private List<WeakReference<IDisposable>>? resolvedInstances;

    private List<WeakReference<IDisposable>> GetResolvedInstances()
    {{
        if (resolvedInstances is null)
            lock (m_lock)
                resolvedInstances ??= new List<WeakReference<IDisposable>>();
        return resolvedInstances;
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
        if (resolvedInstances is not null)
        {{
            foreach (var weakReference in resolvedInstances)
            {{
                if(weakReference.TryGetTarget(out var disposable))
                {{
                    disposable.Dispose();
                }}
            }}
            resolvedInstances.Clear();
        }}
        Base?.Dispose();
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

            var booleans = dataInjections.Select(inj => inj.BooleanInjection).Where(b => b is not null)
                                         .Select(b => b!.Key).Distinct().ToArray();
            var allArguments = booleans.Select(b => $"bool {b}").ToList();
            var justBooleans = allArguments.ToList();
            var allParameters = booleans.Select(b => $"{b}").ToList();
            var ordered = dataInjections.Reverse().ToList();

            foreach (var injection in ordered.ToArray())
            {
                log.Log(LogLevel.Debug, $"Traversing {injection.Name}");
                if (!injection.IsTestType) continue;
                ordered.Remove(injection);
                ordered.Add(injection);
            }

            var interfaceInjectors = new Dictionary<string, List<InjectionData>>();
            var interfaceMemberNames = new Dictionary<string, string>();

            foreach (var injection in ordered)
            {
                for (int i = 0; i < injection.InterfaceFullNames.Length; i++)
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

            var declarations = new Dictionary<string, string>();
            var scopedDeclarations = new Dictionary<string, string>();
            var availableInterfaceFullNames = interfaceInjectors.Keys.ToImmutableArray();
            var constructorParameters = new List<ParameterData>();

            foreach (var injection in ordered)
            {
                declarations[injection.Name] = Declaration(injection, availableInterfaceFullNames, false);
                scopedDeclarations[injection.Name] = Declaration(injection, availableInterfaceFullNames, true);

                var missing = GetBestConstructorMissing(injection, availableInterfaceFullNames);
                foreach (var param in missing)
                {
                    var key = param.TypeFullName + " " + param.Name;
                    if (constructorParameters.All(p => p.TypeFullName + " " + p.Name != key))
                        constructorParameters.Add(param);
                }
            }

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
                    var keys = possibilities.Select(i => i.BooleanInjection?.Key).OfType<string>().Distinct().Reverse().ToArray();
                    var fallback = possibilities.LastOrDefault(p => p.BooleanInjection == null);
                    var last = keys.Last();
                    var ternary = new StringBuilder();
                    foreach (var key in keys)
                    {
                        var trueValue = possibilities.LastOrDefault(p =>
                            p.BooleanInjection?.Value == true && p.BooleanInjection?.Key == key);
                        trueValue ??= fallback;
                        ternary.Append(key == last
                            ? $"{key} ? {trueValue?.Name ?? "null!"} : {fallback?.Name ?? "null!"}"
                            : $"{key} ? {trueValue?.Name ?? "null!"} : ");
                    }

                    if (!declarations.ContainsKey(ifaceMethodName))
                    {
                        log.Log(LogLevel.Information, $"Selecting {ternary} for {ifaceFull}");
                        declarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {ternary};";
                        scopedDeclarations[ifaceMethodName] = $"internal {ifaceFull} {ifaceMethodName} => {ternary};";
                    }
                }
            }

            var localizedParameters = new List<ParameterData>();
            var arrayDeclarations = new Dictionary<string, string>();

            foreach (var parameter in constructorParameters.ToArray())
            {
                if (!parameter.IsCollection) continue;
                if (parameter.CollectionElementFullName is null) continue;
                var name = parameter.Name;
                log.Log(LogLevel.Debug, $"Creating Collection: {name} of element type {parameter.CollectionElementFullName}");
                MakeArray(arrayDeclarations, name, parameter.CollectionElementFullName, parameter.CollectionElementMemberName!, interfaceInjectors);
                constructorParameters.Remove(parameter);
                localizedParameters.Add(parameter);
            }

            var requestedUsages = new List<UsageData>();

            foreach (var request in usages)
            {
                if (request is null) continue;
                if (localizedParameters.Any(p => p.TypeFullName == request.FullName)) continue;
                log.Log(LogLevel.Information, $"Creating Requested: {request.FullName}");
                log.Log(LogLevel.Debug, $"Creating Array: {request.MemberName} of type {request.ElementTypeFullName}[]");
                MakeArray(arrayDeclarations, request.MemberName, request.ElementTypeFullName, request.ElementTypeMemberName, interfaceInjectors, true);
                requestedUsages.Add(request);
            }

            foreach (var parameter in constructorParameters.ToArray())
            {
                if (!parameter.TypeFullName.Contains("IContainer")) continue;
                log.Log(LogLevel.Debug, $"Registering {parameter.Name} as Self");
                declarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                scopedDeclarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                constructorParameters.Remove(parameter);
            }

            var arguments = constructorParameters.OrderBy(p => p.TypeFullName).Select(p => $"{p.TypeFullName} {p.Name}").Distinct();
            var parameters = constructorParameters.OrderBy(p => p.TypeFullName).Select(p => p.Name).Distinct();
            allArguments.AddRange(arguments);
            var lifetimeArguments = allArguments.ToList();
            allParameters.AddRange(parameters);

            var constructor = "(" + string.Join(", ", allArguments) + ")";
            lifetimeArguments.Insert(0, $"{ClassName} fallback");
            allParameters.Insert(0, "this");
            var lifetimeConstructor = "(" + string.Join(", ", lifetimeArguments) + ")";
            var lifetimeParameters = string.Join(", ", allParameters);

            log.Log(LogLevel.Debug, $"Resulting Constructor: {constructor}");
            var constructorFields = string.Join("\n\t", allArguments.Select(arg => arg + ";"));
            var constructorAssignments = string.Join("\n\t\t",
                allArguments.Select(arg => arg.Split(' ').Last()).Select(arg => $"this.{arg} = {arg};"));
            var resolvedConstructorAssignments = string.Join("\n\t\t",
                allArguments.Select(a => a.Split(' ')).Where(a => a[0] != "bool")
                            .Select(a => $"this.{a[1]} = Base.Resolve<{a[0]}>();"));

            var interfacePairs = interfaceInjectors.Keys.Select(k => (TypeName: k, MemberName: interfaceMemberNames[k])).ToList();
            // ReadOnlySpan is a ref struct and cannot be placed in the lookup dictionary
            var localizedForDict = localizedParameters.Where(p => p.CollectionKind != CollectionKind.ReadOnlySpan).ToList();
            var localizedPairs = localizedForDict
                .Select(p => (TypeName: p.TypeFullName, Expression: CollectionDictExpression(p.CollectionKind, p.Name)))
                .ToList();
            var requestedPairs = requestedUsages.Select(u => (TypeName: u.FullName, MemberName: u.MemberName)).ToList();
            var constructorPairs = constructorParameters.Select(p => (TypeName: p.TypeFullName, Expression: p.Name)).ToList();

            var dictSize = interfaceInjectors.Count + localizedForDict.Count + requestedUsages.Count + constructorParameters.Count;
            yield return Constructor(usingStatements, constructorFields,
                                     constructor, constructorAssignments,
                                     dictSize, interfacePairs, localizedPairs, requestedPairs, constructorPairs,
                                     true, ClassName, lifetimeParameters,
                                     resolvingConstructorAssignments: resolvedConstructorAssignments, booleans: justBooleans);
            yield return Declarations(usingStatements, declarations, ClassName);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations, ClassName);
            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable enable
#pragma warning disable CS0169, CS0414
public sealed partial class LifetimeScope : IContainer
{{
    private bool Reentrant;
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
    
    public IContainer? Base {{ get; }}
    public IContainer? Inheritor {{ get; set; }}
    public ILifetimeScope BeginLifetimeScope()
    {{
        var scope = m_fallback.BeginLifetimeScope();
        GetResolvedInstances().Add(new WeakReference<IDisposable>(scope));
        return scope;
    }}
    private readonly object m_lock = new();
    private {ClassName} m_fallback;
    private Dictionary<Type,Func<object>> m_lookup;
    private Dictionary<string,bool> m_booleans;
    private List<WeakReference<IDisposable>>? resolvedInstances;

    private List<WeakReference<IDisposable>> GetResolvedInstances()
    {{
        if (resolvedInstances is null)
            lock (m_lock)
                resolvedInstances ??= new List<WeakReference<IDisposable>>();
        return resolvedInstances;
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
        if (resolvedInstances is not null)
        {{
            foreach (var weakReference in resolvedInstances)
            {{
                if(weakReference.TryGetTarget(out var disposable))
                {{
                    disposable.Dispose();
                }}
            }}
            resolvedInstances.Clear();
        }}
        Base?.Dispose();
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
                                     dictSize, interfacePairs, localizedPairs, requestedPairs, constructorPairs,
                                     false, LifetimeName,
                                     resolvingConstructorAssignments: resolvedConstructorAssignments, addMergingConstructor: false, booleans: justBooleans);
            yield return Declarations(usingStatements, scopedDeclarations, LifetimeName);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations, LifetimeName);
        }

        private static void CheckForCycles(ImmutableArray<InjectionData> dataInjections)
        {
            var tree = new Dictionary<string, List<string>>();
            foreach (var injection in dataInjections)
            {
                if (injection.Lambda != null) continue;
                var node = new List<string>();
                foreach (var ifaceName in injection.InterfaceFullNames)
                {
                    if (!tree.ContainsKey(ifaceName))
                        tree[ifaceName] = node;
                }

                foreach (var ctor in injection.Constructors)
                {
                    foreach (var parameter in ctor.Parameters)
                    {
                        string? depName;
                        if (parameter.IsCollection)
                        {
                            if (parameter.CollectionElementFullName is null) continue;
                            depName = parameter.CollectionElementFullName;
                        }
                        else
                        {
                            // Strip ? so nullable params resolve to their underlying type in the cycle graph
                            depName = parameter.IsNullable
                                ? parameter.TypeFullName.TrimEnd('?')
                                : parameter.TypeFullName;
                        }

                        node.Add(depName);
                        if (tree.TryGetValue(depName, out var list))
                        {
                            foreach (var ifaceName in injection.InterfaceFullNames)
                            {
                                if (list.Contains(ifaceName))
                                    throw new InvalidOperationException(
                                        $"Cyclic Dependency Detected between {injection.TypeFullName} and {ifaceName}");
                            }
                        }
                    }
                }
            }
        }

        private static string Constructor(string usingStatements, string constructorFields, string constructor, string constructorAssignments, int dictSize,
                                          IEnumerable<(string TypeName, string MemberName)> interfaceTypePairs, IEnumerable<(string TypeName, string Expression)> localizedParamPairs,
                                          IEnumerable<(string TypeName, string MemberName)> requestedPairs, IEnumerable<(string TypeName, string Expression)> constructorParamPairs,
                                          bool addLifetimeScopeFunction, string className, string? lifetimeParameters = null,
                                          string? fromConstructor = null, string? resolvingConstructorAssignments = null, bool addMergingConstructor = true, List<string> booleans = null!)
        {
            var lifetimeScopeFunction = addLifetimeScopeFunction
                                            ? $@"
public ILifetimeScope BeginLifetimeScope()
{{
    var scope = new {LifetimeName}({lifetimeParameters});
    GetResolvedInstances().Add(new WeakReference<IDisposable>(scope));
    return scope;
}}" : string.Empty;

            var mergingConstructor = addMergingConstructor ? $@"
public {className}(IContainer Base{fromConstructor})
{{
    this.Base = Base;
    Base.Inheritor = this;  
    {resolvingConstructorAssignments}
    
{string.Join("\n", booleans.Select(b => b.Split(' ').Last()).Select(b => $"\t this.{b} = Base.GetBoolean(\"{b}\");"))}
    
    m_lookup = new({dictSize}) {{
{MakeDictionaryFromTypes(interfaceTypePairs)}
{MakeDictionaryFromParams(localizedParamPairs)}
{MakeDictionaryFromTypes(requestedPairs)}
{MakeDictionaryFromParams(constructorParamPairs)}
    }};
    m_booleans = new();
    foreach(var (key, value) in Base.GetBooleans())
    {{
        m_booleans[key] = value;
    }}
}}" : string.Empty;


            var extraConstruction = addLifetimeScopeFunction ? string.Empty : "m_fallback = fallback;";
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
{MakeDictionaryFromTypes(requestedPairs)}
{MakeDictionaryFromParams(constructorParamPairs)}
        }};
        
    m_booleans = new({booleans.Count}) {{
{string.Join("\n", booleans.Select(b => b.Split(' ').Last()).Select(b => $"\t\t{{ \"{b}\", {b} }},"))}
    }};
    }}
    {mergingConstructor}
    {lifetimeScopeFunction}

}}";
        }

        private static string ArrayDeclarations(string usingStatements, Dictionary<string, string> arrayDeclarations, string className)
        {
            return $@"{usingStatements}
public partial class {className}
{{
    {string.Join("\n\t", arrayDeclarations.Values)}
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
                                      string elementTypeFullName, string elementTypeMemberName,
                                      Dictionary<string, List<InjectionData>> interfaceInjectors, bool function = false)
        {
            var factoryName = $"new {elementTypeFullName}[0]";
            var factory = string.Empty;
            var functionString = function ? "()" : string.Empty;
            var starter = function ? string.Empty : "get {";
            var ender = function ? string.Empty : "}";
            if (interfaceInjectors.TryGetValue(elementTypeFullName, out var injections))
            {
                factoryName = $"Create{name}()".Replace("_", "");
                var nonBooleanInjections = injections.Where(i => i.BooleanInjection == null).ToList();
                var booleanInjections = injections.Where(b => b.BooleanInjection != null).ToList();
                factory = @$"
    IEnumerable<{elementTypeFullName}> {factoryName}
    {{
        if(Reentrant) return Array.Empty<{elementTypeFullName}>();
        Reentrant = true;
        var source = new List<{elementTypeFullName}>({nonBooleanInjections.Count}) {{ 
            {string.Join(",\n\t\t\t", nonBooleanInjections.Select(i => i.Name))} 
        }};
        {string.Join("\n\t\t\t", booleanInjections.Select(i => $"if({i.BooleanInjection!.Key}) source.Add({i.Name});"))}
        var b = Base;
        while(b is not null)
        {{
            if(b.TryResolve<IEnumerable<{elementTypeFullName}>>(out var additional)) source.AddRange(additional!);
            b = b.Base;
        }}
        b = Inheritor;
        while(b is not null)
        {{
            if(b.TryResolve<IEnumerable<{elementTypeFullName}>>(out var additional)) source.AddRange(additional!);
            b = b.Inheritor;
        }}
        Reentrant = false;
        return source;
    }}";
            }
            declarations[name] = $@"
    internal IEnumerable<{elementTypeFullName}> {name}{functionString}
    {{
        {starter}
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
        {ender}
    }} 
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
                return SymbolUtility.SingletonFactory(injection.TypeFullName, name, lazyName, creation, injection.Disposable);

            if (injection.Disposable)
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

                // Compute nullable defaults for lambda method parameters
                HashSet<ParameterData>? lambdaNullableDefaults = null;
                if (lambda.IsMethod && lambda.MethodParameters.Length > 0)
                {
                    foreach (var p in lambda.MethodParameters)
                    {
                        if (!p.IsNullable) continue;
                        var baseType = p.TypeFullName.TrimEnd('?');
                        if (availableInterfaceFullNames.Contains(baseType)) continue;
                        lambdaNullableDefaults ??= new HashSet<ParameterData>();
                        lambdaNullableDefaults.Add(p);
                    }
                }

                if (lambda.IsMethod)
                    return $"{lambda.ContainingTypeMemberName}.{lambda.MemberName}{MakeMethodCall(lambda.MethodParameters, null, lambdaNullableDefaults)}";
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
                var valid = true;
                var localMissing = new HashSet<ParameterData>();
                var localNullableDefaults = new HashSet<ParameterData>();
                foreach (var parameter in ctor.Parameters)
                {
                    // For nullable params, check availability of the underlying non-nullable type
                    var typeLookup = parameter.IsNullable
                        ? parameter.TypeFullName.TrimEnd('?')
                        : parameter.TypeFullName;
                    if (availableInterfaceFullNames.Contains(typeLookup)) continue;
                    if (parameter.HasExplicitDefault) continue;
                    if (parameter.IsParams) continue;
                    // Collection params (IEnumerable<T>, T[], List<T>, etc.) are always satisfiable
                    // via MakeArray – add to missing for factory generation but keep constructor valid
                    if (parameter.IsCollection)
                    {
                        localMissing.Add(parameter);
                        continue;
                    }
                    // Nullable reference/value params that aren't registered default to null
                    if (parameter.IsNullable)
                    {
                        localNullableDefaults.Add(parameter);
                        continue;
                    }
                    valid = false;
                    localMissing.Add(parameter);
                }

                if (valid)
                {
                    chosen = ctor;
                    missing = localMissing.Count > 0 ? localMissing : null;
                    nullableDefaults = localNullableDefaults.Count > 0 ? localNullableDefaults : null;
                    break;
                }

                if ((missing?.Count ?? int.MaxValue) <= localMissing.Count) continue;
                chosen = ctor;
                missing = localMissing;
                nullableDefaults = localNullableDefaults.Count > 0 ? localNullableDefaults : null;
            }
            return chosen;
        }

        private static IEnumerable<ParameterData> GetBestConstructorMissing(InjectionData injection,
            ImmutableArray<string> availableInterfaceFullNames)
        {
            HashSet<ParameterData>? missing = null;
            HashSet<ParameterData>? nullableDefaults = null;
            GetBestConstructor(injection, availableInterfaceFullNames, ref missing, ref nullableDefaults);
            return missing ?? Enumerable.Empty<ParameterData>();
        }

        private static string MakeConstructorCall(ConstructorData ctor, HashSet<ParameterData>? missing, HashSet<ParameterData>? nullableDefaults)
        {
            var args = new List<string>();
            foreach (var parameter in ctor.Parameters)
            {
                if (nullableDefaults?.Contains(parameter) == true)
                {
                    args.Add("null");
                    continue;
                }
                if (missing?.Contains(parameter) == true)
                {
                    args.Add(CollectionConstructorArg(parameter));
                    continue;
                }
                args.Add(parameter.TypeMemberName + "()");
            }
            return $"({string.Join(", ", args)})";
        }

        private static string MakeMethodCall(ImmutableArray<ParameterData> parameters, HashSet<ParameterData>? missing, HashSet<ParameterData>? nullableDefaults = null)
        {
            var args = new List<string>();
            foreach (var parameter in parameters)
            {
                if (nullableDefaults?.Contains(parameter) == true)
                {
                    args.Add("null");
                    continue;
                }
                if (missing?.Contains(parameter) == true)
                {
                    args.Add(CollectionConstructorArg(parameter));
                    continue;
                }
                args.Add(parameter.TypeMemberName + "()");
            }
            return $"({string.Join(", ", args)})";
        }

        /// <summary>
        /// Returns the expression to use when passing a collection (or plain-missing) parameter
        /// in a generated constructor call. Collection params are converted from the cached
        /// IEnumerable&lt;T&gt; factory to the exact type requested.
        /// </summary>
        private static string CollectionConstructorArg(ParameterData parameter) =>
            parameter.CollectionKind switch
            {
                CollectionKind.Array        => $"{parameter.Name}.ToArray()",
                CollectionKind.List         => $"{parameter.Name}.ToList()",
                CollectionKind.ImmutableArray => $"ImmutableArray.CreateRange({parameter.Name})",
                CollectionKind.ReadOnlySpan => $"new global::System.ReadOnlySpan<{parameter.CollectionElementFullName}>({parameter.Name}.ToArray())",
                _                           => parameter.Name, // Enumerable or plain missing → use name directly
            };

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