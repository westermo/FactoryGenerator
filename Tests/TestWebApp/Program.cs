using FactoryGenerator.Attributes;
using FactoryGenerator.Extensions.AspNetCore;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Standard ASP.NET DI
builder.Services.AddSingleton<IDateService, DateService>();

var app = builder.Build();

// Setup FactoryGenerator Container
var adapter = app.Services.ToContainer();
var container = new TestWebApp.Generated.DependencyInjectionContainer(adapter);

// Use FactoryGenerator Middleware
app.UseFactoryGenerator(container);

app.MapGet("/", ([FromServices] IWelcomeService welcomeService) => 
    Results.Text(welcomeService.GetWelcomeMessage()));

app.Run();

// --- Services ---

public interface IDateService
{
    string GetDateString();
}

public class DateService : IDateService
{
    public string GetDateString() => DateTime.UtcNow.ToShortDateString();
}

// A single open-generic factory method. FactoryGenerator specializes it at compile time for every
// closed ILogger<T> requested by an injected constructor (ILogger<WelcomeService>, ILogger<GreetingFormatter>).
// ILoggerFactory is not registered with FactoryGenerator, so it is resolved from ASP.NET's service provider.
[Inject, Self]
public sealed class LoggingRegistrations
{
    [Inject, Singleton, ExceptAs<ILogger>]
    public ILogger<TCategory> CreateLogger<TCategory>(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger<TCategory>();
}

public interface IGreetingFormatter
{
    string Format(string date);
}

[Inject]
public class GreetingFormatter(ILogger<GreetingFormatter> logger) : IGreetingFormatter
{
    public string Format(string date)
    {
        logger.LogInformation("Formatting greeting for {Date}", date);
        return $"Welcome! Today is {date}. Resolved via FactoryGenerator!";
    }
}

public interface IWelcomeService
{
    string GetWelcomeMessage();
}

[Inject]
public class WelcomeService(IDateService dateService, IGreetingFormatter formatter, ILogger<WelcomeService> logger)
    : IWelcomeService
{
    public string GetWelcomeMessage()
    {
        logger.LogInformation("Building welcome message");
        return formatter.Format(dateService.GetDateString());
    }
}
