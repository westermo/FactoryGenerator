using System;

namespace FactoryGenerator.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class SelfAttribute : Attribute;