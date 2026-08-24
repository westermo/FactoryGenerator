using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace FactoryGenerator
{
    public static class Injection
    {
        public static InjectionData? Create(ISymbol symbol, ImmutableArray<AttributeData> attributes, CancellationToken token)
        {
            INamedTypeSymbol? namedTypeSymbol = null;
            LambdaData? lambdaData = null;

            switch (symbol)
            {
                case INamedTypeSymbol nts:
                    namedTypeSymbol = nts;
                    if (namedTypeSymbol.TypeKind == TypeKind.Interface) return null;
                    if (namedTypeSymbol.IsAbstract) return null;
                    break;
                case IMethodSymbol methodSymbol:
                    namedTypeSymbol = methodSymbol.ReturnType as INamedTypeSymbol;
                    lambdaData = new LambdaData(
                        isMethod: true,
                        containingTypeFullName: methodSymbol.ContainingType.ToString()!,
                        containingTypeMemberName: SymbolUtility.MemberName(methodSymbol.ContainingType),
                        memberName: methodSymbol.Name,
                        methodParameters: methodSymbol.Parameters.Select(ExtractParameter).ToImmutableArray());
                    break;
                case IPropertySymbol propertySymbol:
                    namedTypeSymbol = propertySymbol.Type as INamedTypeSymbol;
                    lambdaData = new LambdaData(
                        isMethod: false,
                        containingTypeFullName: propertySymbol.ContainingType.ToString()!,
                        containingTypeMemberName: SymbolUtility.MemberName(propertySymbol.ContainingType),
                        memberName: propertySymbol.Name,
                        methodParameters: ImmutableArray<ParameterData>.Empty);
                    break;
            }

            if (namedTypeSymbol is null) return null;
            var assembly = symbol.ContainingAssembly ?? namedTypeSymbol.ContainingAssembly;
            var assemblyName = assembly?.Name ?? string.Empty;
            var assemblyPriority = GetAssemblyPriority(assembly);

            var singleInstance = false;
            var acquireChildInterfaces = false;
            var asSelf = namedTypeSymbol.Interfaces.Length == 0;
            var scoped = false;
            if (namedTypeSymbol.TypeKind == TypeKind.Interface)
                asSelf = true;

            BooleanInjection? boolean = null;
            var attributedInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var preventedInterfaces = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var attributeData in attributes)
            {
                token.ThrowIfCancellationRequested();
                var name = attributeData.AttributeClass!.ToString();
                if (!name.StartsWith("FactoryGenerator.Attributes")) continue;
                name = attributeData.AttributeClass.Name;
                switch (name)
                {
                    case "SingletonAttribute":
                        singleInstance = true;
                        break;
                    case "InheritInterfacesAttribute":
                        acquireChildInterfaces = true;
                        break;
                    case "AsAttribute":
                        if (attributeData.AttributeClass!.TypeArguments[0] is INamedTypeSymbol addedNamed)
                            attributedInterfaces.Add(addedNamed);
                        break;
                    case "ExceptAsAttribute":
                        if (attributeData.AttributeClass!.TypeArguments[0] is INamedTypeSymbol removedNamed)
                            preventedInterfaces.Add(removedNamed);
                        break;
                    case "SelfAttribute":
                        asSelf = true;
                        break;
                    case "BooleanAttribute":
                        boolean = HandleBoolean(attributeData);
                        break;
                    case "ScopedAttribute":
                        scoped = true;
                        break;
                    default:
                        continue;
                }
            }

            var isDisposable = namedTypeSymbol.AllInterfaces.Any(i => i.SpecialType == SpecialType.System_IDisposable);
            var isAsyncDisposable = namedTypeSymbol.AllInterfaces.Any(IsAsyncDisposableInterface);

            // Accumulated into a single mutable list rather than chaining Add/AddRange/Remove/
            // RemoveRange calls on an ImmutableArray, each of which would otherwise copy the whole
            // backing array — a list gives amortized O(1) appends and the same O(n) removals
            // (unavoidable either way) without the repeated whole-array copies in between.
            var baseInterfaces = acquireChildInterfaces ? namedTypeSymbol.AllInterfaces : namedTypeSymbol.Interfaces;
            var interfaceList = new List<INamedTypeSymbol>(baseInterfaces.Length + attributedInterfaces.Count + 1);
            interfaceList.AddRange(baseInterfaces);
            if (asSelf)
                interfaceList.Add(namedTypeSymbol);
            interfaceList.AddRange(attributedInterfaces);

            var disposableIface = interfaceList.FirstOrDefault(i => i.SpecialType == SpecialType.System_IDisposable);
            if (disposableIface is not null)
                interfaceList.Remove(disposableIface);
            var asyncDisposableIface = interfaceList.FirstOrDefault(IsAsyncDisposableInterface);
            if (asyncDisposableIface is not null)
                interfaceList.Remove(asyncDisposableIface);

            foreach (var prevented in preventedInterfaces)
                interfaceList.Remove(prevented);

            var interfaces = interfaceList
                .Distinct((IEqualityComparer<INamedTypeSymbol>) SymbolEqualityComparer.Default)
                .ToImmutableArray();

            var ifaceFullNames = interfaces.Select(i => i.ToString()!).ToImmutableArray();
            var ifaceMemberNames = interfaces
                .Select(i => SymbolUtility.MemberName(i).Replace("()", ""))
                .ToImmutableArray();

            var typeMemberName = SymbolUtility.MemberName(namedTypeSymbol).Replace("()", "");

            var constructors = lambdaData is null
                ? namedTypeSymbol.Constructors
                    .Select(c => new ConstructorData(c.Parameters.Select(ExtractParameter).ToImmutableArray()))
                    .ToImmutableArray()
                : ImmutableArray<ConstructorData>.Empty;

            return new InjectionData(
                typeFullName: namedTypeSymbol.ToString()!,
                typeMemberName: typeMemberName,
                assemblyName: assemblyName,
                assemblyPriority: assemblyPriority,
                interfaceFullNames: ifaceFullNames,
                interfaceMemberNames: ifaceMemberNames,
                singleton: singleInstance,
                scoped: scoped,
                disposable: isDisposable,
                asyncDisposable: isAsyncDisposable,
                booleanInjection: boolean,
                constructors: constructors,
                lambda: lambdaData);
        }

        private static ParameterData ExtractParameter(IParameterSymbol parameter)
        {
            var typeFullName = parameter.Type.ToString()!;
            var typeMemberName = SymbolUtility.MemberName(parameter.Type).Replace("()", "");
            var isNullable = parameter.Type.NullableAnnotation == NullableAnnotation.Annotated;

            var collectionKind = SymbolUtility.GetCollectionKind(parameter.Type);
            string? elemFull = null, elemMember = null;
            if (collectionKind != CollectionKind.None)
            {
                ITypeSymbol? elemType = null;
                if (parameter.Type is INamedTypeSymbol namedType && namedType.TypeArguments.Length == 1)
                    elemType = namedType.TypeArguments[0];
                else if (parameter.Type is IArrayTypeSymbol arrType)
                    elemType = arrType.ElementType;

                if (elemType is not null)
                {
                    elemFull = elemType.ToString()!;
                    elemMember = SymbolUtility.MemberName(elemType).Replace("()", "");
                }
            }

            return new ParameterData(
                typeFullName, typeMemberName,
                parameter.HasExplicitDefaultValue, parameter.IsParams, parameter.Name,
                collectionKind, elemFull, elemMember,
                isNullable);
        }

        private static BooleanInjection? HandleBoolean(AttributeData attributeData)
        {
            if (attributeData.ConstructorArguments[0].Value is string key)
                return new BooleanInjection(true, key);
            return null;
        }

        /// <summary>
        /// Checks whether an interface is <c>System.IAsyncDisposable</c> without paying for a full
        /// display-string (<c>ToString()</c>) computation on every interface. Roslyn has no
        /// <see cref="SpecialType"/> entry for it (it postdates the "special type" list), so a name
        /// check first is used to filter out the overwhelming majority of unrelated interfaces before
        /// falling back to the (cheaper, non-generic) containing-namespace comparison.
        /// </summary>
        private static bool IsAsyncDisposableInterface(INamedTypeSymbol i) =>
            i.Name == "IAsyncDisposable" && i.ContainingNamespace?.ToDisplayString() == "System";

        private static int GetAssemblyPriority(IAssemblySymbol? assemblySymbol)
        {
            if (assemblySymbol is null)
                return 0;

            foreach (var attributeData in assemblySymbol.GetAttributes())
            {
                if (attributeData.AttributeClass?.ToString() != "FactoryGenerator.Attributes.InjectionPriorityAttribute")
                    continue;

                if (attributeData.ConstructorArguments.Length == 1
                    && attributeData.ConstructorArguments[0].Value is int priority)
                    return priority;
            }

            return 0;
        }
    }
}
