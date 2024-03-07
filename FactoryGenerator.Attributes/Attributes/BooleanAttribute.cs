using System;

namespace FactoryGenerator.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class BooleanAttribute(string key) : Attribute
{
    private string m_key = key;
}