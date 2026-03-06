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

public interface IWelcomeService
{
    string GetWelcomeMessage();
}

[Inject]
public class WelcomeService : IWelcomeService
{
    private readonly IDateService _dateService;

    public WelcomeService(IDateService dateService)
    {
        _dateService = dateService;
    }

    public string GetWelcomeMessage() => $"Welcome! Today is {_dateService.GetDateString()}. Resolved via FactoryGenerator!";
}
