using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace FactoryGenerator.Extensions.AspNetCore;

#nullable enable
internal sealed class ServiceProviderAdapter : IContainer, IDisposable, IAsyncDisposable, IContainerLocalCollectionResolver, IServiceProviderBackedContainer
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IServiceScope? _serviceScope;
    private readonly IContainer? _baseContainer;

    public ServiceProviderAdapter(IServiceProvider serviceProvider, IServiceScope? serviceScope = null, IContainer? baseContainer = null)
    {
        _serviceProvider = serviceProvider;
        _serviceScope = serviceScope;
        _baseContainer = baseContainer;
    }

    public IContainer? Base => _baseContainer;
    public IContainer? Inheritor { get; set; }

    public void Dispose()
    {
        _serviceScope?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        if (_serviceScope is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        _serviceScope?.Dispose();
        return default;
    }

    public T Resolve<T>()
    {
        var service = _serviceProvider.GetService<T>();
        if (service != null) return service;
        if (_baseContainer is not null) return _baseContainer.Resolve<T>();
        throw new KeyNotFoundException($"The type {typeof(T)} has not been registered in the IServiceProvider.");
    }

    public object Resolve(Type type)
    {
        var service = _serviceProvider.GetService(type);
        if (service != null) return service;
        if (_baseContainer is not null) return _baseContainer.Resolve(type);
        throw new KeyNotFoundException($"The type {type} has not been registered in the IServiceProvider.");
    }

    public bool TryResolve(Type type, out object? resolved)
    {
        resolved = _serviceProvider.GetService(type);
        if (resolved is not null) return true;
        if (_baseContainer is not null) return _baseContainer.TryResolve(type, out resolved);
        return false;
    }

    public bool TryResolve<T>(out T? resolved)
    {
        resolved = _serviceProvider.GetService<T>();
        if (resolved is not null) return true;
        if (_baseContainer is not null) return _baseContainer.TryResolve(out resolved);
        return false;
    }

    public bool TryResolveLocalCollection(Type type, out object? resolved)
    {
        resolved = _serviceProvider.GetService(type);
        return resolved is not null;
    }

    public bool IsRegistered(Type type)
    {
        // IServiceProvider doesn't have a reliable IsRegistered method without resolution.
        // We return true if it can be resolved.
        return _serviceProvider.GetService(type) != null || _baseContainer?.IsRegistered(type) == true;
    }

    public bool IsRegistered<T>() => IsRegistered(typeof(T));

    public bool GetBoolean(string key) => _baseContainer?.GetBoolean(key) == true;

    public IEnumerable<(string Key, bool Value)> GetBooleans()
    {
        if (_baseContainer is null)
            yield break;

        foreach (var boolean in _baseContainer.GetBooleans())
            yield return boolean;
    }

    public ILifetimeScope BeginLifetimeScope()
    {
        var scope = _serviceProvider.CreateAsyncScope();
        return new ServiceProviderAdapter(scope.ServiceProvider, scope, _baseContainer);
    }
}
