using System.Runtime.InteropServices;
using FactoryGenerator.Attributes;
using Inherited;
using Inheritor.Generated;

namespace Inheritor;

[Inject]
public class Overrider : IOverridable;

[Inject]
public class OverridingBoolean : IOverrideBoolean;

[Inject]
public class ChainA(ChainB B, ChainC C, ChainD D)
{
    public ChainB B { get; } = B;
    public ChainC C { get; } = C;
    public ChainD D { get; } = D;
}

[Inject]
public class ChainB(ChainC C, ChainD D, ChainE E)
{
    public ChainC C { get; } = C;
    public ChainD D { get; } = D;
    public ChainE E { get; } = E;
}

[Inject]
public class ChainC(ChainE E)
{
    public ChainE E { get; } = E;
}

[Inject]
public class ChainD(ChainC C, ChainE E)
{
    public ChainC C { get; } = C;
    public ChainE E { get; } = E;
}

[Inject]
public class ChainE;

public static class Program
{
    public static IEnumerable<IRequestedArray> Method()
    {
        var container = new DependencyInjectionContainer(false, false, null!);
        var array = container.Resolve<IEnumerable<IRequestedArray>>();
        return array;
    }
}
// ── Inheritor + Base array tests ─────────────────────────────────────────────
// Additional ISplitArray implementations in the Inheritor project.  When a child
// container is created from a parent, the merged IEnumerable<ISplitArray> should
// contain items from both Inherited (Base) and Inheritor.

[Inject]
public class SplitInheritor1 : ISplitArray;

[Inject]
public class SplitInheritor2 : ISplitArray;

[Inject]
public class SplitInheritor3 : ISplitArray;