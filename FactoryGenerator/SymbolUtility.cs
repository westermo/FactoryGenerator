using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;

namespace FactoryGenerator
{
    public static class SymbolUtility
    {
        public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol root)
        {
            foreach (var namespaceOrTypeSymbol in root.GetMembers())
            {
                switch (namespaceOrTypeSymbol)
                {
                    case INamespaceSymbol @namespace:
                    {
                        foreach (var nested in GetAllTypes(@namespace))
                            yield return nested;
                        break;
                    }
                    case INamedTypeSymbol type:

                        foreach (var nested in GetAllTypes(type))
                            yield return nested;
                        yield return type;
                        break;
                }
            }
        }

        public static IEnumerable<INamedTypeSymbol> GetAllTypes(INamedTypeSymbol root)
        {
            foreach (var namespaceOrTypeSymbol in root.GetMembers())
            {
                switch (namespaceOrTypeSymbol)
                {
                    case INamespaceSymbol @namespace:
                    {
                        foreach (var nested in GetAllTypes(@namespace))
                            yield return nested;
                        break;
                    }
                    case INamedTypeSymbol type:

                        foreach (var nested in GetAllTypes(type))
                            yield return nested;
                        yield return type;
                        break;
                }
            }
        }

        internal static bool IsEnumerable(ITypeSymbol symbol) =>
            GetCollectionKind(symbol) == CollectionKind.Enumerable;

        internal static CollectionKind GetCollectionKind(ITypeSymbol symbol)
        {
            if (symbol is IArrayTypeSymbol) return CollectionKind.Array;

            if (symbol.SpecialType == SpecialType.System_Collections_IEnumerable)
                return CollectionKind.Enumerable;

            if (symbol is INamedTypeSymbol named)
            {
                var fullName = named.ConstructedFrom.ToDisplayString();
                switch (fullName)
                {
                    case "System.Collections.Generic.IEnumerable<T>": return CollectionKind.Enumerable;
                    case "System.Collections.Generic.List<T>":        return CollectionKind.List;
                    case "System.Collections.Immutable.ImmutableArray<T>": return CollectionKind.ImmutableArray;
                    case "System.ReadOnlySpan<T>":                    return CollectionKind.ReadOnlySpan;
                }
            }

            if (symbol.Name == "IEnumerable" && symbol.ContainingNamespace?.ToDisplayString() is
                    "System.Collections.Generic" or "System.Collections")
                return CollectionKind.Enumerable;

            return CollectionKind.None;
        }

        public static string MemberName(ISymbol? type)
        {
            if (type is null) return "null!";
            var raw = type.ToString()!;
            var sb = new StringBuilder(raw.Length + 2);
            foreach (var c in raw)
            {
                switch (c)
                {
                    case '.': sb.Append('_'); break;
                    case '<': sb.Append('_'); break;
                    case '>': sb.Append('_'); break;
                    case '?': break;
                    case ',': sb.Append('_'); break;
                    case ' ': break;
                    default:  sb.Append(c); break;
                }
            }
            sb.Append("()");
            return sb.ToString();
        }

        /// <summary>
        /// Generates a lazily-initialized, double-checked-lock singleton/scoped factory member.
        /// </summary>
        /// <param name="forwardToOwner">
        /// True only for genuine <c>[Singleton]</c> injections (never <c>[Scoped]</c>). When set, the
        /// member first checks <c>m_singletonOwner</c> — which is <c>this</c> for a root container and
        /// the original root for a <c>LifetimeScope</c> (see <see cref="FactoryGenerator.Constructor"/>)
        /// — and forwards to it if it isn't <c>this</c>. This is what makes a single generated member
        /// declaration (shared by <c>DependencyInjectionContainer</c> and its <c>LifetimeScope</c>
        /// subclass) resolve to one shared singleton instance across every scope, without needing a
        /// separate forwarding declaration duplicated into the scope class.
        /// </param>
        public static string SingletonFactory(string typeName, string name, string lazyName, string creation, bool disposable, bool forwardToOwner)
        {
            var ownerForward = forwardToOwner
                ? $@"
        if (m_singletonOwner != this)
            return m_singletonOwner.{name};
    "
                : string.Empty;

            if (disposable)
            {
                return $@"
    internal {typeName} {name}
    {{{ownerForward}
        var cached = {lazyName};
        if (cached != null)
            return cached;
    
        lock (m_lock)
        {{
            cached = {lazyName};
            if (cached != null)
                return cached;
            var value = {creation};
            TrackResolvedInstance(value);
            {lazyName} = value;
            return value;
        }}
    }} 
    internal volatile {typeName}? {lazyName};";
            }

            return $@"
    internal {typeName} {name}
    {{{ownerForward}
        var cached = {lazyName};
        if (cached != null)
            return cached;
    
        lock (m_lock)
        {{
            cached = {lazyName};
            if (cached != null)
                return cached;
            return {lazyName} = {creation};
        }}
    }} 
    internal volatile {typeName}? {lazyName};";
        }

        internal static string DisposableFactory(string typeName, string name, string creationCall)
        {
            return $@"
    internal {typeName} {name}
    {{    
        var value = {creationCall};
        TrackResolvedInstance(value);
        return value;
    }}";
        }
    }
}