using Microsoft.AspNetCore.Builder;

namespace FactoryGenerator.Extensions.AspNetCore;

public static class AppBuilderExtensions
{
    public static IApplicationBuilder UseFactoryGenerator(this IApplicationBuilder app, IContainer container)
    {
        return app.UseMiddleware<FactoryGeneratorMiddleware>(container);
    }
}
