using Microsoft.CodeAnalysis;
using Shouldly;
using Xunit.Abstractions;

namespace WeConfig.AutofacGenerator.Tests;

public class InjectionDetectionTests(ITestOutputHelper output) : GeneratorTestsBase(output)
{
    private readonly ITestOutputHelper m_output = output;

    [Fact]
    public void PickupSingleInjection()
    {
        // Create the 'input' compilation that the generator will act on
        var (injection, expected) = InjectType("SomeType", ["ISomeInterface"]);
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, injection);

        var expectedConstruction = "typeof(SomeNameSpace.ISomeInterface),SomeNameSpace_ISomeInterface";
        var expectedDeclaration =
            "private SomeNameSpace.ISomeInterface SomeNameSpace_ISomeInterface() => SomeNameSpace_SomeType()";

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldContain(expectedConstruction);
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleInjectionWithSingleInstance()
    {
        // Create the 'input' compilation that the generator will act on
        var (injection, expected) = InjectType("SomeType", ["ISomeInterface"], singleInstance: true);
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, injection);


        var expectedConstruction = "typeof(SomeNameSpace.ISomeInterface),SomeNameSpace_ISomeInterface";
        var expectedDeclaration =
            "private SomeNameSpace.ISomeInterface SomeNameSpace_ISomeInterface() => SomeNameSpace_SomeType()";

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldContain(expectedConstruction);
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleInjectionWithInheritedInterface()
    {
        // Create the 'input' compilation that the generator will act on
        var code = @"
using FactoryGenerator;
namespace SomeNameSpace;
public interface ISomeInterface
public class SomeType : ISomeInterface {}
[Inject, InheritInterfaces]
public class SomeOtherType : SomeType  {}
";
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, code);


        var expectedConstruction = "typeof(SomeNameSpace.ISomeInterface),SomeNameSpace_ISomeInterface";
        var expectedDeclaration =
            "private SomeNameSpace.ISomeInterface SomeNameSpace_ISomeInterface() => SomeNameSpace_SomeOtherType()";

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldContain(expectedConstruction);
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleInjectionWithComplexConstructor()
    {
        // Create the 'input' compilation that the generator will act on
        var code = """
                   using FactoryGenerator;
                   namespace NS;
                   [Inject]
                   public interface I1 {}
                   public class T1(string A) : I1  {}
                   [Inject]
                   public interface I2 {}
                   public class T2(I1 one, string B) : I2  {}
                   public interface I3 {}
                   [Inject]
                   public class T3(I1 one, I2 two, string C) : I3  {}
                   public interface I4 {}
                   [Inject]
                   public class T4(I1 one, I2 two, I3 three, string D) : I4  {}
                   """;
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, code);

        var expectedConstruction = "{ typeof(NS.I4),NS_I4 }";
        var expectedDeclaration = "private NS.T4 NS_T4() => new NS.T4(NS_I1(), NS_I2(), NS_I3(), D);";

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldContain(expectedConstruction);
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleAsInjection()
    {
        // Create the 'input' compilation that the generator will act on
        var code = """
                   using FactoryGenerator;
                   namespace SomeNameSpace;
                   public interface ISomeInterface {}
                   [Inject, As<ISomeInterface>]
                   public class SomeType {}
                   """;
        var expectedConstruction = "typeof(SomeNameSpace.ISomeInterface),SomeNameSpace_ISomeInterface";
        var expectedDeclaration = "private SomeNameSpace.ISomeInterface SomeNameSpace_ISomeInterface() => SomeNameSpace_SomeType()";
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, code);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldContain(expectedConstruction);
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleInjectionWithBoolean()
    {
        // Create the 'input' compilation that the generator will act on
        var code = $$"""
                     using FactoryGenerator;
                     namespace SomeNameSpace;
                     public interface ISomeInterface {}
                     [Inject, Singleton]
                     [Boolean("someBoolean")]
                     public class SomeOtherType : ISomeInterface  {}
                     public interface IFallbackFactory {
                        [Inject, Singleton]
                        ISomeInterface SomeMethod();
                     }
                     """;
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, code);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        var expectedDeclaration =
            "someBoolean ? SomeNameSpace_SomeOtherType() : SomeNameSpace_ISomeInterfaceSomeMethod()";
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleInjectionWithMoreBooleans()
    {
        // Create the 'input' compilation that the generator will act on
        var code = $$"""
                     using FactoryGenerator;
                     namespace SomeNameSpace;
                     public interface ISomeInterface {}
                     [Inject, Singleton]
                     [Boolean("someBoolean")]
                     public class SomeOtherType : ISomeInterface  {}
                     public interface IFallbackFactory {
                        [Inject, Singleton]
                        [Boolean("someOther")]
                        ISomeInterface SomeMethod();
                     }
                     [Inject, Singleton]
                     public class Fallback : ISomeInterface {}
                     """;
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, code);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        var expectedDeclaration =
            "someBoolean ? SomeNameSpace_SomeOtherType() : someOther ? SomeNameSpace_ISomeInterfaceSomeMethod() : SomeNameSpace_Fallback();";
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void PickupSingleInjectionFromMethod()
    {
        // Create the 'input' compilation that the generator will act on
        var code = $$"""
                     using FactoryGenerator;
                     namespace SomeNameSpace;
                     public interface SomeThirdType {}
                     public interface ISomeInterface {
                         [Inject]
                         SomeThirdType SomeMethod();
                     }
                     [Inject]
                     public class SomeOtherType : ISomeInterface  {
                         ISomeInterface SomeMethod() => return this;
                     }

                     """;
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, code);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        var expectedDeclaration = "SomeNameSpace_ISomeInterface().SomeMethod()";
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldContain(expectedDeclaration);
    }

    [Fact]
    public void DoNotPickupNonInjection()
    {
        // Create the 'input' compilation that the generator will act on
        var nonInjection = """
                           namespace ANamespace;
                           public class SomeOtherType : SomeOtherPlace {}
                           """;
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, nonInjection);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[0].ToString()!.ShouldNotContain("SomeOtherType");
    }

    [Fact]
    public void PickupSingleInjectionWithIDisposable()
    {
        // Create the 'input' compilation that the generator will act on
        var (injection, expected) = InjectType("SomeType", ["SomeInterface", "IDisposable"], "MyNameSpace");
        m_output.WriteLine(injection);
        m_output.WriteLine("-----------------------");
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, injection);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldNotContain("IDisposable");
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldNotContain("IDisposable");
    }

    [Fact]
    public void PickupSingleInjectionWithException()
    {
        // Create the 'input' compilation that the generator will act on
        var (injection, expected) = InjectType("SomeType", ["SomeInterface", "IDontWantThis"], "MyNameSpace", false,
                                               false, "IDontWantThis");
        m_output.WriteLine(injection);
        m_output.WriteLine("-----------------------");
        Compilation inputCompilation = CreateCompilation(AutofacModule, InjectionAttribute, injection);

        var generatorResult = RunGenerator(inputCompilation);
        WriteTestOutput(generatorResult);
        generatorResult.Diagnostics.Length.ShouldBe(0);
        generatorResult.GeneratedSources.Length.ShouldBe(4);
        generatorResult.GeneratedSources[1].SourceText.ToString().ShouldNotContain("IDontWantThis");
        generatorResult.GeneratedSources[2].SourceText.ToString().ShouldNotContain("IDontWantThis");
    }
}