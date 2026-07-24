using System;

namespace FactoryGenerator.Attributes;

[AttributeUsage(AttributeTargets.Assembly)]
public class InjectionPriorityAttribute(int priority) : Attribute
{
    public int Priority { get; } = priority;
}
