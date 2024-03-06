using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit.Abstractions;

namespace WeConfig.AutofacGenerator.Tests;

public class GeneratorTestsBase(ITestOutputHelper output)
{
    protected const string AutofacModule = @"
namespace WeConfig.Imaginary.Namespace;
public partial class AutofacModule : Autofac.Module
{
    private void SomeMethod(ContainerBuilder builder)
    {
        builder.RegisterType<A>().As<B>();
        builder.RegisterType<C>().As<D>();
    }
}
";

    protected static readonly string InjectionAttribute = File.ReadAllText("InjectAttribute.cs");

    protected void WriteTestOutput(GeneratorRunResult generatorResult)
    {
        output.WriteLine("DIAGNOSTICS");
        foreach (var diagnostic in generatorResult.Diagnostics)
        {
            output.WriteLine(diagnostic.GetMessage());
            output.WriteLine(diagnostic.Location.ToString());
            foreach (var loc in diagnostic.AdditionalLocations)
            {
                output.WriteLine(loc.ToString());
            }
        }

        output.WriteLine("GENERATED");
        foreach (var result in generatorResult.GeneratedSources)
        {
            output.WriteLine(result.HintName);
            output.WriteLine("---------------------------------------");
            output.WriteLine(result.SourceText.ToString());
            output.WriteLine("---------------------------------------");
        }
    }

    protected static GeneratorRunResult RunGenerator(Compilation inputCompilation)
    {
        // directly create an instance of the generator
        // (Note: in the compiler this is loaded from an assembly, and created via reflection at runtime)
        var generator = new FactoryGenerator.FactoryGenerator();

        // Create the driver that will control the generation, passing in our generator
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);

        // Run the generation pass
        // (Note: the generator driver itself is immutable, and all calls return an updated version of the driver that you should use for subsequent calls)
        driver = driver.RunGeneratorsAndUpdateCompilation(inputCompilation, out var outputCompilation, out var diagnostics);

        // Or we can look at the results directly:
        var runResult = driver.GetRunResult();
        // Or you can access the individual results on a by-generator basis
        var generatorResult = runResult.Results[0];
        return generatorResult;
    }

    protected static CSharpCompilation CreateCompilation(params string[] source)
        => CSharpCompilation.Create("compilation",
                                    source.Select(s => CSharpSyntaxTree.ParseText(s)),
                                    new[] {MetadataReference.CreateFromFile(typeof(Binder).GetTypeInfo().Assembly.Location)},
                                    new CSharpCompilationOptions(OutputKind.ConsoleApplication));

    protected static (string code, string expected) InjectType(string name, string[] ifaces, string ns = "SomeNameSpace", bool singleInstance = false, bool inherit = false, params string[] exceptions)
    {
        var expectationBuilder = new StringBuilder($"builder.RegisterType<{ns}.{name}>()");
        var ifaceBuilder = new StringBuilder();
        string IFaceDecl = string.Join(',', ifaces);
        foreach (var iface in ifaces)
        {
            ifaceBuilder.AppendLine($"public interface {iface} {{}}");
            if (!exceptions.Contains(iface))
            {
                expectationBuilder.Append($".As<{ns}.{iface}>()");
            }
        }

        if (ifaces.Length > 0)
        {
            IFaceDecl = ": " + IFaceDecl;
        }

        var sinsAttr = singleInstance ? ", Singleton" : string.Empty;
        var inheritAttr = inherit ? ", InheritInterfaces" : string.Empty;
        if (singleInstance)
        {
            expectationBuilder.Append(".SingleInstance()");
        }
        var code = $$"""

                     using FactoryGenerator;
                     namespace {{ns}};
                     {{ifaceBuilder}}
                     [Inject{{sinsAttr}}{{inheritAttr}}]
                     {{string.Join('\n', exceptions.Select(e => $"[ExceptAs<{e}>]"))}}
                     public class {{name}} {{IFaceDecl}} {}

                     """;
        return (code, expectationBuilder.ToString());
    }
}