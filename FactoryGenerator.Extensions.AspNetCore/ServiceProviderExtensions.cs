using System;
using FactoryGenerator;

namespace FactoryGenerator.Extensions.AspNetCore;

public static class ServiceProviderExtensions
{
    public static IContainer ToContainer(this IServiceProvider serviceProvider)
    {
        return new ServiceProviderAdapter(serviceProvider);
    }
}
