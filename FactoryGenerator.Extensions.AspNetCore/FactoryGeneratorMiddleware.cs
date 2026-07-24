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
        var originalProvider = context.RequestServices;
        var requestContainer = new ServiceProviderAdapter(originalProvider, baseContainer: _container);
        var scope = _container is IContainerScopeFactory scopeFactory
            ? scopeFactory.BeginLifetimeScope(requestContainer)
            : _container.BeginLifetimeScope();
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
