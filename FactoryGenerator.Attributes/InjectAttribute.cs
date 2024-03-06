using System;
using System.Runtime.InteropServices;

namespace FactoryGenerator.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class InjectAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class InheritInterfacesAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class SingletonAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface, AllowMultiple = true)]
public class ExceptAsAttribute<T> : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface, AllowMultiple = true)]
public class AsAttribute<T> : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class SelfAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class BooleanAttribute(string key) : Attribute
{
    private string m_key = key;
}
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class LinuxOnlyAttribute : Attribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Interface)]
public class WindowsOnlyAttribute : Attribute;