using System;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryGenerator.Extensions.AspNetCore;

#nullable enable
internal sealed class FactoryGeneratorServiceProvider : IServiceProvider, ISupportRequiredService, IDisposable
{
    private readonly IServiceProvider _baseProvider;
    private readonly ILifetimeScope _scope;

    public FactoryGeneratorServiceProvider(IServiceProvider baseProvider, ILifetimeScope scope)
    {
        _baseProvider = baseProvider;
        _scope = scope;
    }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider))
        {
            return this;
        }

        if (serviceType == typeof(ILifetimeScope))
        {
            return _scope;
        }

        if (_scope.TryResolve(serviceType, out var resolved))
        {
            return resolved;
        }

        return _baseProvider.GetService(serviceType);
    }

    public object GetRequiredService(Type serviceType)
    {
        var service = GetService(serviceType);
        if (service != null)
        {
            return service;
        }

        throw new InvalidOperationException($"No service for type '{serviceType}' has been registered.");
    }

    public void Dispose()
    {
        _scope.Dispose();
    }
}
