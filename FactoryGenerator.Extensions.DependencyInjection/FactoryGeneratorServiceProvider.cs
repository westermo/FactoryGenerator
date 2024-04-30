// Copyright (c) Autofac Project. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FactoryGenerator.Extensions.DependencyInjection;

public class FactoryGeneratorServiceScope(FactoryGeneratorServiceProvider provider) : IServiceScope
{
    public void Dispose()
    {
        provider.Dispose();
    }

    public IServiceProvider ServiceProvider { get; } = provider;
}

/// <summary>
/// FactoryGenerator implementation of the ASP.NET Core <see cref="IServiceProvider"/>.
/// </summary>
/// <seealso cref="IServiceProvider" />
/// <seealso cref="ISupportRequiredService" />
public class FactoryGeneratorServiceProvider(ILifetimeScope container)
    : IServiceProvider, ISupportRequiredService, IServiceProviderIsService, IDisposable, IServiceScopeFactory, IControllerActivator, IServiceProviderFactory<object>, IServiceScope
{
    private bool m_disposed;
    private DefaultServiceProviderFactory m_fallback = new DefaultServiceProviderFactory();

    public object GetRequiredService(Type serviceType)
    {
        return container.Resolve(serviceType);
    }

    /// <inheritdoc />
    public bool IsService(Type serviceType) => container.IsRegistered(serviceType);

    public object? GetService(Type serviceType)
    {
        return container.TryResolve(serviceType, out var resolved) ? resolved : null;
    }

    public ILifetimeScope Container => container;

    protected void Dispose(bool disposing)
    {
        if (!m_disposed)
        {
            m_disposed = true;
            if (disposing)
            {
                container.Dispose();
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public static IServiceCollection CreateProvider(IServiceCollection services, ILifetimeScope container, Action<IServiceProviderFactory<object>> action)
    {
        var provider = new FactoryGeneratorServiceProvider(container);
        // action(provider);
        return services
               .Replace(ServiceDescriptor.Singleton<IServiceProvider>(provider))
               .Replace(ServiceDescriptor.Singleton<IServiceScopeFactory>(provider))
               .Replace(ServiceDescriptor.Singleton<IServiceScope>(provider))
               .Replace(ServiceDescriptor.Singleton<IServiceProviderFactory<object>>(provider))
               .Replace(ServiceDescriptor.Singleton<IControllerActivator>(provider));
    }

    public IServiceScope CreateScope()
    {
        return new FactoryGeneratorServiceScope(new FactoryGeneratorServiceProvider(Container.BeginLifetimeScope()));
    }

    public object Create(ControllerContext context)
    {
        context.ActionDescriptor.
        return container.Resolve(context.ActionDescriptor.ControllerTypeInfo.AsType());
    }

    public void Release(ControllerContext context, object controller)
    {
    }

    public object CreateBuilder(IServiceCollection services)
    {
        return Container;
    }

    public IServiceProvider CreateServiceProvider(object containerBuilder)
    {
        return this;
    }

    public IServiceProvider ServiceProvider => this;
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection WithFactoryGenerator(this IServiceCollection serviceCollection, ILifetimeScope container, Action<IServiceProviderFactory<object>> action)
    {
        FactoryGeneratorServiceProvider.CreateProvider(serviceCollection, container, action);
        return serviceCollection;
    }
}