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
                                       (((ImmutableArray<Injection> Injections, Compilation Compilation) Left, ImmutableArray<INamedTypeSymbol?> CompileTimeResolvedTypes) Left, LoggingOptions? log)
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

        private INamedTypeSymbol? ResolveTransformations(GeneratorSyntaxContext context, CancellationToken token)
        {
            var typeArguments = context.Node.DescendantNodes().OfType<TypeArgumentListSyntax>().FirstOrDefault();
            if (typeArguments is null) return null;
            var identifier = typeArguments.DescendantNodes().FirstOrDefault();
            if (identifier is null) return null;
            var info = context.SemanticModel.GetSymbolInfo(identifier, token);
            var symbol = (INamedTypeSymbol?) info.Symbol;
            return symbol;
        }

        private bool ResolveSymbols(SyntaxNode node, CancellationToken token)
        {
            if (node is not MemberAccessExpressionSyntax invocation) return false;
            return invocation.ToString().Contains("Resolve");
        }

        private static IEnumerable<Injection> FindMethods(INamespaceSymbol namespaceSymbol, CancellationToken token)
        {
            foreach (var type in SymbolUtility.GetAllTypes(namespaceSymbol))
            {
                token.ThrowIfCancellationRequested();
                if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Interface) continue;
                var typeAttributes = type.GetAttributes().Concat(type.AllInterfaces.SelectMany(i => i.GetAttributes()))
                                         .ToImmutableArray();
                if (typeAttributes.Any(attribute => attribute.AttributeClass?.Name.Contains("Inject") != false))
                {
                    var info = Injection.Create(type, typeAttributes, token);
                    if (info is not null) yield return info;
                }

                foreach (var method in type.GetMembers().OfType<IMethodSymbol>()
                                           .Where(method => method.DeclaredAccessibility == Accessibility.Public))
                {
                    var attributes = method.GetAttributes();
                    if (!attributes.Any(attribute => attribute.AttributeClass?.Name.Contains("Inject") != false))
                        continue;
                    var info = Injection.Create(method, attributes, token);
                    if (info is not null) yield return info;
                }

                foreach (var property in type.GetMembers().OfType<IPropertySymbol>()
                                             .Where(property => property.DeclaredAccessibility == Accessibility.Public))
                {
                    var attributes = property.GetAttributes();
                    if (!attributes.Any(attribute => attribute.AttributeClass?.Name.Contains("Inject") != false))
                        continue;
                    var info = Injection.Create(property, attributes, token);
                    if (info is not null) yield return info;
                }
            }
        }

        private static INamespaceSymbol GetGlobalNamespace(Compilation compilation, CancellationToken token)
        {
            return compilation.GlobalNamespace;
        }

        private const string ClassName = "DependencyInjectionContainer";
        private const string LifetimeName = "LifetimeScope";

        private static IEnumerable<string> GenerateCode(ImmutableArray<Injection> dataInjections,
                                                        Compilation compilation, ImmutableArray<INamedTypeSymbol?> usages, ILogger log)
        {
            CheckForCycles(dataInjections);
            log.Log(LogLevel.Debug, "Starting Code Generation");
            var usingStatements = $@"
using System;
using System.Collections.Generic;
using FactoryGenerator;
using System.CodeDom.Compiler;
namespace {compilation.Assembly.Name}.Generated;
#nullable enable";

            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable disable
public sealed partial class {ClassName} : IContainer
{{
    private object m_lock = new();
    private Dictionary<Type,Func<object>> m_lookup;
    private readonly List<WeakReference<IDisposable>> resolvedInstances = new();

    public T Resolve<T>()
    {{
        return (T)Resolve(typeof(T));
    }}

    public object Resolve(Type type)
    {{
        var instance = m_lookup[type]();
        return instance;
    }}

    public void Dispose()
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

    public bool TryResolve(Type type, out object resolved)
    {{
        if(m_lookup.TryGetValue(type, out var factory))
        {{
            resolved = factory();
            return true;
        }}
        resolved = default;
        return false;
    }}


    public bool TryResolve<T>(out T resolved)
    {{
        if(m_lookup.TryGetValue(typeof(T), out var factory))
        {{
            var value = factory();
            if(value is T t)
            {{
                resolved = t;
                return true;
            }}
        }}
        resolved = default;
        return false;
    }}
}}";

            var booleans = dataInjections.Select(inj => inj.BooleanInjection).Where(b => b is not null)
                                         .Select(b => b!.Key).Distinct().ToArray();
            var allArguments = booleans.Select(b => $"bool {b}").ToList();
            var allParameters = booleans.Select(b => $"{b}").ToList();
            var ordered = dataInjections.Reverse().ToList();
            //Put all test-overrides at the end
            foreach (var injection in ordered.ToArray())
            {
                log.Log(LogLevel.Debug, $"Traversing {injection.Name}");
                if (!injection.Type.ToString().Contains("Test")) continue;
                ordered.Remove(injection); //Remove it from current position
                ordered.Add(injection); //Add it to the end
            }

            //Get the relevant implementation that'll actually be injected
            var interfaceInjectors = new Dictionary<INamedTypeSymbol, List<Injection>>(SymbolEqualityComparer.Default);
            foreach (var injection in ordered)
            {
                foreach (var injectedInterface in injection.Interfaces)
                {
                    if (!interfaceInjectors.ContainsKey(injectedInterface))
                    {
                        interfaceInjectors[injectedInterface] = new List<Injection>();
                    }

                    interfaceInjectors[injectedInterface].Add(injection);
                }
            }

            var declarations = new Dictionary<string, string>();
            var scopedDeclarations = new Dictionary<string, string>();
            var availableInterfaces = interfaceInjectors.Keys.ToImmutableArray();
            var constructorParameters = new List<IParameterSymbol>();

            foreach (var injection in ordered)
            {
                declarations[injection.Name] = injection.Declaration(availableInterfaces, false);
                scopedDeclarations[injection.Name] = injection.Declaration(availableInterfaces, true);

                HashSet<IParameterSymbol>? missing = null;
                injection.GetBestConstructor(availableInterfaces, ref missing);
                if (missing is null) continue;
                constructorParameters.AddRange(missing.Where(parameter =>
                                                                 constructorParameters.All(p => p.ToString() != parameter.ToString())));
            }

            foreach (var interfaceSymbol in interfaceInjectors.Keys)
            {
                var possibilities = interfaceInjectors[interfaceSymbol];
                if (possibilities.All(i => i.BooleanInjection == null))
                {
                    var chosen = possibilities.Last();

                    if (SymbolUtility.MemberName(interfaceSymbol) != chosen.Name)
                    {
                        if (!declarations.ContainsKey(SymbolUtility.MemberName(interfaceSymbol)))
                        {
                            log.Log(LogLevel.Information, $"Selecting {chosen.Name} for {interfaceSymbol}");
                            declarations[SymbolUtility.MemberName(interfaceSymbol)] =
                                $"internal {interfaceSymbol} {SymbolUtility.MemberName(interfaceSymbol)} => {chosen.Name};";
                            scopedDeclarations[SymbolUtility.MemberName(interfaceSymbol)] =
                                $"internal {interfaceSymbol} {SymbolUtility.MemberName(interfaceSymbol)} => {chosen.Name};";
                        }
                    }
                }
                else
                {
                    var keys = possibilities.Select(i => i.BooleanInjection?.Key).OfType<string>().ToArray().Distinct()
                                            .Reverse().ToArray();
                    var fallback = possibilities.LastOrDefault(p => p.BooleanInjection == null);
                    var last = keys.Last();
                    var ternary = new StringBuilder();
                    foreach (var key in keys)
                    {
                        var trueValue = possibilities.LastOrDefault(p =>
                                                                        p.BooleanInjection?.Value == true && p.BooleanInjection?.Key == key);
                        trueValue ??= fallback;
                        ternary.Append(key == last ? $"{key} ? {trueValue?.Name ?? "null!"} : {fallback?.Name ?? "null!"}" : $"{key} ? {trueValue?.Name ?? "null!"} : ");
                    }

                    if (!declarations.ContainsKey(SymbolUtility.MemberName(interfaceSymbol)))
                    {
                        log.Log(LogLevel.Information, $"Selecting {ternary} for {interfaceSymbol}");
                        declarations[SymbolUtility.MemberName(interfaceSymbol)] =
                            $"internal {interfaceSymbol} {SymbolUtility.MemberName(interfaceSymbol)} => {ternary};";
                        scopedDeclarations[SymbolUtility.MemberName(interfaceSymbol)] =
                            $"internal {interfaceSymbol} {SymbolUtility.MemberName(interfaceSymbol)} => {ternary};";
                    }
                }
            }

            var localizedParameters = new List<IParameterSymbol>();

            var arrayDeclarations = new Dictionary<string, string>();
            foreach (var parameter in constructorParameters.ToArray())
            {
                if (!SymbolUtility.IsEnumerable(parameter.Type)) continue;
                if (parameter.Type is not INamedTypeSymbol named) continue;
                if (named.TypeArguments.Length != 1) continue;
                var type = (INamedTypeSymbol) named.TypeArguments[0];
                var name = parameter.Name;
                log.Log(LogLevel.Debug, $"Creating Array: {name} of type {type}[]");
                MakeArray(arrayDeclarations, name, type, interfaceInjectors);
                constructorParameters.Remove(parameter);
                localizedParameters.Add(parameter);
            }

            foreach (var parameter in constructorParameters.ToArray())
            {
                if (parameter.Type is not IArrayTypeSymbol array) continue;
                if (array.ElementType is not INamedTypeSymbol type) continue;
                var name = parameter.Name;
                log.Log(LogLevel.Debug, $"Creating Array: {name} of type {type}[]");
                MakeArray(arrayDeclarations, name, type, interfaceInjectors);

                constructorParameters.Remove(parameter);
                localizedParameters.Add(parameter);
            }

            var requested = new List<INamedTypeSymbol>();

            foreach (var request in usages)
            {
                if (localizedParameters.Any(p => p.Type.Equals(request, SymbolEqualityComparer.Default))) continue;
                if (request is null) continue;
                if (!SymbolUtility.IsEnumerable(request)) continue;
                if (request.TypeArguments.Length != 1) continue;
                log.Log(LogLevel.Information, $"Creating Requested: {request}");
                var type = (INamedTypeSymbol) request.TypeArguments[0];
                var name = SymbolUtility.MemberName(request).Replace("()", "");
                log.Log(LogLevel.Debug, $"Creating Array: {name} of type {type}[]");

                MakeArray(arrayDeclarations, name, type, interfaceInjectors, true);
                requested.Add(request);
            }


            foreach (var parameter in constructorParameters.ToArray())
            {
                if (parameter.Type.Name.Contains("IContainer"))
                {
                    log.Log(LogLevel.Debug, $"Registering {parameter.Name} as Self");
                    declarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                    scopedDeclarations[parameter.Name] = $"private IContainer {parameter.Name} => this;";
                    constructorParameters.Remove(parameter);
                }
            }

            var arguments = constructorParameters.OrderBy(p => p.Type.ToString()).Select(p => $"{p.Type} {p.Name}")
                                                 .Distinct();

            var parameters = constructorParameters.OrderBy(p => p.Type.ToString()).Select(p => $"{p.Name}")
                                                  .Distinct();
            allArguments.AddRange(arguments);
            allParameters.AddRange(parameters);

            var constructor = "(" + string.Join(", ", allArguments) + ")";
            var lifetimeConstructor = "(" + $"{ClassName} fallback, " + string.Join(", ", allArguments) + ")";
            var lifetimeParameters = "this, " + string.Join(", ", allParameters);

            log.Log(LogLevel.Debug, $"Resulting Constructor: {constructor}");
            var constructorFields = string.Join("\n\t", allArguments.Select(arg => arg + ";"));
            var constructorAssignments = string.Join("\n\t\t",
                                                     allArguments.Select(arg => arg.Split(' ').Last()).Select(arg => $"this.{arg} = {arg};"));
            var dictSize = interfaceInjectors.Count + localizedParameters.Count + requested.Count +
                           constructorParameters.Count;
            yield return Constructor(usingStatements, constructorFields,
                                     constructor, constructorAssignments,
                                     dictSize, interfaceInjectors.Keys,
                                     localizedParameters, requested,
                                     constructorParameters, true, ClassName, lifetimeParameters);
            yield return Declarations(usingStatements, declarations, ClassName);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations, ClassName);
            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
