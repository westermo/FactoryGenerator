using System;
using System.Collections.Generic;
using System.Linq;

namespace FactoryGenerator;

/// <summary>
/// A global registry for source-generated container factories.
/// Plugin assemblies register their container factories via [ModuleInitializer] on load.
/// The host can then build a chained container pipeline from a base container.
/// </summary>
public static class ContainerRegistry
{
    private static readonly List<ContainerRegistration> s_registrations = new List<ContainerRegistration>();
    private static readonly object s_lock = new object();

    /// <summary>
    /// Registers a container factory. Called automatically by generated [ModuleInitializer] methods.
    /// </summary>
    /// <param name="assemblyName">The assembly name that owns this container.</param>
    /// <param name="factory">A factory that takes an optional base IContainer and returns a new IContainer wrapping it.</param>
    /// <param name="priority">Optional priority for ordering. Lower values are closer to the base. Default is 0.</param>
    public static void Register(string assemblyName, Func<IContainer, IContainer> factory, int priority = 0)
    {
        lock (s_lock)
        {
            s_registrations.Add(new ContainerRegistration(assemblyName, factory, priority));
        }
    }

    /// <summary>
    /// Builds a chained container by applying all registered plugin factories on top of the base container.
    /// Ordering is determined by priority (ascending), then registration order.
    /// </summary>
    /// <param name="baseContainer">The root container to build upon.</param>
    /// <returns>The outermost container in the chain with all plugins applied.</returns>
    public static IContainer BuildChain(IContainer baseContainer)
    {
        List<ContainerRegistration> snapshot;
        lock (s_lock)
        {
            snapshot = s_registrations.OrderBy(r => r.Priority).ToList();
        }

        var existingAssemblies = GetAssemblyNames(baseContainer);
        var current = baseContainer;
        foreach (var registration in snapshot)
        {
            if (!existingAssemblies.Add(registration.AssemblyName))
                continue;
            current = registration.Factory(current);
        }

        return current;
    }

    /// <summary>
    /// Builds a chained container using only the specified assemblies, in the given order.
    /// </summary>
    /// <param name="baseContainer">The root container to build upon.</param>
    /// <param name="assemblyNames">Ordered list of assembly names to chain.</param>
    /// <returns>The outermost container in the chain.</returns>
    public static IContainer BuildChain(IContainer baseContainer, IEnumerable<string> assemblyNames)
    {
        List<ContainerRegistration> snapshot;
        lock (s_lock)
        {
            snapshot = new List<ContainerRegistration>(s_registrations);
        }

        var existingAssemblies = GetAssemblyNames(baseContainer);
        var current = baseContainer;
        foreach (var name in assemblyNames)
        {
            if (!existingAssemblies.Add(name))
                continue;

            var registration = snapshot.Find(r => r.AssemblyName == name);
            if (registration == null)
            {
                throw new InvalidOperationException(
                    $"No container factory registered for assembly '{name}'. " +
                    "Ensure the assembly is loaded before calling BuildChain.");
            }

            current = registration.Factory(current);
        }

        return current;
    }

    private static HashSet<string> GetAssemblyNames(IContainer container)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var current = container; current is not null; current = current.Base)
        {
            if (current is IContainerRegistrationMetadata metadata)
                names.Add(metadata.AssemblyName);
        }

        for (var current = container.Inheritor; current is not null; current = current.Inheritor)
        {
            if (current is IContainerRegistrationMetadata metadata)
                names.Add(metadata.AssemblyName);
        }

        return names;
    }

    /// <summary>
    /// Returns the names of all currently registered container assemblies.
    /// </summary>
    public static IReadOnlyList<string> RegisteredAssemblies
    {
        get
        {
            lock (s_lock)
            {
                return s_registrations.Select(r => r.AssemblyName).ToList();
            }
        }
    }

    /// <summary>
    /// Removes all registrations. Primarily for testing purposes.
    /// </summary>
    public static void Clear()
    {
        lock (s_lock)
        {
            s_registrations.Clear();
        }
    }
}

/// <summary>
/// Represents a registered container factory from a specific assembly.
/// </summary>
public sealed class ContainerRegistration
{
    public string AssemblyName { get; }
    public Func<IContainer, IContainer> Factory { get; }
    public int Priority { get; }

    public ContainerRegistration(string assemblyName, Func<IContainer, IContainer> factory, int priority)
    {
        AssemblyName = assemblyName;
        Factory = factory;
        Priority = priority;
    }
}
