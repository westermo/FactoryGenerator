using FactoryGenerator.Attributes;

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