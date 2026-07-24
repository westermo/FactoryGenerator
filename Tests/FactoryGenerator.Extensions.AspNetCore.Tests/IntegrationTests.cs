using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using FactoryGenerator.Attributes;

namespace FactoryGenerator.Extensions.AspNetCore.Tests;

public interface IMyService
{
    string GetValue();
}

[Inject]
public class MyService : IMyService
{
    public string GetValue() => "Hello from FactoryGenerator";
}

public interface IOtherService
{
    string GetValue();
}

public class OtherService : IOtherService
{
    public string GetValue() => "Hello from IServiceProvider";
}

public interface IRequestScopedDependency
{
    Guid Id { get; }
}

public class RequestScopedDependency : IRequestScopedDependency
{
    public Guid Id { get; } = Guid.NewGuid();
}

public interface IRequestScopedFactoryService
{
    Guid GetRequestId();
}

[Inject]
public class RequestScopedFactoryService(IRequestScopedDependency dependency) : IRequestScopedFactoryService
{
    public Guid GetRequestId() => dependency.Id;
}

public interface IAsyncRequestScopedService
{
}

[Inject, Scoped]
public class AsyncRequestScopedService : IAsyncRequestScopedService, IAsyncDisposable
{
    public static int DisposeAsyncCount { get; private set; }

    public static void Reset()
    {
        DisposeAsyncCount = 0;
    }

    public ValueTask DisposeAsync()
    {
        DisposeAsyncCount++;
        return default;
    }
}

public interface IFrameworkOnlyCollectionItem
{
}

public sealed class FrameworkOnlyCollectionItem : IFrameworkOnlyCollectionItem
{
}

[Inject, Self]
public class FrameworkOnlyCollectionConsumer(IEnumerable<IFrameworkOnlyCollectionItem> items)
{
    public IEnumerable<IFrameworkOnlyCollectionItem> Items { get; } = items;
}

public class IntegrationTests
{
    [Test]
    public async Task Middleware_Integrates_FactoryGenerator_With_RequestServices()
    {
        // Setup
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton<IOtherService, OtherService>();
                        services.AddScoped<IRequestScopedDependency, RequestScopedDependency>();
                    })
                    .Configure(app =>
                    {
                        // Wrap IServiceProvider to be an IContainer
                        var adapter = app.ApplicationServices.ToContainer();
                        
                        // In a real app, this would be MyApp.Generated.DependencyInjectionContainer
                        // Here, it should be generated for this assembly.
                        // Since we have the analyzer project reference, it should be generated.
                        var container = new Generated.DependencyInjectionContainer(adapter);
                        
                        app.UseFactoryGenerator(container);

                        app.Run(async context =>
                        {
                            var myService = context.RequestServices.GetRequiredService<IMyService>();
                            var otherService = context.RequestServices.GetRequiredService<IOtherService>();
                            var requestScopedFactoryService = context.RequestServices.GetRequiredService<IRequestScopedFactoryService>();
                            var requestScopedDependency = context.RequestServices.GetRequiredService<IRequestScopedDependency>();

                            await context.Response.WriteAsync($"{myService.GetValue()} | {otherService.GetValue()} | {requestScopedFactoryService.GetRequestId()} | {requestScopedDependency.Id}");
                        });
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();

        // Act
        var response = await client.GetAsync("/");

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        var parts = content.Split(" | ");
        parts.Length.ShouldBe(4);
        parts[0].ShouldBe("Hello from FactoryGenerator");
        parts[1].ShouldBe("Hello from IServiceProvider");
        parts[2].ShouldBe(parts[3]);
    }

    [Test]
    public async Task Middleware_Uses_Current_RequestScope_For_FrameworkScopedDependencies()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddScoped<IRequestScopedDependency, RequestScopedDependency>();
                    })
                    .Configure(app =>
                    {
                        var adapter = app.ApplicationServices.ToContainer();
                        var container = new Generated.DependencyInjectionContainer(adapter);

                        app.UseFactoryGenerator(container);

                        app.Run(async context =>
                        {
                            var requestScopedFactoryService = context.RequestServices.GetRequiredService<IRequestScopedFactoryService>();
                            var requestScopedDependency = context.RequestServices.GetRequiredService<IRequestScopedDependency>();

                            await context.Response.WriteAsync($"{requestScopedFactoryService.GetRequestId()}|{requestScopedDependency.Id}");
                        });
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();

        var firstResponse = await client.GetStringAsync("/");
        var secondResponse = await client.GetStringAsync("/");

        var firstParts = firstResponse.Split('|');
        var secondParts = secondResponse.Split('|');

        firstParts.Length.ShouldBe(2);
        secondParts.Length.ShouldBe(2);
        firstParts[0].ShouldBe(firstParts[1]);
        secondParts[0].ShouldBe(secondParts[1]);
        firstParts[0].ShouldNotBe(secondParts[0]);
    }

    [Test]
    public async Task Middleware_Disposes_AsyncOnly_FactoryScopedServices()
    {
        AsyncRequestScopedService.Reset();

        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddScoped<IRequestScopedDependency, RequestScopedDependency>();
                    })
                    .Configure(app =>
                    {
                        var adapter = app.ApplicationServices.ToContainer();
                        var container = new Generated.DependencyInjectionContainer(adapter);

                        app.UseFactoryGenerator(container);

                        app.Run(context =>
                        {
                            _ = context.RequestServices.GetRequiredService<IAsyncRequestScopedService>();
                            return context.Response.WriteAsync("ok");
                        });
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetAsync("/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        AsyncRequestScopedService.DisposeAsyncCount.ShouldBe(1);
    }

    [Test]
    public async Task Middleware_Uses_Framework_Collections_When_No_Local_Implementations_Exist()
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddScoped<IRequestScopedDependency, RequestScopedDependency>();
                        services.AddSingleton<IFrameworkOnlyCollectionItem, FrameworkOnlyCollectionItem>();
                        services.AddSingleton<IFrameworkOnlyCollectionItem, FrameworkOnlyCollectionItem>();
                    })
                    .Configure(app =>
                    {
                        var adapter = app.ApplicationServices.ToContainer();
                        var container = new Generated.DependencyInjectionContainer(adapter);

                        app.UseFactoryGenerator(container);

                        app.Run(async context =>
                        {
                            var consumer = context.RequestServices.GetRequiredService<FrameworkOnlyCollectionConsumer>();
                            await context.Response.WriteAsync(consumer.Items.Count().ToString());
                        });
                    });
            })
            .StartAsync();

        var client = host.GetTestClient();
        var response = await client.GetStringAsync("/");

        response.ShouldBe("2");
    }
}
