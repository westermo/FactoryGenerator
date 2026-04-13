using Inherited;
using Inheritor;
using Inheritor.Generated;
using Shouldly;
using Type = Inherited.Type;

namespace FactoryGenerator.Tests;

public class InjectionDetectionTests()
{
    private readonly IContainer m_container = new DependencyInjectionContainer(default, default, new NonInjectedClass());

    [After(Test)]
    public void DisposeContainer() => m_container.Dispose();

    [Test]
    public void InjectedTypesAreResolvable()
    {
        m_container.Resolve<IType>().ShouldBeOfType<Type>();
    }

    [Test]
    public void SingletonInjectionsResolveToTheSameInstanceEverytime()
    {
        var first = m_container.Resolve<ISingleton>();
        var second = m_container.Resolve<ISingleton>();
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Test]
    public void NonSingleInjectionsResolveToDifferentInstanceEverytime()
    {
        var first = m_container.Resolve<IType>();
        var second = m_container.Resolve<IType>();
        ReferenceEquals(first, second).ShouldBeFalse();
    }

    [Test]
    public void ResolveUsesArguments()
    {
        var dummy = new NonInjectedClass();
        var myContainer = new DependencyInjectionContainer(default, default, dummy);
        myContainer.Resolve<Constructed>().NonInjectedClassArgument.ShouldBe(dummy);
    }

    [Test]
    [Arguments(true, typeof(EnabledImplementation))]
    [Arguments(false, typeof(FallbackImplementation))]
    public void PickupSingleInjectionWithBoolean(bool value, System.Type expected)
    {
        var myContainer = new DependencyInjectionContainer(value, default, default!);
        myContainer.Resolve<ISwitchableInterface>().ShouldBeOfType(expected);
    }

    [Test]
    public void PickupSingleInjectionFromMethod()
    {
        m_container.Resolve<IMethodResult>().ShouldBeOfType<MethodResult>();
    }

    [Test]
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

    [Test]
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

    [Test]
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

    [Test]
    public void PickupTypesSpecifiedByAs()
    {
        m_container.Resolve<IPresent>().ShouldBeOfType<Composite>();
    }

    [Test]
    public void PickupInheritedInterfaces()
    {
        m_container.Resolve<ISub>().ShouldBeOfType<Inherited.Inheritor>();
    }

    [Test]
    public void InheritorsOverride()
    {
        m_container.Resolve<IOverridable>().ShouldBeOfType<Overrider>();
    }


    [Test]
    public void DisposingContainerDisposesSingletons()
    {
        ISingletonDisposer singleton;
        {
            using var myContainer = new DependencyInjectionContainer(false, default, default!);
            singleton = myContainer.Resolve<ISingletonDisposer>();
            singleton.ShouldBeOfType<DisposableSingleton>();
        }
        ((DisposableSingleton) singleton).WasDisposed.ShouldBeTrue();
    }

    [Test]
    public void DisposingLifetimeContainerDoesNotDisposeSingletons()
    {
        ISingletonDisposer singleton;
        using (var myContainer = new DependencyInjectionContainer(false, default, default!))
        {
            using (var lifetime = myContainer.BeginLifetimeScope())
            {
                singleton = lifetime.Resolve<ISingletonDisposer>();
            }

            singleton.ShouldBeOfType<DisposableSingleton>();
            ((DisposableSingleton) singleton).WasDisposed.ShouldBeFalse();
        }

        ((DisposableSingleton) singleton).WasDisposed.ShouldBeTrue();
    }

    [Test]
    public void DisposingLifetimeContainerDisposesScoped()
    {
        IScoped singleton;
        using var myContainer = new DependencyInjectionContainer(false, default, default!);
        using (var lifetime = myContainer.BeginLifetimeScope())
        {
            singleton = lifetime.Resolve<IScoped>();
        }

        singleton.ShouldBeOfType<Scoped>();
        singleton.WasDisposed.ShouldBeTrue();
    }

