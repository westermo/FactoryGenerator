using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace FactoryGenerator
{
    public class Injection
    {
        public INamedTypeSymbol Type { get; }
        public ImmutableArray<INamedTypeSymbol> Interfaces { get; }
        public bool Singleton { get; }
        public bool Scoped { get; }
        public BooleanInjection? BooleanInjection { get; }
        public ISymbol? Lambda { get; }
        private string LazyName => "m_" + Name.Replace("()", "");
        public string Name => SymbolUtility.MemberName(Type).Replace("()", "") + (Lambda is not null ? Lambda.Name : string.Empty) + "()";
        public bool Disposable { get; }

        public string Declaration(ImmutableArray<INamedTypeSymbol> availableParameters, bool forLifetimeScope)
        {
            var creationCall = CreationCall(availableParameters);
            if (forLifetimeScope && Singleton)
            {
                return $"internal {Type} {Name} => m_fallback.{Name};";
            }

            return (Singleton || Scoped) ? SymbolUtility.SingletonFactory(Type, Name, LazyName, creationCall, Disposable) :
                   Disposable ? SymbolUtility.DisposableFactory(Type, Name, creationCall) : $"internal {Type} {Name} => {creationCall};";
        }

        public string DisposeCall => LazyName + "?.Dispose();";

        private string CreationCall(ImmutableArray<INamedTypeSymbol> availableParameters)
        {
            HashSet<IParameterSymbol>? missing = null;
            string? creationCall;
            switch (Lambda)
            {
                case IMethodSymbol methodSymbol:
                    if (!availableParameters.Contains(methodSymbol.ContainingType))
                    {
                        throw new Exception(
                            $"Could not find any [Inject]ed implementations of {methodSymbol.ContainingType} to use as the source for the injection of {methodSymbol.ContainingType}.{methodSymbol.Name}. Please provide at least one injection of the type {methodSymbol.ContainingType}.");
                    }

                    creationCall = $"{SymbolUtility.MemberName(methodSymbol.ContainingType)}.{methodSymbol.Name}{MakeMethodCall(methodSymbol, missing)}";
                    break;
                case IPropertySymbol parameterSymbol:
                    if (!availableParameters.Contains(parameterSymbol.ContainingType))
                    {
                        throw new Exception(
                            $"Could not find any [Inject]ed implementations of {parameterSymbol.ContainingType} to use as the source for the injection of {parameterSymbol.ContainingType}.{parameterSymbol.Name}. Please provide at least one injection of the type {parameterSymbol.ContainingType}.");
                    }

                    creationCall = $"{SymbolUtility.MemberName(parameterSymbol.ContainingType)}.{parameterSymbol.Name}";
                    break;
                default:
                    var constructor = GetBestConstructor(availableParameters, ref missing);
                    if (constructor is null)
                    {
                        throw new Exception($"No Construction method for {Type}. Lambda was of Type {Lambda?.GetType()}");
                    }

                    creationCall = $"new {Type}{MakeConstructorCall(constructor, missing)}";
                    break;
            }

            return creationCall;
        }

        private string MakeConstructorCall(IMethodSymbol? constructor, HashSet<IParameterSymbol>? missing)
        {
            if (constructor == null) throw new Exception($"Construction Method for {Type}({Lambda}) was null");
            var parameters = new List<string>();
            foreach (var parameter in constructor.Parameters)
            {
                if (missing?.Contains(parameter) == true)
                {
                    parameters.Add(parameter.Name);
                    continue;
                }

                if (parameter.Type is not INamedTypeSymbol named) continue;
                parameters.Add(SymbolUtility.MemberName(named));
            }

            return $"({string.Join(", ", parameters)})";
        }

        private string MakeMethodCall(IMethodSymbol? constructor, HashSet<IParameterSymbol>? missing)
        {
            if (constructor == null) throw new Exception($"Method for {Type} was null");
            var parameters = new List<string>();
            foreach (var parameter in constructor.Parameters)
            {
                if (missing?.Contains(parameter) == true)
                {
                    parameters.Add(parameter.Name);
                    continue;
                }

                parameters.Add(SymbolUtility.MemberName(parameter.Type));
            }

            return $"({string.Join(", ", parameters)})";
        }

        public IMethodSymbol? GetBestConstructor(ImmutableArray<INamedTypeSymbol> availableParameters, ref HashSet<IParameterSymbol>? missing)
        {
            missing = null;
            IMethodSymbol? chosenConstructor = null;
            foreach (var constructor in Type.Constructors)
            {
                var valid = true;
                var localMissing = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
                foreach (var parameter in constructor.Parameters)
                {
                    if (availableParameters.Contains(parameter.Type, SymbolEqualityComparer.Default)) continue;
                    if (parameter.HasExplicitDefaultValue) continue;
                    if (parameter.IsParams) continue;
                    valid = false;
                    localMissing.Add(parameter);
                }

                if (valid)
                {
                    chosenConstructor = constructor;
                    missing = null;
                    break;
                }

                if ((missing?.Count ?? int.MaxValue) <= localMissing.Count) continue;
                chosenConstructor = constructor;
                missing = localMissing;
            }

            return chosenConstructor;
        }

        private Injection(INamedTypeSymbol type, ImmutableArray<INamedTypeSymbol> interfaces,
                          bool singleton,
                          bool disposable,
                          BooleanInjection? booleanInjection, ISymbol? lambda, bool scoped)
        {
            Type = type;
            Interfaces = interfaces;
            Singleton = singleton;
            Disposable = disposable;
            BooleanInjection = booleanInjection;
            Lambda = lambda;
            Scoped = scoped;
        }


        public static Injection? Create(ISymbol symbol, ImmutableArray<AttributeData> attributes, CancellationToken token)
        {
            INamedTypeSymbol? namedTypeSymbol = null;
            ISymbol? lambda = null;
            switch (symbol)
            {
                case INamedTypeSymbol nts:
                {
                    namedTypeSymbol = nts;
                    if (namedTypeSymbol.TypeKind == TypeKind.Interface) return null;
                    if (namedTypeSymbol.IsAbstract) return null;
                    break;
                }
                case IMethodSymbol methodSymbol:
                {
                    namedTypeSymbol = methodSymbol.ReturnType as INamedTypeSymbol;
                    lambda = methodSymbol;
                    break;
                }
                case IPropertySymbol property:
                {
                    namedTypeSymbol = property.Type as INamedTypeSymbol;
                    lambda = property;
                    break;
                }
            }

            if (namedTypeSymbol is null) return null;

            var singleInstance = false;
            var acquireChildInterfaces = false;
            var asSelf = namedTypeSymbol.Interfaces.Length == 0;
            var scoped = false;
            if (namedTypeSymbol.TypeKind == TypeKind.Interface)
            {
                asSelf = true;
            }

            BooleanInjection? boolean = null;
            HashSet<INamedTypeSymbol> attributedInterfaces = new(SymbolEqualityComparer.Default);
            HashSet<INamedTypeSymbol> preventedInterfaces = new(SymbolEqualityComparer.Default);
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
                        {
                            attributedInterfaces.Add(addedNamed);
                        }

                        break;
                    case "ExceptAsAttribute":
                        if (attributeData.AttributeClass!.TypeArguments[0] is INamedTypeSymbol removedNamed)
                        {
                            preventedInterfaces.Add(removedNamed);
                        }

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
            {
                interfaces = interfaces.Add(namedTypeSymbol);
            }

            interfaces = interfaces.AddRange(attributedInterfaces);
            var isDisposable = namedTypeSymbol.AllInterfaces.Any(i => i.Name.Equals("IDisposable"));
            var disposable = interfaces.FirstOrDefault(interfaceSymbol => interfaceSymbol.Name.Contains("IDisposable"));

            if (disposable is not null)
            {
                interfaces = interfaces.Remove(disposable);
            }

            interfaces = interfaces.RemoveRange(preventedInterfaces).Distinct(SymbolEqualityComparer.Default).Cast<INamedTypeSymbol>().ToImmutableArray();


            return new Injection(namedTypeSymbol, interfaces, singleInstance, isDisposable, boolean, lambda, scoped);
        }

        private static BooleanInjection? HandleBoolean(AttributeData attributeData)
        {
            if (attributeData.ConstructorArguments[0].Value is string key)
            {
                return new BooleanInjection(true, key);
            }

            return null;
        }
    }
}