#nullable disable
public sealed partial class LifetimeScope : IContainer
{{
    public ILifetimeScope BeginLifetimeScope()
    {{
        return m_fallback.BeginLifetimeScope();
    }}
    private object m_lock = new();
    private {ClassName} m_fallback;
    private Dictionary<Type,Func<object>> m_lookup;
    private readonly List<WeakReference<IDisposable>> resolvedInstances = new();

   public T Resolve<T>()
    {{
        return (T)Resolve(typeof(T));
    }}

    public object Resolve(Type type)
    {{
        var instance = m_lookup[type]();
        return instance;
    }}

    public void Dispose()
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

    public bool TryResolve(Type type, out object resolved)
    {{
        if(m_lookup.TryGetValue(type, out var factory))
        {{
            resolved = factory();
            return true;
        }}
        resolved = default;
        return false;
    }}


    public bool TryResolve<T>(out T resolved)
    {{
        if(m_lookup.TryGetValue(typeof(T), out var factory))
        {{
            var value = factory();
            if(value is T t)
            {{
                resolved = t;
                return true;
            }}
        }}
        resolved = default;
        return false;
    }}
}}
";
            yield return Constructor(usingStatements, constructorFields,
                                     lifetimeConstructor, constructorAssignments,
                                     dictSize, interfaceInjectors.Keys,
                                     localizedParameters, requested,
                                     constructorParameters, false, LifetimeName);
            yield return Declarations(usingStatements, scopedDeclarations, LifetimeName);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations, LifetimeName);
        }

        private static void CheckForCycles(ImmutableArray<Injection> dataInjections)
        {
            var tree = new Dictionary<INamedTypeSymbol, List<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
            foreach (var injection in dataInjections)
            {
                foreach (var iface in injection.Interfaces)
                {
                    if (!tree.ContainsKey(iface))
                    {
                        tree[iface] = new List<INamedTypeSymbol>();
                    }
                }

                foreach (var constructor in injection.Type.Constructors)
                {
                    foreach (var parameter in constructor.Parameters)
                    {
                        if (parameter.Type is INamedTypeSymbol named)
                        {
                            if (SymbolUtility.IsEnumerable(parameter.Type))
                            {
                                if (named.TypeArguments.Length != 1) continue;
                                named = (INamedTypeSymbol) named.TypeArguments[0];
                            }

                            foreach (var iface in injection.Interfaces)
                            {
                                tree[iface].Add(named);
                            }

                            if (tree.TryGetValue(named, out var list))
                            {
                                foreach (var iface in injection.Interfaces)
                                {
                                    if (list.Contains(iface)) throw new InvalidOperationException($"Cyclic Dependency Detected between {injection.Type} and {iface}");
                                }
                            }
                        }

                        if (parameter.Type is IArrayTypeSymbol array)
                        {
                            if (array.ElementType is INamedTypeSymbol arrType)
                            {
                                tree[injection.Type].Add(arrType);
                                if (tree.TryGetValue(arrType, out var list))
                                {
                                    foreach (var iface in injection.Interfaces)
                                    {
                                        if (list.Contains(iface)) throw new InvalidOperationException($"Cyclic Dependency Detected between {injection.Type} and {iface}");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private static string Constructor(string usingStatements, string constructorFields, string constructor, string constructorAssignments, int dictSize,
                                          IEnumerable<INamedTypeSymbol> interfaceInjectors, List<IParameterSymbol> localizedParameters, List<INamedTypeSymbol> requested,
                                          List<IParameterSymbol> constructorParameters, bool addLifetimeScopeFunction, string className, string? lifetimeParameters = null)
        {
            var lifetimeScopeFunction = addLifetimeScopeFunction
                                            ? $@"
public ILifetimeScope BeginLifetimeScope()
{{
    return new {LifetimeName}({lifetimeParameters});
}}"
                                            : string.Empty;
            var extraConstruction = addLifetimeScopeFunction ? string.Empty : "m_fallback = fallback;";
            return $@"{usingStatements}
public partial class {className}
{{
    {constructorFields}
    public {className}{constructor}
    {{
        {extraConstruction}
        {constructorAssignments}
        m_lookup = new({dictSize})
        {{
{MakeDictionary(interfaceInjectors)}
{MakeDictionary(localizedParameters)}
{MakeDictionary(requested)}
{MakeDictionary(constructorParameters)}
        }};
    }}

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

        private static void MakeArray(Dictionary<string, string> declarations, string name, INamedTypeSymbol type,
                                      Dictionary<INamedTypeSymbol, List<Injection>> interfaceInjectors, bool function = false)
        {
            var factoryName = "[]";
            var factory = string.Empty;
            if (interfaceInjectors.TryGetValue(type, out var injections))
            {
                factoryName = $"Create{name}()".Replace("_", "");
                factory = @$"
    {type}[] {factoryName}
    {{
        {type}[] source = [{string.Join(", ", injections.Where(i => i.BooleanInjection == null).Select(i => i.Name))}];
        {string.Join("\n\t\t\t", injections.Where(b => b.BooleanInjection != null)
                                           .Select(i => $"if({i.BooleanInjection!.Key}) source = [..source, {i.Name}];"))}
        return source;
    }}";
            }

            if (function)
            {
                declarations[name] = $@"
    internal {type}[] {name}()
    {{
        if (m_{name} != null)
            return m_{name};
    
        lock (m_lock)
        {{
            if (m_{name} != null)
                return m_{name};
            return m_{name} = {factoryName};
        }}
    }} 
    internal {type}[]? m_{name};" + factory;
            }
            else
            {
                declarations[name] = $@"
    internal {type}[] {name}
    {{
        get
        {{
            if (m_{name} != null)
                return m_{name};
        
            lock (m_lock)
            {{
                if (m_{name} != null)
                    return m_{name};
                return m_{name} = {factoryName};
            }}
        }}
    }} 
    internal {type}[]? m_{name};" + factory;
            }
        }

        private static string MakeDictionary(IEnumerable<INamedTypeSymbol> types)
        {
            StringBuilder builder = new();
            foreach (var type in types)
            {
                builder.AppendLine($"\t\t\t{{ typeof({type}),{SymbolUtility.MemberName(type).Replace("()", "")} }},");
            }

            return builder.ToString();
        }

        private static string MakeDictionary(IEnumerable<IParameterSymbol> parameters)
        {
            StringBuilder builder = new();
            foreach (var parameter in parameters)
            {
                builder.AppendLine($"\t\t\t{{ typeof({parameter.Type}), () => {parameter.Name} }},");
            }

            return builder.ToString();
        }
    }
}