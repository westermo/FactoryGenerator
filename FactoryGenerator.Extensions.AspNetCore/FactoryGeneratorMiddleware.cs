using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace FactoryGenerator.Extensions.AspNetCore;

internal sealed class FactoryGeneratorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IContainer _container;

    public FactoryGeneratorMiddleware(RequestDelegate next, IContainer container)
    {
        _next = next;
        _container = container;
    }

    public async Task Invoke(HttpContext context)
    {
        var scope = _container.BeginLifetimeScope();
        var originalProvider = context.RequestServices;
        context.RequestServices = new FactoryGeneratorServiceProvider(originalProvider, scope);

        try
        {
            await _next(context);
        }
        finally
        {
            // The FactoryGeneratorServiceProvider.Dispose will dispose the scope
            if (context.RequestServices is FactoryGeneratorServiceProvider wrapper)
            {
                wrapper.Dispose();
            }
            context.RequestServices = originalProvider;
        }
    }
}