    [Test]
    public void DisposingContainerDoesNotDisposeUntrackedInstances()
    {
        IDisposer singleton;
        {
            using var myContainer = new DependencyInjectionContainer(false, default, default!);
            singleton = myContainer.Resolve<IDisposer>();
            singleton.ShouldBeOfType<DisposableNonSingleton>();
        }
        ((DisposableNonSingleton) singleton).WasDisposed.ShouldBeTrue();
    }

    [Test]
    public void DisposingContainerDoesNotDisposesUnreferencedSingletons()
    {
        using var myContainer = new DependencyInjectionContainer(false, default, default!);
    }

    [Test]
    public void ArrayExpressionsCollect()
    {
        m_container.Resolve<ArrayConsumer>().Arrays.Count().ShouldBe(3);
    }

    [Test]
    public void RequestedArraysArePresent()
    {
        Program.Method().Count().ShouldBe(3);
    }

    [Test]
    public void BooleanFallbackIsOverriden()
    {
        m_container.Resolve<IOverrideBoolean>().ShouldBeOfType<OverridingBoolean>();
    }

    [Test]
    public void TryResolveWithTypeArgumentsWorks()
    {
        m_container.TryResolve<IType>(out var type).ShouldBeTrue();
        type.ShouldBeOfType<Type>();
    }

    [Test]
    public void TryResolveWithTypeParameterWorks()
    {
        m_container.TryResolve(typeof(IType), out var type).ShouldBeTrue();
        type.ShouldBeOfType<Type>();
    }

    [Test]
    public void ClassesInsideOtherClassesCanBeInjected()
    {
        m_container.Resolve<Containing.Containee>();
    }
    [Test]
    public void ContainerMayCreateItself()
    {
        var newContainer = new DependencyInjectionContainer(m_container);
        var resolved = m_container.Resolve<IEnumerable<IArray>>();
        resolved.Count().ShouldBe(6);
        var nonInjected = m_container.Resolve<Inherited.NonInjectedClass>();
    }
    [Test]
    public void HierarchicalContainersResolveArraysProperly()
    {
        var newContainer = new DependencyInjectionContainer(m_container);
        newContainer.Resolve<ArrayConsumer>().Arrays.Count().ShouldBe(6);
    }
    [Test]
    public void HierarchicalContainersResolveUsesFallBackIfItCannotFindImplementation()
    {
        var newContainer = new DependencyInjectionContainer(new DummyContainer());
        newContainer.Resolve<string>().ShouldBe(DummyContainer.DummyText);
    }

    [Test]
    public void ContainerPropgatesRelevantBooleansCreateItself()
    {
        var baseContainer = new DependencyInjectionContainer(true, false, new());
        baseContainer.GetBoolean("A").ShouldBeFalse();
        baseContainer.GetBoolean("TestBool").ShouldBeTrue();

        var newContainer = new DependencyInjectionContainer(baseContainer);

        newContainer.GetBoolean("A").ShouldBeFalse();
        newContainer.GetBoolean("TestBool").ShouldBeTrue();
    }
    [Test]
    public void HierarchicalContainersPropgatesBooleansUnknownToIt()
    {
        var newContainer = new DependencyInjectionContainer(new DummyContainer());
        newContainer.GetBoolean("B").ShouldBe(true);
        newContainer.GetBoolean("C").ShouldBe(false);
    }

    // ── Nullable parameter tests ──────────────────────────────────────────────

    [Test]
    public void NullableUnregisteredParameterDefaultsToNull()
    {
        m_container.Resolve<NullableConsumer>().Optional.ShouldBeNull();
    }

    [Test]
    public void NullableRegisteredParameterIsResolved()
    {
        m_container.Resolve<NullablePresentConsumer>().Optional.ShouldBeOfType<NullablePresent>();
    }

    // ── Collection constructor parameter tests ────────────────────────────────

    [Test]
    public void ArrayConstructorParameterIsResolved()
    {
        m_container.Resolve<ArrayParameterConsumer>().Arrays.Length.ShouldBe(3);
    }

