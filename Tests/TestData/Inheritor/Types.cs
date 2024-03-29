﻿using FactoryGenerator.Attributes;
using Inherited;
using Inheritor.Generated;

namespace Inheritor;

[Inject]
public class Overrider : IOverridable;
[Inject]
public class OverridingBoolean : IOverrideBoolean;

public static class Program
{
    public static IEnumerable<IRequestedArray> Method()
    {
        var container = new DependencyInjectionContainer(false, false, null!);
        var array = container.Resolve<IEnumerable<IRequestedArray>>();
        return array;
    }
}