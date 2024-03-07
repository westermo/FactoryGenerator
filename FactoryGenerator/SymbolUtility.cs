using System.Collections.Generic;
using System.Linq;
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
                        yield return type;
                        break;
                }
            }
        }
        internal static bool IsEnumerable(ITypeSymbol symbol)
        {
            return symbol.Name.Contains("IEnumerable") || ImplementsInterface(symbol, "IEnumerable");
        }

        private static bool ImplementsInterface(ITypeSymbol type, string interfaceName)
        {
            return type.AllInterfaces.Any(iface => iface.ToString().Contains(interfaceName));
        }

        public static string MemberName(ISymbol? type)
        {
            return type is null
                       ? "null!"
                       : type.ToString()
                             .Replace('.', '_')
                             .Replace('<', '_')
                             .Replace('>', '_')
                             .Replace("?", "")
                             .Replace(',', '_')
                             .Replace(" ", "") + "()";
        }

        public static string SingletonFactory(INamedTypeSymbol type, string name, string lazyName, string creation)
        {
            return $@"
    private {type} {name}
    {{
        if ({lazyName} != null)
            return {lazyName};
    
        lock (m_lock)
        {{
            if ({lazyName} != null)
                return {lazyName};
            return {lazyName} = {creation};
        }}
    }} 
    private {type}? {lazyName};";
        }
    }
}