using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

namespace FactoryGenerator;
#nullable enable
public interface IContainer : IDisposable
{
    T Resolve<T>();
    object Resolve(Type type);

    bool TryResolve(Type type, [NotNullWhen(true)] out object? resolved);


    bool TryResolve<T>([NotNullWhen(true)] out T? resolved);

    bool IsRegistered(Type type) => TryResolve(type, out _);
    bool IsRegistered<T>() => IsRegistered(typeof(T));
}