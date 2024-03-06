using System;
using System.Diagnostics.CodeAnalysis;

namespace FactoryGenerator;

public interface IContainer
{
    T Resolve<T>();
    object Resolve(Type type);

    bool TryResolve(Type type, [NotNullWhen(true)] out object resolved)
    {
        try
        {
            resolved = Resolve(type);
            return true;
        }
        catch (Exception)
        {
            resolved = null;
            return false;
        }
    }


    bool TryResolve<T>([NotNullWhen(true)] out T resolved)
    {
        try
        {
            resolved = Resolve<T>();
            return true;
        }
        catch (Exception)
        {
            resolved = default;
            return false;
        }
    }

    bool IsRegistered(Type type) => TryResolve(type, out _);
}