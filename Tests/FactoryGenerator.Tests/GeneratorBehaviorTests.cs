using System;
using System.IO;
using System.Linq;
using FactoryGenerator;
using FactoryGenerator.Attributes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace FactoryGenerator.Tests;

public class GeneratorBehaviorTests
{
    [Test]
    public void UnregisteredTypedLoggersRemainDistinctExternalConstructorDependencies()
    {
        const string source = """
using FactoryGenerator.Attributes;
using Microsoft.Extensions.Logging;

namespace Sample
{
[Inject, Self]
public sealed class Worker(ILogger<Worker> workerLogger);

[Inject, Self]
public sealed class OtherWorker(ILogger<OtherWorker> otherLogger);
}
""";

        var (runResult, outputCompilation) = RunGenerator(CreateCompilation(source));
        runResult.Results[0].Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray()
            .ShouldBeEmpty();

        var constructor = runResult.Results[0].GeneratedSources
            .Single(result => result.HintName == "FactoryGenerator.DependencyInjectionContainer/Constructor.g.cs")
            .SourceText.ToString();
        constructor.ShouldContain("Microsoft.Extensions.Logging.ILogger<Sample.Worker> workerLogger");
        constructor.ShouldContain("Microsoft.Extensions.Logging.ILogger<Sample.OtherWorker> otherLogger");
    }

    [Test]
    public void ClosedTypedLoggerFactoriesSupportContainersScopesAndStaticResolution()
    {
        var assemblyName = "TypedLogging" + Guid.NewGuid().ToString("N");
        var source = $$"""
using System;
using FactoryGenerator.Attributes;
using Microsoft.Extensions.Logging;
using {{assemblyName}}.Generated;

namespace {{assemblyName}}
{
[Inject, Self]
public sealed class Worker(ILogger<Worker> logger)
{
    public ILogger<Worker> Logger { get; } = logger;
    public void Run() => Logger.LogInformation("working");
}

[Inject, Self]
public sealed class OtherWorker(ILogger<OtherWorker> logger)
{
    public ILogger<OtherWorker> Logger { get; } = logger;
    public void Run() => Logger.LogInformation("other");
}

[Inject, Self]
public sealed class LoggingRegistrations
{
    [Inject, Singleton, ExceptAs<ILogger>]
    public ILogger<Worker> WorkerLogger(ILoggerFactory loggerFactory)
        => loggerFactory.CreateLogger<Worker>();

    [Inject, Singleton, ExceptAs<ILogger>]
    public ILogger<OtherWorker> OtherLogger(ILoggerFactory loggerFactory)
        => loggerFactory.CreateLogger<OtherWorker>();
}

public static class StaticResolution
{
    public static Worker Create(ILoggerFactory loggerFactory)
        => Worker.Resolve(null, loggerFactory);

    public static Worker CreateCached(DependencyInjectionContainer container, ILoggerFactory loggerFactory)
        => Worker.Resolve(container, loggerFactory);
}
}
""";

        var (runResult, outputCompilation) = RunGenerator(CreateCompilation(assemblyName, source));
        runResult.Results[0].Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray()
            .ShouldBeEmpty();

        var assembly = System.Reflection.Assembly.Load(EmitAssembly(outputCompilation));
        var containerType = assembly.GetType($"{assemblyName}.Generated.DependencyInjectionContainer")!;
        var workerType = assembly.GetType($"{assemblyName}.Worker")!;
        var otherWorkerType = assembly.GetType($"{assemblyName}.OtherWorker")!;
        var staticResolution = assembly.GetType($"{assemblyName}.StaticResolution")!;
        var factory = new RecordingLoggerFactory();

        containerType.GetConstructors()
            .ShouldContain(ctor => ctor.GetParameters().Length == 1
                && ctor.GetParameters()[0].ParameterType == typeof(ILoggerFactory));

        using (var container = (IContainer)Activator.CreateInstance(containerType, factory)!)
        {
            container.Resolve<ILoggerFactory>().ShouldBeSameAs(factory);
            container.IsRegistered(typeof(ILogger<>).MakeGenericType(workerType)).ShouldBeTrue();
            container.IsRegistered(typeof(ILogger<>).MakeGenericType(otherWorkerType)).ShouldBeTrue();
            container.IsRegistered<Microsoft.Extensions.Logging.ILogger>().ShouldBeFalse();

            var worker = container.Resolve(workerType);
            var otherWorker = container.Resolve(otherWorkerType);
            var logger = workerType.GetProperty("Logger")!.GetValue(worker);
            logger.ShouldBeOfType(typeof(Logger<>).MakeGenericType(workerType));
            logger.ShouldBeSameAs(workerType.GetProperty("Logger")!.GetValue(container.Resolve(workerType)));
            otherWorkerType.GetProperty("Logger")!.GetValue(otherWorker)
                .ShouldBeOfType(typeof(Logger<>).MakeGenericType(otherWorkerType));
            workerType.GetMethod("Run")!.Invoke(worker, null);
            otherWorkerType.GetMethod("Run")!.Invoke(otherWorker, null);

            using var scope = container.BeginLifetimeScope();
            workerType.GetProperty("Logger")!.GetValue(scope.Resolve(workerType)).ShouldBeSameAs(logger);
            var cached = staticResolution.GetMethod("CreateCached")!.Invoke(null, [container, factory]);
            workerType.GetProperty("Logger")!.GetValue(cached).ShouldBeSameAs(logger);

            var uncached = staticResolution.GetMethod("Create")!.Invoke(null, [factory]);
            workerType.GetProperty("Logger")!.GetValue(uncached).ShouldNotBeSameAs(logger);
            factory.Categories.ShouldBe([
                $"{assemblyName}.Worker", $"{assemblyName}.OtherWorker", $"{assemblyName}.Worker"
            ]);
        }

        factory.DisposeCount.ShouldBe(0);
    }

