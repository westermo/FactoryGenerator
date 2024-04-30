using FactoryGenerator.Extensions.DependencyInjection;
using TestWebApp.Generated;

var builder = WebApplication.CreateSlimBuilder(args);
// builder.Services.AddControllers();
var container = new DependencyInjectionContainer();
builder.Services.AddMvcCore().AddControllersAsServices();
builder.Host.UseDefaultServiceProvider(p => p.ValidateOnBuild = false);
builder.Services.WithFactoryGenerator(container, factory => builder.Host.UseServiceProviderFactory(factory));

var app = builder.Build();
app.MapControllers();
app.Run();