    [Test]
    public void ListConstructorParameterIsResolved()
    {
        m_container.Resolve<ListConsumer>().Arrays.Count.ShouldBe(3);
    }

    [Test]
    public void ImmutableArrayConstructorParameterIsResolved()
    {
        m_container.Resolve<ImmutableArrayConsumer>().Arrays.Length.ShouldBe(3);
    }

    [Test]
    public void ReadOnlySpanConstructorParameterIsResolved()
    {
        m_container.Resolve<ReadOnlySpanConsumer>().Count.ShouldBe(3);
    }

    // ── Cross-array reentrancy tests ──────────────────────────────────────────
    // Ensures that reentrancy guards are per-array-type, not global.  Resolving
    // IEnumerable<ICrossArrayA> triggers construction of CrossA3 which needs
    // IEnumerable<ICrossArrayB>.  That second resolution must not be blocked.

    [Test]
    public void CrossArrayReentrancyResolvesAllA()
    {
        var items = m_container.Resolve<CrossArrayConsumer>().Items.ToList();
        items.Count.ShouldBe(3);
    }

    [Test]
    public void CrossArrayReentrancyResolvesBInsideCrossA3()
    {
        var items = m_container.Resolve<CrossArrayConsumer>().Items.ToList();
        var crossA3 = items.OfType<CrossA3>().ShouldHaveSingleItem();
        crossA3.Deps.Count().ShouldBe(2);
    }

    // ── Inheritor + Base array tests ──────────────────────────────────────────

    [Test]
    public void InheritorAndBaseContainerMergeArrays()
    {
        var parent = new DependencyInjectionContainer(false, false, new NonInjectedClass());
        var child = new DependencyInjectionContainer(parent);
        // Inherited defines SplitBase1 + SplitBase2 (2 items per container).
        // Inheritor defines SplitInheritor1..3 (3 more per container).
        // Each standalone container has 5.  After merging, the child sees its own 5
        // plus the parent's 5 = 10.
        child.Resolve<SplitArrayConsumer>().Items.Count().ShouldBe(10);
    }

    [Test]
    public void BaseContainerSeesInheritorArraysAfterLinking()
    {
        var parent = new DependencyInjectionContainer(false, false, new NonInjectedClass());
        var child = new DependencyInjectionContainer(parent);
        // After linking, the parent's Inheritor is the child.  Resolving on the
        // parent should now include its own 5 plus the child's 5 = 10.
        parent.Resolve<SplitArrayConsumer>().Items.Count().ShouldBe(10);
    }

    private class DummyContainer : IContainer
    {
        public const string DummyText = "I am a bit of text";

        public static NonInjectedClass m_dummy = new();
        public IContainer? Base => null;

        public IContainer? Inheritor { get; set; } = null;

        public ILifetimeScope BeginLifetimeScope()
        {
            return this;
        }

        public void Dispose()
        {
        }

        public bool GetBoolean(string key)
        {
            return false;
        }

        public bool IsRegistered(System.Type type)
        {
            return true;
        }

        public bool IsRegistered<T>()
        {
            return true;
        }

        public T Resolve<T>()
        {
            if (typeof(T) == typeof(string)) return (T) (object) DummyText;
            return (T) (object) m_dummy;
        }

        public object Resolve(System.Type type)
        {
            if (type == typeof(string)) return DummyText;
            return m_dummy;
        }

        public bool TryResolve(System.Type type, out object? resolved)
        {
            resolved = null;
            if (type == typeof(string)) resolved = DummyText;
            return resolved != null;
        }

        public bool TryResolve<T>(out T? resolved)
        {
            resolved = default;
            if (typeof(T) == typeof(string)) resolved = (T) (object) DummyText;
            return resolved != null;
        }
        public IEnumerable<(string Key, bool Value)> GetBooleans()
        {
            return [("B", true), ("C", false)];
        }

    }
}