    [Test]
    public void GenericFactoriesSpecializeTypedLoggerRegistrationsForClosedConstructorDependencies()
    {
        var assemblyName = "GenericTypedLogging" + Guid.NewGuid().ToString("N");
        var source = $$"""
using FactoryGenerator.Attributes;
using Microsoft.Extensions.Logging;
using {{assemblyName}}.Generated;

namespace {{assemblyName}}
{
[Inject, Self]
public sealed class Worker(ILogger<Worker> logger)
{
    public ILogger<Worker> Logger { get; } = logger;
}

[Inject, Self]
public sealed class OtherWorker(ILogger<OtherWorker> logger)
{
    public ILogger<OtherWorker> Logger { get; } = logger;
}

[Inject, Self]
public sealed class LoggingRegistrations
{
    [Inject, Singleton, ExceptAs<ILogger>]
    public ILogger<TCategory> CreateLogger<TCategory>(ILoggerFactory loggerFactory)
        => loggerFactory.CreateLogger<TCategory>();
}

public static class StaticResolution
{
    public static Worker Create(DependencyInjectionContainer container, ILoggerFactory loggerFactory)
        => Worker.Resolve(container, loggerFactory);

    public static Worker CreateUncached(ILoggerFactory loggerFactory)
        => Worker.Resolve(null, loggerFactory);
}
}
""";

        var (runResult, outputCompilation) = RunGenerator(CreateCompilation(assemblyName, source));
        runResult.Results[0].Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray()
            .ShouldBeEmpty();

        var assembly = System.Reflection.Assembly.Load(EmitAssembly(outputCompilation));
        var containerType = assembly.GetType($"{assemblyName}.Generated.DependencyInjectionContainer")!;
        var workerType = assembly.GetType($"{assemblyName}.Worker")!;
        var otherWorkerType = assembly.GetType($"{assemblyName}.OtherWorker")!;
        var staticResolution = assembly.GetType($"{assemblyName}.StaticResolution")!;
        var factory = new RecordingLoggerFactory();

        using (var container = (IContainer)Activator.CreateInstance(containerType, factory)!)
        {
            container.IsRegistered(typeof(ILogger<>).MakeGenericType(workerType)).ShouldBeTrue();
            container.IsRegistered(typeof(ILogger<>).MakeGenericType(otherWorkerType)).ShouldBeTrue();
            container.IsRegistered<Microsoft.Extensions.Logging.ILogger>().ShouldBeFalse();

            var worker = container.Resolve(workerType);
            var logger = workerType.GetProperty("Logger")!.GetValue(worker);
            logger.ShouldBeOfType(typeof(Logger<>).MakeGenericType(workerType));
            logger.ShouldBeSameAs(workerType.GetProperty("Logger")!.GetValue(container.Resolve(workerType)));

            var otherWorker = container.Resolve(otherWorkerType);
            otherWorkerType.GetProperty("Logger")!.GetValue(otherWorker)
                .ShouldBeOfType(typeof(Logger<>).MakeGenericType(otherWorkerType));

            var staticWorker = staticResolution.GetMethod("Create")!.Invoke(null, [container, factory]);
            workerType.GetProperty("Logger")!.GetValue(staticWorker).ShouldBeSameAs(logger);
            var uncachedWorker = staticResolution.GetMethod("CreateUncached")!.Invoke(null, [factory]);
            workerType.GetProperty("Logger")!.GetValue(uncachedWorker).ShouldNotBeSameAs(logger);

            factory.Categories.ShouldBe([
                $"{assemblyName}.Worker", $"{assemblyName}.OtherWorker", $"{assemblyName}.Worker"
            ]);
        }

        factory.DisposeCount.ShouldBe(0);
    }

