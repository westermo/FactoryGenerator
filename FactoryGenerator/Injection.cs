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

            var interfaces = acquireChildInterfaces ? namedTypeSymbol.AllInterfaces : namedTypeSymbol.Interfaces;
            if (asSelf)
                interfaces = interfaces.Add(namedTypeSymbol);
            interfaces = interfaces.AddRange(attributedInterfaces);

            var isDisposable = namedTypeSymbol.AllInterfaces.Any(i => i.Name.Equals("IDisposable"));
            var disposableIface = interfaces.FirstOrDefault(i => i.Name.Contains("IDisposable"));
            if (disposableIface is not null)
                interfaces = interfaces.Remove(disposableIface);

            interfaces = interfaces
                .RemoveRange(preventedInterfaces)
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<INamedTypeSymbol>()
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
                isTestType: namedTypeSymbol.ToString()!.Contains("Test"),
                interfaceFullNames: ifaceFullNames,
                interfaceMemberNames: ifaceMemberNames,
                singleton: singleInstance,
                scoped: scoped,
                disposable: isDisposable,
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
    }
}
