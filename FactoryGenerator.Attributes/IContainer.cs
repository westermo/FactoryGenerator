using System;
using System.Collections;
using System.Collections.Generic;

namespace FactoryGenerator;
#nullable enable
public interface ILifetimeScope : IDisposable
{
    T Resolve<T>();
    object Resolve(Type type);

    bool TryResolve(Type type, out object? resolved);


    bool TryResolve<T>(out T? resolved);

    bool IsRegistered(Type type);
    bool IsRegistered<T>();
    bool GetBoolean(string key);
    IEnumerable<(string Key, bool Value)> GetBooleans();
    ILifetimeScope BeginLifetimeScope();
}

public interface IContainer : ILifetimeScope
{
    IContainer? Base { get; }
    IContainer? Inheritor { get; set; }
}