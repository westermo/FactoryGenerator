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