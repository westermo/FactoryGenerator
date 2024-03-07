using System;

namespace FactoryGenerator.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface, AllowMultiple = true)]
public class ExceptAsAttribute<T> : Attribute;