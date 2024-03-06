using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FactoryGenerator
{
    [Generator]
    public class FactoryGenerator : IIncrementalGenerator
    {
        private const string ToolName = nameof(FactoryGenerator);
        private const string Version = "1.0.0";

        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            var references = context.CompilationProvider.Select(GetGlobalNamespace);
            var rest = references.SelectMany(FindMethods);
            var attributes = rest.Collect();
            var compilation = context.CompilationProvider;
            var syntaxUsages = context.SyntaxProvider.CreateSyntaxProvider(ResolveSymbols, ResolveTransformations)
                                      .Collect();
            var combined = attributes.Combine(compilation).Combine(syntaxUsages);
            context.RegisterSourceOutput(combined, MakeAutofacModule);
        }

        private void MakeAutofacModule(SourceProductionContext context,
                                       ((ImmutableArray<Injection> Injections, Compilation Compilation) Left, ImmutableArray<INamedTypeSymbol?>
                                           CompileTimeResolvedTypes) data)
        {
            var injections = data.Left.Injections;
            var compilation = data.Left.Compilation;
            var usages = data.CompileTimeResolvedTypes;

            var source = GenerateCode(injections, compilation, usages).ToArray();
            context.AddSource("DependencyInjectionContainer.Lookup.g.cs", source[0]);
            context.AddSource("DependencyInjectionContainer.Constructor.g.cs", source[1]);
            context.AddSource("DependencyInjectionContainer.Declarations.g.cs", source[2]);
            context.AddSource("DependencyInjectionContainer.EnumerableDeclarations.g.cs", source[3]);
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

        private static IEnumerable<string> GenerateCode(ImmutableArray<Injection> dataInjections,
                                                        Compilation compilation, ImmutableArray<INamedTypeSymbol?> usages)
        {
            var usingStatements = $@"
using System;
using System.Collections.Generic;
using FactoryGenerator;
using System.CodeDom.Compiler;
namespace {compilation.Assembly.Name}.Generated;
#nullable enable";

            yield return $@"{usingStatements}
[GeneratedCode(""{ToolName}"", ""{Version}"")]
public partial class DependencyInjectionContainer : IContainer
{{
    public T Resolve<T>()
    {{
        return (T)Resolve(typeof(T));
    }}
    public object Resolve(Type type)
    {{
        return m_lookup[type]();   
    }}
    private Dictionary<Type,Func<object>> m_lookup;
    private object m_lock = new();
}}";
            var booleans = dataInjections.Select(inj => inj.BooleanInjection).Where(b => b is not null)
                                         .Select(b => b!.Key).Distinct();
            var allArguments = booleans.Select(b => $"bool {b}").ToList();
            var ordered = dataInjections.Reverse().ToList();
            //Put all test-overrides at the end
            foreach (var injection in ordered.ToArray())
            {
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
            var availableInterfaces = interfaceInjectors.Keys.ToImmutableArray();
            var constructorParameters = new List<IParameterSymbol>();

            foreach (var injection in ordered)
            {
                declarations[injection.Name] = injection.Declaration(availableInterfaces);
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
                            declarations[SymbolUtility.MemberName(interfaceSymbol)] =
                                $"private {interfaceSymbol} {SymbolUtility.MemberName(interfaceSymbol)} => {chosen.Name};";
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
                        if (key == last)
                        {
                            ternary.Append($"{key} ? {trueValue?.Name ?? "null!"} : {fallback?.Name ?? "null!"}");
                        }
                        else
                        {
                            ternary.Append($"{key} ? {trueValue?.Name ?? "null!"} : ");
                        }
                    }

                    if (!declarations.ContainsKey(SymbolUtility.MemberName(interfaceSymbol)))
                        declarations[SymbolUtility.MemberName(interfaceSymbol)] =
                            $"private {interfaceSymbol} {SymbolUtility.MemberName(interfaceSymbol)} => {ternary};";
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
                MakeArray(arrayDeclarations, name, type, interfaceInjectors);
                constructorParameters.Remove(parameter);
                localizedParameters.Add(parameter);
            }

            foreach (var parameter in constructorParameters.ToArray())
            {
                if (parameter.Type is not IArrayTypeSymbol array) continue;
                if (array.ElementType is not INamedTypeSymbol type) continue;
                var name = parameter.Name;
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
                var type = (INamedTypeSymbol) request.TypeArguments[0];
                var name = SymbolUtility.MemberName(request).Replace("()", "");

                MakeArray(arrayDeclarations, name, type, interfaceInjectors, true);
                requested.Add(request);
            }


            foreach (var parameter in constructorParameters.ToArray())
            {
                if (parameter.Type.Name.Contains("IContainer"))
                {
                    declarations[parameter.Name] = $"private ILifetimeScope {parameter.Name} => this;";
                    constructorParameters.Remove(parameter);
                }
            }

            var arguments = constructorParameters.OrderBy(p => p.Type.ToString()).Select(p => $"{p.Type} {p.Name}")
                                                 .Distinct();
            allArguments.AddRange(arguments);

            var constructor = "(" + string.Join(", ", allArguments) + ")";
            var constructorFields = string.Join("\n\t", allArguments.Select(arg => arg + ";"));
            var constructorAssignments = string.Join("\n\t\t",
                                                     allArguments.Select(arg => arg.Split(' ').Last()).Select(arg => $"this.{arg} = {arg};"));
            var dictSize = interfaceInjectors.Count + localizedParameters.Count + requested.Count +
                           constructorParameters.Count;
            yield return Constructor(usingStatements, constructorFields, constructor, constructorAssignments, dictSize, interfaceInjectors, localizedParameters, requested, constructorParameters);
            yield return Declarations(usingStatements, declarations);
            yield return ArrayDeclarations(usingStatements, arrayDeclarations);
        }

        private static string Constructor(string usingStatements, string constructorFields, string constructor, string constructorAssignments, int dictSize,
                                          Dictionary<INamedTypeSymbol, List<Injection>> interfaceInjectors, List<IParameterSymbol> localizedParameters, List<INamedTypeSymbol> requested,
                                          List<IParameterSymbol> constructorParameters)
        {
            return $@"{usingStatements}
public partial class DependencyInjectionContainer
{{
    {constructorFields}
    public DependencyInjectionContainer{constructor}
    {{
        {constructorAssignments}
        m_lookup = new({dictSize})
        {{
{MakeDictionary(interfaceInjectors.Keys)}
{MakeDictionary(localizedParameters)}
{MakeDictionary(requested)}
{MakeDictionary(constructorParameters)}
        }};
    }}
}}";
        }

        private static string ArrayDeclarations(string usingStatements, Dictionary<string, string> arrayDeclarations)
        {
            return $@"{usingStatements}
public partial class DependencyInjectionContainer
{{
    {string.Join("\n\t", arrayDeclarations.Values)}
}}";
        }

        private static string Declarations(string usingStatements, Dictionary<string, string> declarations)
        {
            return $@"{usingStatements}
public partial class DependencyInjectionContainer
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

            var functor = function ? "()" : "";
            declarations[name] = $@"
    private {type}[]? m_{name}; 
    private {type}[] {name}{functor} => m_{name} ??= {factoryName};
{factory}";
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