using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryGenerator.Extensions.AspNetCore;

#nullable enable
internal sealed class ServiceProviderAdapter : IContainer, IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope? _serviceScope;

    public ServiceProviderAdapter(IServiceProvider serviceProvider, IServiceScope? serviceScope = null)
    {
        _serviceProvider = serviceProvider;
        _serviceScope = serviceScope;
    }

    public IContainer? Base => null;
    public IContainer? Inheritor { get; set; }

    public void Dispose()
    {
        _serviceScope?.Dispose();
    }

    public T Resolve<T>()
    {
        var service = _serviceProvider.GetService<T>();
        if (service != null) return service;
        throw new KeyNotFoundException($"The type {typeof(T)} has not been registered in the IServiceProvider.");
    }

    public object Resolve(Type type)
    {
        var service = _serviceProvider.GetService(type);
        if (service != null) return service;
        throw new KeyNotFoundException($"The type {type} has not been registered in the IServiceProvider.");
    }

    public bool TryResolve(Type type, out object? resolved)
    {
        resolved = _serviceProvider.GetService(type);
        return resolved != null;
    }

    public bool TryResolve<T>(out T? resolved)
    {
        resolved = _serviceProvider.GetService<T>();
        return resolved != null;
    }

    public bool IsRegistered(Type type)
    {
        // IServiceProvider doesn't have a reliable IsRegistered method without resolution.
        // We return true if it can be resolved.
        return _serviceProvider.GetService(type) != null;
    }

    public bool IsRegistered<T>() => IsRegistered(typeof(T));

    public bool GetBoolean(string key) => false;

    public IEnumerable<(string Key, bool Value)> GetBooleans()
    {
        yield break;
    }

    public ILifetimeScope BeginLifetimeScope()
    {
        var scope = _serviceProvider.CreateScope();
        return new ServiceProviderAdapter(scope.ServiceProvider, scope);
    }
}
