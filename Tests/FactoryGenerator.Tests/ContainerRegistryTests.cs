using System.Runtime.CompilerServices;
using FactoryGenerator;
using Inherited;
using Inheritor.Generated;
using Shouldly;

namespace FactoryGenerator.Tests;

public class ContainerRegistryTests
{
    [Test]
    public void ContainerEntryPointRegistersOnModuleLoad()
    {
        EnsureContainerEntryPointModuleInitialized();

        ContainerRegistry.RegisteredAssemblies.ShouldContain("Inheritor");
    }

    [Test]
    public void ContainerEntryPointCreateBuildsWorkingContainer()
    {
        EnsureContainerEntryPointModuleInitialized();

        // Create a base container
        var baseContainer = new DependencyInjectionContainer(default, default, new NonInjectedClass());

        // Use the static entry point to create a chained container
        var chained = ContainerEntryPoint.Create(baseContainer);

        chained.ShouldNotBeNull();
        chained.ShouldBeAssignableTo<IContainer>();
    }

    [Test]
    public void BuildChainCreatesWorkingContainerPipeline()
    {
        EnsureContainerEntryPointModuleInitialized();

        // Create a base container
        var baseContainer = new DependencyInjectionContainer(default, default, new NonInjectedClass());

        // Build a chain using the registry
        var final = ContainerRegistry.BuildChain(baseContainer, new[] { "Inheritor" });

        // The final container should be able to resolve types from the base
        final.Resolve<IType>().ShouldNotBeNull();
        final.Resolve<ISingleton>().ShouldNotBeNull();
    }

    [Test]
    public void BuildChainWithoutAssemblyListSkipsCurrentContainerAssembly()
    {
        EnsureContainerEntryPointModuleInitialized();
        var baseContainer = new DependencyInjectionContainer(default, default, new NonInjectedClass());

        var final = ContainerRegistry.BuildChain(baseContainer);

        ReferenceEquals(final, baseContainer).ShouldBeTrue();
        final.Inheritor.ShouldBeNull();
    }

    [Test]
    public void BuildChainWithExplicitAssemblyListSkipsCurrentContainerAssembly()
    {
        EnsureContainerEntryPointModuleInitialized();
        var baseContainer = new DependencyInjectionContainer(default, default, new NonInjectedClass());

        var final = ContainerRegistry.BuildChain(baseContainer, new[] { "Inheritor" });

        ReferenceEquals(final, baseContainer).ShouldBeTrue();
        final.Inheritor.ShouldBeNull();
    }

    [Test]
    public void ContainerEntryPointAssemblyNameIsCorrect()
    {
        ContainerEntryPoint.AssemblyName.ShouldBe("Inheritor");
    }

    private static void EnsureContainerEntryPointModuleInitialized()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(ContainerEntryPoint).Module.ModuleHandle);
    }
}
