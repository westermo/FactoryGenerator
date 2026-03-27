using System;
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

        internal static bool IsEnumerable(ITypeSymbol symbol)
        {
            if (symbol.SpecialType == SpecialType.System_Collections_IEnumerable) return true;
            if (symbol is INamedTypeSymbol named)
            {
                var fullName = named.ConstructedFrom.ToDisplayString();
                if (fullName == "System.Collections.Generic.IEnumerable<T>") return true;
            }
            return symbol.Name == "IEnumerable" && symbol.ContainingNamespace?.ToDisplayString() is
                "System.Collections.Generic" or "System.Collections";
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

        public static string SingletonFactory(string typeName, string name, string lazyName, string creation, bool disposable)
        {
            if (disposable)
            {
                return $@"
    internal {typeName} {name}
    {{
        var cached = {lazyName};
        if (cached != null)
            return cached;
    
        lock (m_lock)
        {{
            cached = {lazyName};
            if (cached != null)
                return cached;
            var value = {creation};
            GetResolvedInstances().Add(new WeakReference<IDisposable>(value));
            {lazyName} = value;
            return value;
        }}
    }} 
    internal volatile {typeName}? {lazyName};";
            }

            return $@"
    internal {typeName} {name}
    {{
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
        GetResolvedInstances().Add(new WeakReference<IDisposable>(value));
        return value;
    }}";
        }
    }
}