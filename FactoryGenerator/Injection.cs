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

            var isEnumerable = SymbolUtility.IsEnumerable(parameter.Type);
            string? enumElemFull = null, enumElemMember = null;
            if (isEnumerable && parameter.Type is INamedTypeSymbol namedEnum && namedEnum.TypeArguments.Length == 1)
            {
                var elem = namedEnum.TypeArguments[0];
                enumElemFull = elem.ToString()!;
                enumElemMember = SymbolUtility.MemberName(elem).Replace("()", "");
            }

            var isArray = parameter.Type is IArrayTypeSymbol;
            string? arrElemFull = null, arrElemMember = null;
            if (isArray && parameter.Type is IArrayTypeSymbol arrType && arrType.ElementType is INamedTypeSymbol arrElem)
            {
                arrElemFull = arrElem.ToString()!;
                arrElemMember = SymbolUtility.MemberName(arrElem).Replace("()", "");
            }

            return new ParameterData(
                typeFullName, typeMemberName,
                parameter.HasExplicitDefaultValue, parameter.IsParams, parameter.Name,
                isEnumerable, enumElemFull, enumElemMember,
                isArray, arrElemFull, arrElemMember);
        }

        private static BooleanInjection? HandleBoolean(AttributeData attributeData)
        {
            if (attributeData.ConstructorArguments[0].Value is string key)
                return new BooleanInjection(true, key);
            return null;
        }
    }
}
