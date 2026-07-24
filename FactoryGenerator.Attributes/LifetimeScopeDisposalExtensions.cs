using System;
using System.Threading.Tasks;

namespace FactoryGenerator;

public static class LifetimeScopeDisposalExtensions
{
    public static ValueTask DisposeAsync(this ILifetimeScope scope)
    {
        if (scope is null)
            throw new ArgumentNullException(nameof(scope));

        if (scope is IAsyncDisposable asyncDisposable)
            return asyncDisposable.DisposeAsync();

        scope.Dispose();
        return default;
    }
}
