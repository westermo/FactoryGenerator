using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace FactoryGenerator
{
    public enum CollectionKind { None, Enumerable, Array, List, ImmutableArray, ReadOnlySpan }

    public sealed class InjectionData : IEquatable<InjectionData>
    {
        public string TypeFullName { get; }
        public string TypeMemberName { get; }   // MemberName(type) without "()"
        public bool IsTestType { get; }
        public ImmutableArray<string> InterfaceFullNames { get; }
        public ImmutableArray<string> InterfaceMemberNames { get; }  // parallel to InterfaceFullNames, without "()"
        public bool Singleton { get; }
        public bool Scoped { get; }
        public bool Disposable { get; }
        public BooleanInjection? BooleanInjection { get; }
        public ImmutableArray<ConstructorData> Constructors { get; }
        public LambdaData? Lambda { get; }

        // Computed — not stored
        public string Name => TypeMemberName + (Lambda?.MemberName ?? string.Empty) + "()";
        public string LazyFieldName => "m_" + TypeMemberName + (Lambda?.MemberName ?? string.Empty);

        public InjectionData(
            string typeFullName, string typeMemberName, bool isTestType,
            ImmutableArray<string> interfaceFullNames, ImmutableArray<string> interfaceMemberNames,
            bool singleton, bool scoped, bool disposable,
            BooleanInjection? booleanInjection,
            ImmutableArray<ConstructorData> constructors, LambdaData? lambda)
        {
            TypeFullName = typeFullName;
            TypeMemberName = typeMemberName;
            IsTestType = isTestType;
            InterfaceFullNames = interfaceFullNames;
            InterfaceMemberNames = interfaceMemberNames;
            Singleton = singleton;
            Scoped = scoped;
            Disposable = disposable;
            BooleanInjection = booleanInjection;
            Constructors = constructors;
            Lambda = lambda;
        }

        public bool Equals(InjectionData? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return TypeFullName == other.TypeFullName
                && TypeMemberName == other.TypeMemberName
                && IsTestType == other.IsTestType
                && InterfaceFullNames.SequenceEqual(other.InterfaceFullNames)
                && InterfaceMemberNames.SequenceEqual(other.InterfaceMemberNames)
                && Singleton == other.Singleton
                && Scoped == other.Scoped
                && Disposable == other.Disposable
                && Equals(BooleanInjection, other.BooleanInjection)
                && Constructors.SequenceEqual(other.Constructors)
                && Equals(Lambda, other.Lambda);
        }

        public override bool Equals(object? obj) => obj is InjectionData other && Equals(other);
        public override int GetHashCode() => TypeFullName.GetHashCode();
    }

    public sealed class ConstructorData : IEquatable<ConstructorData>
    {
        public ImmutableArray<ParameterData> Parameters { get; }

        public ConstructorData(ImmutableArray<ParameterData> parameters)
        {
            Parameters = parameters;
        }

        public bool Equals(ConstructorData? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return Parameters.SequenceEqual(other.Parameters);
        }

        public override bool Equals(object? obj) => obj is ConstructorData other && Equals(other);
        public override int GetHashCode() => Parameters.Length;
    }

    public sealed class ParameterData : IEquatable<ParameterData>
    {
        public string TypeFullName { get; }
        public string TypeMemberName { get; }            // MemberName(param.Type) without "()"
        public bool HasExplicitDefault { get; }
        public bool IsParams { get; }
        public string Name { get; }                      // parameter.Name
        public CollectionKind CollectionKind { get; }
        public string? CollectionElementFullName { get; }
        public string? CollectionElementMemberName { get; } // without "()"
        public bool IsNullable { get; }

        // Convenience
        public bool IsCollection => CollectionKind != CollectionKind.None;
        public bool IsEnumerable => CollectionKind == CollectionKind.Enumerable;
        public bool IsArrayType => CollectionKind == CollectionKind.Array;

        public ParameterData(
            string typeFullName, string typeMemberName,
            bool hasExplicitDefault, bool isParams, string name,
            CollectionKind collectionKind, string? collectionElementFullName, string? collectionElementMemberName,
            bool isNullable)
        {
            TypeFullName = typeFullName;
            TypeMemberName = typeMemberName;
            HasExplicitDefault = hasExplicitDefault;
            IsParams = isParams;
            Name = name;
            CollectionKind = collectionKind;
            CollectionElementFullName = collectionElementFullName;
            CollectionElementMemberName = collectionElementMemberName;
            IsNullable = isNullable;
        }

        public bool Equals(ParameterData? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return TypeFullName == other.TypeFullName
                && TypeMemberName == other.TypeMemberName
                && HasExplicitDefault == other.HasExplicitDefault
                && IsParams == other.IsParams
                && Name == other.Name
                && CollectionKind == other.CollectionKind
                && CollectionElementFullName == other.CollectionElementFullName
                && CollectionElementMemberName == other.CollectionElementMemberName
                && IsNullable == other.IsNullable;
        }

        public override bool Equals(object? obj) => obj is ParameterData other && Equals(other);
        public override int GetHashCode() => TypeFullName.GetHashCode();
    }

    public sealed class LambdaData : IEquatable<LambdaData>
    {
        public bool IsMethod { get; }
        public string ContainingTypeFullName { get; }
        public string ContainingTypeMemberName { get; }  // SymbolUtility.MemberName(containingType) WITH "()"
        public string MemberName { get; }                // method or property name
        public ImmutableArray<ParameterData> MethodParameters { get; }  // empty for property

        public LambdaData(
            bool isMethod, string containingTypeFullName, string containingTypeMemberName,
            string memberName, ImmutableArray<ParameterData> methodParameters)
        {
            IsMethod = isMethod;
            ContainingTypeFullName = containingTypeFullName;
            ContainingTypeMemberName = containingTypeMemberName;
            MemberName = memberName;
            MethodParameters = methodParameters;
        }

        public bool Equals(LambdaData? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return IsMethod == other.IsMethod
                && ContainingTypeFullName == other.ContainingTypeFullName
                && ContainingTypeMemberName == other.ContainingTypeMemberName
                && MemberName == other.MemberName
                && MethodParameters.SequenceEqual(other.MethodParameters);
        }

        public override bool Equals(object? obj) => obj is LambdaData other && Equals(other);
        public override int GetHashCode() => ContainingTypeFullName.GetHashCode();
    }

    public sealed class UsageData : IEquatable<UsageData>
    {
        public string FullName { get; }
        public string MemberName { get; }           // SymbolUtility.MemberName(type) without "()"
        public string ElementTypeFullName { get; }
        public string ElementTypeMemberName { get; } // without "()"

        public UsageData(string fullName, string memberName, string elementTypeFullName, string elementTypeMemberName)
        {
            FullName = fullName;
            MemberName = memberName;
            ElementTypeFullName = elementTypeFullName;
            ElementTypeMemberName = elementTypeMemberName;
        }

        public bool Equals(UsageData? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;
            return FullName == other.FullName
                && MemberName == other.MemberName
                && ElementTypeFullName == other.ElementTypeFullName
                && ElementTypeMemberName == other.ElementTypeMemberName;
        }

        public override bool Equals(object? obj) => obj is UsageData other && Equals(other);
        public override int GetHashCode() => FullName.GetHashCode();
    }
}
