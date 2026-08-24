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
    /// Discovers candidate types for injection. Scopes the scan to the small set of assemblies
    /// that can actually declare a FactoryGenerator attribute, instead of the compilation's full
    /// merged global namespace (which otherwise includes the entire referenced BCL).
    /// </summary>
    public partial class FactoryGenerator
    {
        /// <summary>
        /// The name of the assembly that declares the FactoryGenerator marker attributes
        /// (<c>[Inject]</c>, <c>[Singleton]</c>, ...). Applying any of those attributes to a type,
        /// method, or property requires the containing assembly to (directly or transitively)
        /// reference this assembly, which lets us narrow the type-discovery scan below.
        /// </summary>
        private const string AttributesAssemblyName = "FactoryGenerator.Attributes";

        /// <summary>
        /// Scope used by <see cref="FindMethods"/> to discover candidate types. When
        /// <see cref="RelevantAssemblies"/> is non-empty, only those assemblies' own type trees are
        /// scanned. Otherwise (the <see cref="AttributesAssemblyName"/> assembly could not be located
        /// in the reference graph) we conservatively fall back to <see cref="GlobalNamespace"/>, which
        /// mirrors the original, unscoped behavior.
        /// </summary>
        private readonly struct InjectionScanScope
        {
            public InjectionScanScope(INamespaceSymbol globalNamespace, ImmutableArray<IAssemblySymbol> relevantAssemblies)
            {
                GlobalNamespace = globalNamespace;
                RelevantAssemblies = relevantAssemblies;
            }

            public INamespaceSymbol GlobalNamespace { get; }
            public ImmutableArray<IAssemblySymbol> RelevantAssemblies { get; }
        }

        private static InjectionScanScope GetInjectionScanScope(Compilation compilation, CancellationToken token)
        {
            return new InjectionScanScope(compilation.GlobalNamespace, GetRelevantAssemblies(compilation, token));
        }

        /// <summary>
        /// A real-world compilation typically references the entire BCL (hundreds of assemblies,
        /// tens of thousands of types) via <c>compilation.GlobalNamespace</c>, yet only assemblies that
        /// can (transitively) reach <see cref="AttributesAssemblyName"/> are able to declare any
        /// FactoryGenerator attribute at all — applying the attribute requires a reference to its
        /// declaring assembly. We compute that small "relevant" subset up front so <see cref="FindMethods"/>
        /// can scan each relevant assembly's own (unmerged) type tree instead of the merged global one.
        /// </summary>
        private static ImmutableArray<IAssemblySymbol> GetRelevantAssemblies(Compilation compilation, CancellationToken token)
        {
            var allReachableAssemblies = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default) { compilation.Assembly };
            // Captured once per assembly during the BFS below and reused by CanReachAttributesAssembly,
            // instead of calling GetReferencedAssemblies a second time per assembly during that check.
            var references = new Dictionary<IAssemblySymbol, IAssemblySymbol[]>(SymbolEqualityComparer.Default);
            var toVisit = new Queue<IAssemblySymbol>();
            toVisit.Enqueue(compilation.Assembly);
            while (toVisit.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var current = toVisit.Dequeue();
                var directReferences = GetReferencedAssemblies(current).ToArray();
                references[current] = directReferences;
                foreach (var referenced in directReferences)
                {
                    if (allReachableAssemblies.Add(referenced))
                        toVisit.Enqueue(referenced);
                }
            }

            var attributesAssemblies = allReachableAssemblies.Where(assembly => assembly.Name == AttributesAssemblyName).ToArray();
            if (attributesAssemblies.Length == 0)
                return ImmutableArray<IAssemblySymbol>.Empty; // Signals FindMethods to fall back to the unscoped scan.

            var canReachAttributes = new Dictionary<IAssemblySymbol, bool>(SymbolEqualityComparer.Default);

            bool CanReachAttributesAssembly(IAssemblySymbol assembly)
            {
                if (canReachAttributes.TryGetValue(assembly, out var known))
                    return known;

                canReachAttributes[assembly] = false; // Guards against re-entrancy; reference graphs are acyclic anyway.
                if (Array.IndexOf(attributesAssemblies, assembly) >= 0)
                    return canReachAttributes[assembly] = true;

                foreach (var referenced in references[assembly])
                {
                    if (CanReachAttributesAssembly(referenced))
                        return canReachAttributes[assembly] = true;
                }

                return false;
            }

            var relevant = ImmutableArray.CreateBuilder<IAssemblySymbol>();
            foreach (var assembly in allReachableAssemblies)
            {
                token.ThrowIfCancellationRequested();
                if (CanReachAttributesAssembly(assembly))
                    relevant.Add(assembly);
            }

            return relevant.ToImmutable();
        }

        private static IEnumerable<INamedTypeSymbol> GetCandidateTypes(InjectionScanScope scope, CancellationToken token)
        {
            if (scope.RelevantAssemblies.IsDefaultOrEmpty)
                return SymbolUtility.GetAllTypes(scope.GlobalNamespace);

            return scope.RelevantAssemblies.SelectMany(assembly =>
            {
                token.ThrowIfCancellationRequested();
                return SymbolUtility.GetAllTypes(assembly.GlobalNamespace);
            });
        }

        private static IEnumerable<InjectionData> FindMethods(InjectionScanScope scope, CancellationToken token)
        {
            foreach (var type in GetCandidateTypes(scope, token))
            {
                token.ThrowIfCancellationRequested();
                if (type.TypeKind != TypeKind.Class && type.TypeKind != TypeKind.Interface) continue;

                // Cheap, allocation-free match check first: the vast majority of scanned types have
                // no FactoryGenerator attribute at all (directly or via an implemented interface), so
                // avoid building the concatenated attribute array (Concat + ToImmutableArray) unless
                // the type actually matches.
                if (HasInjectionAttribute(type))
                {
                    var typeAttributes = type.GetAttributes().Concat(type.AllInterfaces.SelectMany(i => i.GetAttributes()))
                                             .ToImmutableArray();
                    var info = Injection.Create(type, typeAttributes, token);
                    if (info is not null) yield return info;
                }

                // Single pass over the member list instead of two separate GetMembers().OfType<>().Where()
                // LINQ chains. Matches are buffered per member kind and yielded methods-then-properties
                // below to preserve today's exact discovery order (positional boolean constructor
                // parameters depend on it).
                List<(IMethodSymbol Method, ImmutableArray<AttributeData> Attributes)>? methodMatches = null;
                List<(IPropertySymbol Property, ImmutableArray<AttributeData> Attributes)>? propertyMatches = null;
                foreach (var member in type.GetMembers())
                {
                    if (member.DeclaredAccessibility != Accessibility.Public) continue;
                    switch (member)
                    {
                        case IMethodSymbol method:
                        {
                            var attributes = method.GetAttributes();
                            if (attributes.Any(IsInjection))
                                (methodMatches ??= new List<(IMethodSymbol, ImmutableArray<AttributeData>)>()).Add((method, attributes));
                            break;
                        }
                        case IPropertySymbol property:
                        {
                            var attributes = property.GetAttributes();
                            if (attributes.Any(IsInjection))
                                (propertyMatches ??= new List<(IPropertySymbol, ImmutableArray<AttributeData>)>()).Add((property, attributes));
                            break;
                        }
                    }
                }

                if (methodMatches is not null)
                {
                    foreach (var (method, attributes) in methodMatches)
                    {
                        var info = Injection.Create(method, attributes, token);
                        if (info is not null) yield return info;
                    }
                }

                if (propertyMatches is not null)
                {
                    foreach (var (property, attributes) in propertyMatches)
                    {
                        var info = Injection.Create(property, attributes, token);
                        if (info is not null) yield return info;
                    }
                }
            }

            bool IsInjection(AttributeData attribute)
            {
                return attribute.AttributeClass?.Name.Contains("Inject") == true && attribute.AttributeClass.ToString().StartsWith("FactoryGenerator.Attributes");
            }

            bool HasInjectionAttribute(INamedTypeSymbol type)
            {
                foreach (var attribute in type.GetAttributes())
                {
                    if (IsInjection(attribute)) return true;
                }

                foreach (var iface in type.AllInterfaces)
                {
                    foreach (var attribute in iface.GetAttributes())
                    {
                        if (IsInjection(attribute)) return true;
                    }
                }

                return false;
            }
        }
    }
}
