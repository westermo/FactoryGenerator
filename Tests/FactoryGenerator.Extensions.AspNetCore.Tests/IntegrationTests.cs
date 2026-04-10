using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using FactoryGenerator;
using FactoryGenerator.Attributes;
using FactoryGenerator.Extensions.AspNetCore;

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

                            await context.Response.WriteAsync($"{myService.GetValue()} | {otherService.GetValue()}");
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
        content.ShouldBe("Hello from FactoryGenerator | Hello from IServiceProvider");
    }
}