    [Test]
    public void GenericFactoryMethodsCloseFromConstructorDependenciesAndHonorConstraints()
    {
        var assemblyName = "GenericFactory" + Guid.NewGuid().ToString("N");
        var source = $$"""
using FactoryGenerator.Attributes;
using {{assemblyName}}.Generated;

namespace {{assemblyName}}
{
public interface IDependency
{
}

[Inject, Singleton, Self]
public sealed class Dependency : IDependency
{
}

public interface ITypedInterface<T>
{
    IDependency Dependency { get; }
}

public sealed class TypedImplementation<T>(IDependency dependency) : ITypedInterface<T>
{
    public IDependency Dependency { get; } = dependency;
}

public sealed class Alpha
{
}

public sealed class Beta
{
}

[Inject, Self]
public sealed class GenericFactory
{
    [Inject, Singleton]
    public ITypedInterface<T> TypedFactoryMethod<T>(IDependency dependency)
        where T : class, new()
        => new TypedImplementation<T>(dependency);
}

[Inject, Self]
public sealed class Consumer(ITypedInterface<Alpha> alpha, ITypedInterface<Beta> beta)
{
    public ITypedInterface<Alpha> Alpha { get; } = alpha;
    public ITypedInterface<Beta> Beta { get; } = beta;
}

[Inject, Self]
public sealed class StringConsumer(ITypedInterface<string> typed)
{
}

public static class StaticResolution
{
    public static Consumer Create(DependencyInjectionContainer container)
        => Consumer.Resolve(container);
}
}
""";

        var (runResult, outputCompilation) = RunGenerator(CreateCompilation(assemblyName, source));
        runResult.Results[0].Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray()
            .ShouldBeEmpty();

        var generated = string.Join(
            Environment.NewLine,
            runResult.Results[0].GeneratedSources.Select(result => result.SourceText.ToString()));
        generated.ShouldContain("TypedFactoryMethod<" + assemblyName + ".Alpha>");
        generated.ShouldContain("TypedFactoryMethod<" + assemblyName + ".Beta>");
        generated.ShouldContain(assemblyName + ".ITypedInterface<string> typed");

        var assembly = System.Reflection.Assembly.Load(EmitAssembly(outputCompilation));
        var containerType = assembly.GetType($"{assemblyName}.Generated.DependencyInjectionContainer")!;
        var consumerType = assembly.GetType($"{assemblyName}.Consumer")!;
        var staticResolution = assembly.GetType($"{assemblyName}.StaticResolution")!;
        var dependencyType = assembly.GetType($"{assemblyName}.Dependency")!;
        var typedInterfaceType = assembly.GetType($"{assemblyName}.ITypedInterface`1")!;
        var typedStringType = typedInterfaceType.MakeGenericType(typeof(string));
        var typedStringImplementationType = assembly.GetType($"{assemblyName}.TypedImplementation`1")!
            .MakeGenericType(typeof(string));
        var typedString = Activator.CreateInstance(
            typedStringImplementationType,
            Activator.CreateInstance(dependencyType))!;
        var containerConstructor = containerType.GetConstructors()
            .Single(constructor => constructor.GetParameters().Any(parameter => parameter.ParameterType == typedStringType));
        using var container = (IContainer)containerConstructor.Invoke([typedString]);

        var dependency = container.Resolve(assembly.GetType($"{assemblyName}.IDependency")!);
        var consumer = container.Resolve(consumerType);
        var alpha = consumerType.GetProperty("Alpha")!.GetValue(consumer)!;
        var beta = consumerType.GetProperty("Beta")!.GetValue(consumer)!;
        alpha.GetType().GetProperty("Dependency")!.GetValue(alpha).ShouldBeSameAs(dependency);
        beta.GetType().GetProperty("Dependency")!.GetValue(beta).ShouldBeSameAs(dependency);
        alpha.ShouldBeSameAs(consumerType.GetProperty("Alpha")!.GetValue(container.Resolve(consumerType)));
        beta.ShouldBeSameAs(consumerType.GetProperty("Beta")!.GetValue(container.Resolve(consumerType)));
        alpha.ShouldNotBeSameAs(beta);

        var staticConsumer = staticResolution.GetMethod("Create")!.Invoke(null, [container]);
        consumerType.GetProperty("Alpha")!.GetValue(staticConsumer).ShouldBeSameAs(alpha);
        consumerType.GetProperty("Beta")!.GetValue(staticConsumer).ShouldBeSameAs(beta);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public System.Collections.Generic.List<string> Categories { get; } = [];
        public int DisposeCount { get; private set; }

        public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
        public void Dispose() => DisposeCount++;
    }

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

