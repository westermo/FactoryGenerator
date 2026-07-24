using FactoryGenerator;
using Inherited;
using Inheritor.Generated;
using Shouldly;

namespace FactoryGenerator.Tests;

public class ContainerRegistryTests
{
    [Fact]
    public void ContainerEntryPointRegistersOnModuleLoad()
    {
        // The Inheritor assembly's ModuleInitializer should have already registered
        // its container factory in ContainerRegistry when the assembly was loaded.
        ContainerRegistry.RegisteredAssemblies.ShouldContain("Inheritor");
    }

    [Fact]
    public void ContainerEntryPointCreateBuildsWorkingContainer()
    {
        // Create a base container
        var baseContainer = new DependencyInjectionContainer(default, default, new NonInjectedClass());

        // Use the static entry point to create a chained container
        var chained = ContainerEntryPoint.Create(baseContainer);

        chained.ShouldNotBeNull();
        chained.ShouldBeAssignableTo<IContainer>();
    }

    [Fact]
    public void BuildChainCreatesWorkingContainerPipeline()
    {
        // Create a base container
        var baseContainer = new DependencyInjectionContainer(default, default, new NonInjectedClass());

        // Build a chain using the registry
        var final = ContainerRegistry.BuildChain(baseContainer, new[] { "Inheritor" });

        // The final container should be able to resolve types from the base
        final.Resolve<IType>().ShouldNotBeNull();
        final.Resolve<ISingleton>().ShouldNotBeNull();
    }

    [Fact]
    public void ContainerEntryPointAssemblyNameIsCorrect()
    {
        ContainerEntryPoint.AssemblyName.ShouldBe("Inheritor");
    }
}
