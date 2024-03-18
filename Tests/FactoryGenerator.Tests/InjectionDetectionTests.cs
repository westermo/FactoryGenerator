using Inherited;
using Inheritor;
using Inheritor.Generated;
using Shouldly;
using Type = Inherited.Type;

namespace FactoryGenerator.Tests;

public class InjectionDetectionTests()
{
    private readonly DependencyInjectionContainer m_container = new(default, default, new NonInjectedClass());

    [Fact]
    public void InjectedTypesAreResolvable()
    {
        m_container.Resolve<IType>().ShouldBeOfType<Type>();
    }

    [Fact]
    public void SingletonInjectionsResolveToTheSameInstanceEverytime()
    {
        var first = m_container.Resolve<ISingleton>();
        var second = m_container.Resolve<ISingleton>();
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public void NonSingleInjectionsResolveToDifferentInstanceEverytime()
    {
        var first = m_container.Resolve<IType>();
        var second = m_container.Resolve<IType>();
        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Fact]
    public void ResolveUsesArguments()
    {
        var dummy = new NonInjectedClass();
        var myContainer = new DependencyInjectionContainer(default, default, dummy);
        myContainer.Resolve<Constructed>().NonInjectedClassArgument.ShouldBe(dummy);
    }

    [Theory]
    [InlineData(true, typeof(EnabledImplementation))]
    [InlineData(false, typeof(FallbackImplementation))]
    public void PickupSingleInjectionWithBoolean(bool value, System.Type expected)
    {
        var myContainer = new DependencyInjectionContainer(value, default, default!);
        myContainer.Resolve<ISwitchableInterface>().ShouldBeOfType(expected);
    }

    [Fact]
    public void PickupSingleInjectionFromMethod()
    {
        m_container.Resolve<IMethodResult>().ShouldBeOfType<MethodResult>();
    }

    [Fact]
    public void DoNotPickupNonInjection()
    {
        try
        {
            m_container.Resolve<IAnotherType>();
        }
        catch (Exception)
        {
            return;
        }

        true.ShouldBeFalse();
    }

    [Fact]
    public void DontPickupIDisposable()
    {
        try
        {
            m_container.Resolve<IDisposable>();
        }
        catch (Exception)
        {
            return;
        }

        true.ShouldBeFalse();
    }

    [Fact]
    public void DontPickupExcluded()
    {
        try
        {
            m_container.Resolve<IExcluded>();
        }
        catch (Exception)
        {
            return;
        }

        true.ShouldBeFalse();
    }

    [Fact]
    public void PickupTypesSpecifiedByAs()
    {
        m_container.Resolve<IPresent>().ShouldBeOfType<Composite>();
    }

    [Fact]
    public void PickupInheritedInterfaces()
    {
        m_container.Resolve<ISub>().ShouldBeOfType<Inherited.Inheritor>();
    }

    [Fact]
    public void InheritorsOverride()
    {
        m_container.Resolve<IOverridable>().ShouldBeOfType<Overrider>();
    }


    [Fact]
    public void DisposingContainerDisposesSingletons()
    {
        ISingletonDisposer singleton;
        {
            using var myContainer = new DependencyInjectionContainer(false, default!);
            singleton = myContainer.Resolve<ISingletonDisposer>();
            singleton.ShouldBeOfType<DisposableSingleton>();
        }
        ((DisposableSingleton) singleton).WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public void DisposingContainerDoesNotDisposeUntrackedInstances()
    {
        IDisposer singleton;
        {
            using var myContainer = new DependencyInjectionContainer(false, default!);
            singleton = myContainer.Resolve<IDisposer>();
            singleton.ShouldBeOfType<DisposableNonSingleton>();
        }
        ((DisposableNonSingleton) singleton).WasDisposed.ShouldBeFalse();
        ((DisposableNonSingleton) singleton).Dispose();
        ((DisposableNonSingleton) singleton).WasDisposed.ShouldBeTrue();
    }

    [Fact]
    public void DisposingContainerDoesNotDisposesUnreferencedSingletons()
    {
        using var myContainer = new DependencyInjectionContainer(false, default!);
    }

    [Fact]
    public void ArrayExpressionsCollect()
    {
        m_container.Resolve<ArrayConsumer>().Arrays.Count().ShouldBe(3);
    }

    [Fact]
    public void RequestedArraysArePresent()
    {
        Program.Method().Count().ShouldBe(3);
    }
    [Fact]
    public void BooleanFallbackIsOverriden()
    {
        m_container.Resolve<IOverrideBoolean>().ShouldBeOfType<OverridingBoolean>();
    }
}