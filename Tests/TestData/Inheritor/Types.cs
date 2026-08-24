using FactoryGenerator.Attributes;
using Inherited;
using Inheritor.Generated;

namespace Inheritor;

[Inject]
public class Overrider : IOverridable;

[Inject]
public class OverridingBoolean : IOverrideBoolean;

[Inject]
public class OverrideCycleResolved : IOverrideCycle;

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

    public static IEnumerable<IRequestedArray> MethodAgain()
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

// ── Large dependency tree (1-3-9-27) ─────────────────────────────────────────
// Root → 3 branches → 9 branches → 27 leaves.  Exercises deep, wide resolution.

// Leaves (27)
[Inject] public class Leaf01;
[Inject] public class Leaf02;
[Inject] public class Leaf03;
[Inject] public class Leaf04;
[Inject] public class Leaf05;
[Inject] public class Leaf06;
[Inject] public class Leaf07;
[Inject] public class Leaf08;
[Inject] public class Leaf09;
[Inject] public class Leaf10;
[Inject] public class Leaf11;
[Inject] public class Leaf12;
[Inject] public class Leaf13;
[Inject] public class Leaf14;
[Inject] public class Leaf15;
[Inject] public class Leaf16;
[Inject] public class Leaf17;
[Inject] public class Leaf18;
[Inject] public class Leaf19;
[Inject] public class Leaf20;
[Inject] public class Leaf21;
[Inject, Singleton] public class Leaf22;
[Inject] public class Leaf23;
[Inject, Singleton] public class Leaf24;
[Inject] public class Leaf25;
[Inject] public class Leaf26;
[Inject] public class Leaf27;

// Mid-level (9) — each depends on 3 leaves
[Inject] public class Mid1(Leaf01 leaf01, Leaf02 leaf02, Leaf03 leaf03) { public Leaf01 Leaf01 => leaf01; public Leaf02 Leaf02 => leaf02; public Leaf03 Leaf03 => leaf03; }
[Inject] public class Mid2(Leaf04 leaf04, Leaf05 leaf05, Leaf06 leaf06) { public Leaf04 Leaf04 => leaf04; public Leaf05 Leaf05 => leaf05; public Leaf06 Leaf06 => leaf06; }
[Inject] public class Mid3(Leaf07 leaf07, Leaf08 leaf08, Leaf09 leaf09) { public Leaf07 Leaf07 => leaf07; public Leaf08 Leaf08 => leaf08; public Leaf09 Leaf09 => leaf09; }
[Inject] public class Mid4(Leaf10 leaf10, Leaf11 leaf11, Leaf12 leaf12) { public Leaf10 Leaf10 => leaf10; public Leaf11 Leaf11 => leaf11; public Leaf12 Leaf12 => leaf12; }
[Inject] public class Mid5(Leaf13 leaf13, Leaf14 leaf14, Leaf15 leaf15) { public Leaf13 Leaf13 => leaf13; public Leaf14 Leaf14 => leaf14; public Leaf15 Leaf15 => leaf15; }
[Inject] public class Mid6(Leaf16 leaf16, Leaf17 leaf17, Leaf18 leaf18) { public Leaf16 Leaf16 => leaf16; public Leaf17 Leaf17 => leaf17; public Leaf18 Leaf18 => leaf18; }
[Inject] public class Mid7(Leaf19 leaf19, Leaf20 leaf20, Leaf21 leaf21) { public Leaf19 Leaf19 => leaf19; public Leaf20 Leaf20 => leaf20; public Leaf21 Leaf21 => leaf21; }
[Inject] public class Mid8(Leaf22 leaf22, Leaf23 leaf23, Leaf24 leaf24) { public Leaf22 Leaf22 => leaf22; public Leaf23 Leaf23 => leaf23; public Leaf24 Leaf24 => leaf24; }
[Inject] public class Mid9(Leaf25 leaf25, Leaf26 leaf26, Leaf27 leaf27) { public Leaf25 Leaf25 => leaf25; public Leaf26 Leaf26 => leaf26; public Leaf27 Leaf27 => leaf27; }

// Branches (3) — each depends on 3 mid-level nodes
[Inject] public class Branch1(Mid1 mid1, Mid2 mid2, Mid3 mid3) { public Mid1 Mid1 => mid1; public Mid2 Mid2 => mid2; public Mid3 Mid3 => mid3; }
[Inject] public class Branch2(Mid4 mid4, Mid5 mid5, Mid6 mid6) { public Mid4 Mid4 => mid4; public Mid5 Mid5 => mid5; public Mid6 Mid6 => mid6; }
[Inject] public class Branch3(Mid7 mid7, Mid8 mid8, Mid9 mid9) { public Mid7 Mid7 => mid7; public Mid8 Mid8 => mid8; public Mid9 Mid9 => mid9; }

// Root (1) — depends on 3 branches
[Inject, Self] public class TreeRoot(Branch1 branch1, Branch2 branch2, Branch3 branch3) { public Branch1 Branch1 => branch1; public Branch2 Branch2 => branch2; public Branch3 Branch3 => branch3; }