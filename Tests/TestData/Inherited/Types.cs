using FactoryGenerator.Attributes;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Inherited;

public interface IType;

public interface IOverridable;

[Inject]
public class Type : IType;

[Inject]
public class Overriden : IOverridable;

public interface ISingleton;

[Inject, Singleton]
public class Singleton : ISingleton;

public interface ISelfInjection;

[Inject, Singleton, Self]
public class SelfSingletonInjection : ISelfInjection;

public interface ISwitchableInterface;

[Inject, Singleton, Boolean("TestBool")]
public class EnabledImplementation : ISwitchableInterface;

[Inject, Singleton, Self]
public class FallbackImplementation : ISwitchableInterface;

[Inject, Self]
public class SelfInjectionNonSingletonNoInterfaces;

public class NonInjectedClass;

[Inject, Self]
public class Constructed(NonInjectedClass nonInjectedClassArgument, ISingleton injectedArgument)
{
    public NonInjectedClass NonInjectedClassArgument { get; } = nonInjectedClassArgument;
    public ISingleton InjectedArgument { get; } = injectedArgument;
}

public interface IMethodResult;

public class MethodResult : IMethodResult;

public interface IMethodSource
{
    [Inject]
    IMethodResult Method();
}

[Inject]
public class MethodSource : IMethodSource
{
    public IMethodResult Method() => new MethodResult();
}

public interface IAnotherType;

public class AnotherType : IAnotherType;

public interface IExcluded;

public interface IPresent;

public interface IInherited : IPresent;

[Inject, As<IPresent>, ExceptAs<IExcluded>]
public class Composite : IInherited, IExcluded, IDisposable
{
    public void Dispose()
    {
    }
}

public interface ISub;

public class Sub : ISub;

[Inject, InheritInterfaces]
public class Inheritor : Sub;

public interface IArray;

[Inject]
public class Array1 : IArray;

[Inject]
public class Array2 : IArray;

[Inject]
public class Array3 : IArray;

[Inject, Self]
public class ArrayConsumer(IEnumerable<IArray> arrays)
{
    public IEnumerable<IArray> Arrays { get; } = arrays;
}

public interface IRequestedArray;

[Inject]
public class RequestedArray1 : IRequestedArray;

[Inject]
public class RequestedArray2 : IRequestedArray;

[Inject]
public class RequestedArray3 : IRequestedArray;

public interface IDisposer;

[Inject]
public class DisposableNonSingleton : IDisposer, IDisposable
{
    public bool WasDisposed { get; private set; }

    public void Dispose()
    {
        WasDisposed = true;
    }
}

public interface ISingletonDisposer;

[Inject, Singleton]
public class DisposableSingleton : ISingletonDisposer, IDisposable
{
    public bool WasDisposed { get; private set; }

    public void Dispose()
    {
        WasDisposed = true;
    }
}

public interface IOverrideBoolean;

[Inject, Boolean("A")]
public class OverridenBooleanOption : IOverrideBoolean;

[Inject]
public class OverridenFallback : IOverrideBoolean;

public class Containing
{
    [Inject]
    public class Containee;
}

public interface IScoped
{
    bool WasDisposed { get; }
}

public interface ISelfish;

public interface ISelfReferentialFactory
{
    [Inject]
    public SelfReferential Create();
}

[Inject]
public class SelfReferentialFactory : ISelfReferentialFactory
{
    public SelfReferential Create()
    {
        return new SelfReferential([]);
    }
}

public class SelfReferential(IEnumerable<ISelfish> references) : ISelfish
{
    public IEnumerable<ISelfish> References { get; } = references;
}

[Inject, Scoped]
public class Scoped : IDisposable, IScoped
{
    public bool WasDisposed { get; private set; }

    public void Dispose()
    {
        WasDisposed = true;
    }
}

// ── Nullable parameter tests ─────────────────────────────────────────────────

/// <summary>Interface with no [Inject] implementation — intentionally unregistered.</summary>
public interface INullableOptional;

[Inject, Self]
public class NullableConsumer(INullableOptional? optional)
{
    public INullableOptional? Optional { get; } = optional;
}

public interface INullablePresent;

[Inject]
public class NullablePresent : INullablePresent;

[Inject, Self]
public class NullablePresentConsumer(INullablePresent? optional)
{
    public INullablePresent? Optional { get; } = optional;
}

// ── Additional collection type tests ─────────────────────────────────────────

[Inject, Self]
public class ArrayParameterConsumer(IArray[] arrays)
{
    public IArray[] Arrays { get; } = arrays;
}

[Inject, Self]
public class ListConsumer(List<IArray> arrays)
{
    public List<IArray> Arrays { get; } = arrays;
}

[Inject, Self]
public class ImmutableArrayConsumer(ImmutableArray<IArray> arrays)
{
    public ImmutableArray<IArray> Arrays { get; } = arrays;
}

[Inject, Self]
public class ReadOnlySpanConsumer(ReadOnlySpan<IArray> arrays)
{
    public int Count { get; } = arrays.Length;
}

// ── Cross-array reentrancy tests ─────────────────────────────────────────────
// Resolving IEnumerable<ICrossArrayA> should work even though CrossA3 depends on
// IEnumerable<ICrossArrayB>.  The reentrancy flag is per-collection type, so
// resolving the B array must not be blocked by the A array's reentrancy guard.

public interface ICrossArrayB;

[Inject]
public class CrossB1 : ICrossArrayB;

[Inject]
public class CrossB2 : ICrossArrayB;

public interface ICrossArrayA;

[Inject]
public class CrossA1 : ICrossArrayA;

[Inject]
public class CrossA2 : ICrossArrayA;

/// <summary>
/// Implementation of ICrossArrayA that depends on an array of ICrossArrayB.
/// When the container builds IEnumerable&lt;ICrossArrayA&gt; and encounters CrossA3
/// it must resolve IEnumerable&lt;ICrossArrayB&gt;.  This must succeed because the
/// reentrancy guard is local to each array type.
/// </summary>
[Inject]
public class CrossA3(IEnumerable<ICrossArrayB> deps) : ICrossArrayA
{
    public IEnumerable<ICrossArrayB> Deps { get; } = deps;
}

[Inject, Self]
public class CrossArrayConsumer(IEnumerable<ICrossArrayA> items)
{
    public IEnumerable<ICrossArrayA> Items { get; } = items;
}

// ── Inheritor + Base array tests ─────────────────────────────────────────────
// Interface whose implementations are split across the Inherited and Inheritor
// projects, so we can verify that arrays merge correctly across container
// hierarchies (both Base → child and Inheritor → child directions).

public interface ISplitArray;

[Inject]
public class SplitBase1 : ISplitArray;

[Inject]
public class SplitBase2 : ISplitArray;

[Inject, Self]
public class SplitArrayConsumer(IEnumerable<ISplitArray> items)
{
    public IEnumerable<ISplitArray> Items { get; } = items;
}