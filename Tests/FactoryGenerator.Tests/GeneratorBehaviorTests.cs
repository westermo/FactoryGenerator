using System;
using System.IO;
using System.Linq;
using FactoryGenerator.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;

namespace FactoryGenerator.Tests;

public class GeneratorBehaviorTests
{
    [Test]
    public void GeneratorRejectsMultipleExternalValuesOfSameType()
    {
        const string source = """
using FactoryGenerator.Attributes;

namespace Sample
{
public interface IService
{
}

public class ExternalValue
{
}

[Inject]
public class FirstConsumer : IService
{
    public FirstConsumer(ExternalValue first)
    {
    }
}

[Inject, Self]
public class SecondConsumer
{
    public SecondConsumer(ExternalValue second)
    {
    }
}
}
""";

        var compilation = CreateCompilation(source);
        var (runResult, _) = RunGenerator(compilation);
        runResult.Results.Length.ShouldBe(1);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldNotBeNull();
        generatorResult.Exception!.Message.ShouldContain("Multiple externally provided values of the same type");
        generatorResult.Exception.Message.ShouldContain("Sample.ExternalValue");
    }

    [Test]
    public void GeneratorSupportsBooleanKeysThatAreNotIdentifiers()
    {
        const string source = """
using FactoryGenerator.Attributes;

namespace Sample
{
public interface IService
{
}

[Inject, Boolean("feature-flag")]
public class EnabledService : IService
{
}

[Inject]
public class FallbackService : IService
{
}
}
""";

        var compilation = CreateCompilation(source);
        var (runResult, outputCompilation) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray()
            .ShouldBeEmpty();

        var generatedSource = string.Join(Environment.NewLine, generatorResult.GeneratedSources.Select(sourceResult => sourceResult.SourceText.ToString()));
        generatedSource.ShouldContain("\"feature-flag\"");
        generatedSource.ShouldNotContain("bool feature-flag");
        generatedSource.ShouldContain("Resolve(DependencyInjectionContainer? container, bool boolean_feature_flag)");
    }

    [Test]
    public void AssemblyPriorityCanOverrideProjectGraphPrecedence()
    {
        var baseAssemblyName = "PriorityBase" + Guid.NewGuid().ToString("N");
        var derivedAssemblyName = "PriorityDerived" + Guid.NewGuid().ToString("N");

        var baseSource = $$"""
using FactoryGenerator.Attributes;

[assembly: InjectionPriority(9)]

namespace {{baseAssemblyName}}
{
public interface IService
{
}

[Inject]
public class BaseService : IService
{
}
}
""";

        var derivedSource = $$"""
using FactoryGenerator.Attributes;
using {{baseAssemblyName}};

namespace {{derivedAssemblyName}}
{
[Inject]
public class DerivedService : IService
{
}
}
""";

        var baseCompilation = CreateCompilation(baseAssemblyName, baseSource);
var (baseReference, _) = EmitReference(baseCompilation);
        var derivedCompilation = CreateCompilation(derivedAssemblyName, derivedSource, baseReference);

        var (runResult, outputCompilation) = RunGenerator(derivedCompilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray()
            .ShouldBeEmpty();

        var serviceMemberName = baseAssemblyName + "_IService()";
        var prioritizedImplementationMemberName = baseAssemblyName + "_BaseService()";
        var nonPrioritizedImplementationMemberName = derivedAssemblyName + "_DerivedService()";
        var generatedSource = string.Join(Environment.NewLine, generatorResult.GeneratedSources.Select(sourceResult => sourceResult.SourceText.ToString()));
        generatedSource.ShouldContain($"internal {baseAssemblyName}.IService {serviceMemberName} => {prioritizedImplementationMemberName};");
        generatedSource.ShouldNotContain($"internal {baseAssemblyName}.IService {serviceMemberName} => {nonPrioritizedImplementationMemberName};");
    }

    private static CSharpCompilation CreateCompilation(string assemblyName, string source, params MetadataReference[] additionalReferences)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        var excludedAssemblies = new[]
        {
            "Benchmarks",
            "FactoryGenerator",
            "FactoryGenerator.Attributes",
            "FactoryGenerator.Extensions.AspNetCore",
            "FactoryGenerator.Extensions.AspNetCore.Tests",
            "FactoryGenerator.Tests",
            "Inherited",
            "Inheritor",
            "TestWebApp"
        };
        var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
            .Split(Path.PathSeparator)
            .Where(path => !excludedAssemblies.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(InjectAttribute).Assembly.Location));
        references.AddRange(additionalReferences);

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        return CreateCompilation("GeneratorBehaviorTests", source);
    }

    private static (GeneratorDriverRunResult RunResult, Compilation OutputCompilation) RunGenerator(CSharpCompilation compilation)
    {
        var parseOptions = (CSharpParseOptions)compilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new global::FactoryGenerator.FactoryGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);
        return (driver.GetRunResult(), outputCompilation);
    }

    private static (MetadataReference Reference, byte[] Image) EmitReference(CSharpCompilation compilation)
    {
        var image = EmitAssembly(compilation);
        return (MetadataReference.CreateFromImage(image), image);
    }

    private static byte[] EmitAssembly(CSharpCompilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }
}