        generatorResult.Exception.ShouldBeNull();
        var diagnostic = generatorResult.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("FG004");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("Multiple externally provided values of the same type");
        diagnostic.GetMessage().ShouldContain("Sample.ExternalValue");
    }

    [Test]
    public void GeneratorPicksUpClassesViaAttributeAppliedToImplementedInterface()
    {
        // [Inject] is declared on the interface, not the implementing class. FindMethods must
        // still discover ImplicitlyInjected by checking type.AllInterfaces for attributes, not just
        // the type's own attributes — pins down this (previously untested) discovery path so it
        // can't silently regress in a future refactor of the type-scanning pipeline.
        const string source = """
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              [Inject]
                              public interface IMarker
                              {
                              }

                              public class ImplicitlyInjected : IMarker
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
        generatedSource.ShouldContain("ImplicitlyInjected");
    }

    [Test]
    public void GeneratorSupportsIdenticallyNamedContainerParametersAcrossDifferentInjections()
    {
        // Two unrelated injections both take an IContainer-typed external constructor parameter
        // with the exact same identifier name ("lifeTimeScope"). Both end up as
        // declarations[parameter.Name] = "..." in GenerateCode; since that's a Dictionary, the
        // second assignment must just overwrite the first (same key) rather than producing two
        // separate AddSource calls for the same hint name — pins down that this legitimate,
        // realistic naming collision within a single generator run doesn't throw.
        const string source = """
                              using FactoryGenerator;
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              public interface IServiceA
                              {
                              }

                              public interface IServiceB
                              {
                              }

                              [Inject]
                              public class ServiceA : IServiceA
                              {
                                  public ServiceA(IContainer lifeTimeScope)
                                  {
                                  }
                              }

                              [Inject]
                              public class ServiceB : IServiceB
                              {
                                  public ServiceB(IContainer lifeTimeScope)
                                  {
                                  }
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
    }

    [Test]
    public void GeneratorSupportsExternalParameterNamesDifferingOnlyByCase()
    {
        // Real-world reproduction: two unrelated injections each take an IContainer-typed external
        // constructor parameter whose names differ ONLY by case ("lifeTimeScope" vs "lifetimeScope").
        // Roslyn's SourceProductionContext.AddSource compares hint names with
        // StringComparer.OrdinalIgnoreCase, but GenerateCode's `declarations` dictionary used the
        // default (ordinal, case-SENSITIVE) comparer — so both names survived as separate dictionary
        // entries (correctly, since they're both valid, distinct C# identifiers) but then crashed
        // the whole generator with "hintName ... must be unique within a generator" as soon as BOTH
        // tried to become separate generated files. Pins down the fix: colliding declarations must
        // be merged into a single generated file instead.
        const string source = """
                              using FactoryGenerator;
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              public interface IServiceA
                              {
                              }

                              public interface IServiceB
                              {
                              }

                              [Inject]
                              public class ServiceA : IServiceA
                              {
                                  public ServiceA(IContainer lifeTimeScope)
                                  {
                                  }
                              }

                              [Inject]
                              public class ServiceB : IServiceB
                              {
                                  public ServiceB(IContainer lifetimeScope)
                                  {
                                  }
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
        generatedSource.ShouldContain("IContainer lifeTimeScope => this;");
        generatedSource.ShouldContain("IContainer lifetimeScope => this;");
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
    public void BooleanOnlyImplementationsThrowInsteadOfResolvingNull()
    {
        var assemblyName = "BooleanOnly" + Guid.NewGuid().ToString("N");
        var source = $$"""
                       using FactoryGenerator.Attributes;

                       namespace {{assemblyName}}
                       {
                       public interface IService
                       {
                       }

                       [Inject, Boolean("enabled")]
                       public class EnabledService : IService
                       {
                       }
                       }
                       """;

        var compilation = CreateCompilation(assemblyName, source);
        var (runResult, outputCompilation) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
                         .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                         .ToArray()
                         .ShouldBeEmpty();

        var expectedMessage = $"Cannot resolve {assemblyName}.IService without a matching implementation";
        var declarations = generatorResult.GeneratedSources
                                          .Single(sourceResult => sourceResult.HintName == $"FactoryGenerator.Declarations/{assemblyName}_IService().g.cs")
                                          .SourceText
                                          .ToString();
        declarations.ShouldContain(expectedMessage);
        declarations.ShouldNotContain("null!");

        var staticExtensions = generatorResult.GeneratedSources
                                              .Single(sourceResult => sourceResult.HintName == $"FactoryGenerator.StaticExtensions/{assemblyName}_IServiceExtensions.g.cs")
                                              .SourceText
                                              .ToString();
        staticExtensions.ShouldContain(expectedMessage);

        var assembly = System.Reflection.Assembly.Load(EmitAssembly(outputCompilation));
        var containerType = assembly.GetType($"{assemblyName}.Generated.DependencyInjectionContainer");
        var serviceType = assembly.GetType($"{assemblyName}.IService");

        containerType.ShouldNotBeNull();
        serviceType.ShouldNotBeNull();

        var container = (IContainer) Activator.CreateInstance(containerType!, [false])!;
        var exception = Should.Throw<InvalidOperationException>(() => container.Resolve(serviceType!));
        exception.Message.ShouldContain(expectedMessage);
    }

    [Test]
    public void GeneratorDetectsCyclesThroughInjectedMethods()
    {
        const string source = """
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              public interface IResult
                              {
                              }

                              public class Result : IResult
                              {
                              }

                              public interface IFactory
                              {
                                  [Inject]
                                  IResult Create();
                              }

                              [Inject]
                              public class Factory : IFactory
                              {
                                  public Factory(IResult result)
                                  {
                                  }

                                  public IResult Create() => new Result();
                              }
                              }
                              """;

        var compilation = CreateCompilation(source);
        var (runResult, _) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        var diagnostic = generatorResult.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("FG003");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("Cyclic Dependency Detected");
        diagnostic.GetMessage().ShouldContain("Sample.IResult");
        diagnostic.GetMessage().ShouldContain("Sample.IFactory");
    }

    [Test]
    public void GeneratorDetectsCyclesThroughInjectedProperties()
    {
        const string source = """
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              public interface IResult
                              {
                              }

                              public class Result : IResult
                              {
                              }

                              public interface IFactory
                              {
                                  [Inject]
                                  IResult Value { get; }
                              }

                              [Inject]
                              public class Factory : IFactory
                              {
                                  public Factory(IResult result)
                                  {
                                  }

                                  public IResult Value => new Result();
                              }
                              }
                              """;

        var compilation = CreateCompilation(source);
        var (runResult, _) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        var diagnostic = generatorResult.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("FG003");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("Cyclic Dependency Detected");
        diagnostic.GetMessage().ShouldContain("Sample.IResult");
        diagnostic.GetMessage().ShouldContain("Sample.IFactory");
    }

    [Test]
    public void InjectedMethodsSurfaceExternalParametersAndHonorOptionalAndParamsArguments()
    {
        var assemblyName = "InjectedMethod" + Guid.NewGuid().ToString("N");
        var source = $$"""
                       using FactoryGenerator.Attributes;

                       namespace {{assemblyName}}
                       {
                       public sealed class ExternalInput
                       {
                           public ExternalInput(string name)
                           {
                               Name = name;
                           }

                           public string Name { get; }
                       }

                       public interface IPart
                       {
                       }

                       [Inject]
                       public class PartOne : IPart
                       {
                       }

                       [Inject]
                       public class PartTwo : IPart
                       {
                       }

                       public interface IResult
                       {
                       }

                       public sealed class Result : IResult
                       {
                           public Result(string summary, int partCount)
                           {
                               Summary = summary;
                               PartCount = partCount;
                           }

                           public string Summary { get; }
                           public int PartCount { get; }
                       }

                       public interface IFactory
                       {
                           [Inject]
                           IResult Create(ExternalInput input, string label = "default", params IPart[] parts);
                       }

                       [Inject]
                       public class Factory : IFactory
                       {
                           public IResult Create(ExternalInput input, string label = "default", params IPart[] parts)
                           {
                               return new Result(input.Name + ":" + label, parts.Length);
                           }
                       }
                       }
                       """;

        var compilation = CreateCompilation(assemblyName, source);
        var (runResult, outputCompilation) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
                         .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                         .ToArray()
                         .ShouldBeEmpty();

        var assembly = System.Reflection.Assembly.Load(EmitAssembly(outputCompilation));
        var containerType = assembly.GetType($"{assemblyName}.Generated.DependencyInjectionContainer");
        var externalType = assembly.GetType($"{assemblyName}.ExternalInput");
        var serviceType = assembly.GetType($"{assemblyName}.IResult");

        containerType.ShouldNotBeNull();
        externalType.ShouldNotBeNull();
        serviceType.ShouldNotBeNull();

        var constructor = containerType!.GetConstructors()
                                        .Single(ctor =>
                                        {
                                            var parameters = ctor.GetParameters();
                                            return parameters.Length == 1 && parameters[0].ParameterType == externalType;
                                        });

        var external = Activator.CreateInstance(externalType!, "runtime");
        var container = (IContainer) constructor.Invoke([external!]);
        var resolved = container.Resolve(serviceType!);

        resolved.GetType().GetProperty("Summary")!.GetValue(resolved).ShouldBe("runtime:default");
        resolved.GetType().GetProperty("PartCount")!.GetValue(resolved).ShouldBe(2);
    }

    [Test]
    public void InjectedConstructorsHonorOptionalAndParamsArguments()
    {
        var assemblyName = "InjectedConstructor" + Guid.NewGuid().ToString("N");
        var source = $$"""
                       using FactoryGenerator.Attributes;

                       namespace {{assemblyName}}
                       {
                       public interface IPart
                       {
                       }

                       [Inject]
                       public class PartOne : IPart
                       {
                       }

                       [Inject]
                       public class PartTwo : IPart
                       {
                       }

                       [Inject, Self]
                       public sealed class Consumer
                       {
                           public Consumer(string label = "default", params IPart[] parts)
                           {
                               Summary = label;
                               PartCount = parts.Length;
                           }

                           public string Summary { get; }
                           public int PartCount { get; }
                       }
                       }
                       """;

        var compilation = CreateCompilation(assemblyName, source);
        var (runResult, outputCompilation) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        outputCompilation.GetDiagnostics()
                         .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                         .ToArray()
                         .ShouldBeEmpty();

        var assembly = System.Reflection.Assembly.Load(EmitAssembly(outputCompilation));
        var containerType = assembly.GetType($"{assemblyName}.Generated.DependencyInjectionContainer");
        var consumerType = assembly.GetType($"{assemblyName}.Consumer");

        containerType.ShouldNotBeNull();
        consumerType.ShouldNotBeNull();

        var container = (IContainer) Activator.CreateInstance(containerType!)!;
        var resolved = container.Resolve(consumerType!);

        resolved.GetType().GetProperty("Summary")!.GetValue(resolved).ShouldBe("default");
        resolved.GetType().GetProperty("PartCount")!.GetValue(resolved).ShouldBe(2);
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

    [Test]
    public void GeneratorReportsDiagnosticWhenInjectedMemberHasNoSource()
    {
        const string source = """
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              public interface IResult
                              {
                              }

                              public interface IFactory
                              {
                                  [Inject]
                                  IResult Create();
                              }
                              }
                              """;

        var compilation = CreateCompilation(source);
        var (runResult, _) = RunGenerator(compilation);
        var generatorResult = runResult.Results[0];

        generatorResult.Exception.ShouldBeNull();
        var diagnostic = generatorResult.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Id.ShouldBe("FG001");
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.GetMessage().ShouldContain("Sample.IFactory");
    }

    [Test]
    public void GeneratorDoesNotRegenerateForUnrelatedEdits()
    {
        const string source = """
                              using FactoryGenerator.Attributes;

                              namespace Sample
                              {
                              public interface IService
                              {
                              }

                              [Inject]
                              public class Service : IService
                              {
                              }
                              }
                              """;

        var compilation = CreateCompilation(source);
        var parseOptions = (CSharpParseOptions) compilation.SyntaxTrees.First().Options;
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new global::FactoryGenerator.FactoryGenerator().AsSourceGenerator()],
            parseOptions: parseOptions,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        driver = driver.RunGenerators(compilation);
        driver.GetRunResult().Results[0].Exception.ShouldBeNull();

        var edited = compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText("namespace Sample { internal class Unrelated { } }", parseOptions));
        driver = driver.RunGenerators(edited);
        var result = driver.GetRunResult().Results[0];

        result.Exception.ShouldBeNull();
        result.TrackedSteps.ShouldContainKey("Analysis");
        result.TrackedSteps["Analysis"]
              .SelectMany(step => step.Outputs)
              .ShouldAllBe(output => output.Reason == IncrementalStepRunReason.Cached || output.Reason == IncrementalStepRunReason.Unchanged);
        result.TrackedOutputSteps
              .SelectMany(pair => pair.Value)
              .SelectMany(step => step.Outputs)
              .ShouldAllBe(output => output.Reason == IncrementalStepRunReason.Cached || output.Reason == IncrementalStepRunReason.Unchanged);
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
        var references = ((string?) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES"))!
                         .Split(Path.PathSeparator)
                         .Where(path => !excludedAssemblies.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal))
                         .Select(path => (MetadataReference) MetadataReference.CreateFromFile(path))
                         .ToList();

        references.Add(MetadataReference.CreateFromFile(typeof(InjectAttribute).Assembly.Location));
        references.AddRange(additionalReferences);

        return CSharpCompilation.Create(
            assemblyName: assemblyName,
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        return CreateCompilation("GeneratorBehaviorTests", source);
    }

    private static (GeneratorDriverRunResult RunResult, Compilation OutputCompilation) RunGenerator(CSharpCompilation compilation)
    {
        var parseOptions = (CSharpParseOptions) compilation.SyntaxTrees.First().Options;
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

    private static byte[] EmitAssembly(Compilation compilation)
    {
        using var stream = new MemoryStream();
        var result = compilation.Emit(stream);
        result.Success.ShouldBeTrue(string.Join(Environment.NewLine, result.Diagnostics));
        return stream.ToArray();
    }
}