using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FactoryGenerator
{
    /// <summary>
    /// Orders discovered injections (by assembly priority/distance) and indexes them by the
    /// interfaces they can satisfy, forming the basis for interface-to-implementation selection.
    /// </summary>
    public partial class FactoryGenerator
    {
        /// <summary>
        /// The result of analyzing the full set of discovered injections once: assembly-priority
        /// ordering plus the interface-to-implementation index derived from it. Both the
        /// dictionary-based container generator and the static-extensions generator need an
        /// identical copy of this; computing it once here — as a single incremental-pipeline stage
        /// (see <see cref="Initialize"/>) — means it is derived exactly once per compilation instead
        /// of once per consumer.
        /// </summary>
        private sealed class InjectionAnalysis : IEquatable<InjectionAnalysis>
        {
            public InjectionAnalysis(
                ImmutableArray<InjectionData> rawInjections,
                ImmutableArray<InjectionData> ordered,
                Dictionary<string, List<InjectionData>> interfaceInjectors,
                Dictionary<string, string> interfaceMemberNames,
                HashSet<string> availableInterfaceFullNames)
            {
                RawInjections = rawInjections;
                Ordered = ordered;
                InterfaceInjectors = interfaceInjectors;
                InterfaceMemberNames = interfaceMemberNames;
                AvailableInterfaceFullNames = availableInterfaceFullNames;
            }

            /// <summary>
            /// Injections in original discovery order, exactly as produced by <c>FindMethods</c>.
            /// The dictionary-based container generator derives its (positional) boolean-parameter
            /// order from the first-seen order in this sequence — <see cref="Ordered"/> must not be
            /// substituted for it, since re-sorting would silently reorder generated constructor
            /// parameters.
            /// </summary>
            public ImmutableArray<InjectionData> RawInjections { get; }

            /// <summary>Injections in final assembly-priority/distance order.</summary>
            public ImmutableArray<InjectionData> Ordered { get; }

            /// <summary>Interface full name → the injections that can satisfy it, in priority order.</summary>
            public Dictionary<string, List<InjectionData>> InterfaceInjectors { get; }

            /// <summary>Interface full name → its generated member name (parallel to <see cref="InterfaceInjectors"/>).</summary>
            public Dictionary<string, string> InterfaceMemberNames { get; }

            /// <summary>Every interface full name any injection can satisfy — <see cref="InterfaceInjectors"/>'s keys as a set.</summary>
            public HashSet<string> AvailableInterfaceFullNames { get; }

            // Equality (and hashing) is defined purely in terms of RawInjections: everything else
            // (Ordered, InterfaceInjectors, InterfaceMemberNames, AvailableInterfaceFullNames) is a
            // deterministic pure function of RawInjections plus the Compilation that BuildInjectionAnalysis
            // was invoked with (already tracked separately by the incremental pipeline). Comparing the
            // raw, order-sensitive sequence — rather than the re-sorted Ordered sequence — is required
            // for correctness: two different discovery orders can sort into an identical Ordered
            // sequence while still needing different generated boolean-parameter ordering.
            public bool Equals(InjectionAnalysis? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return RawInjections.SequenceEqual(other.RawInjections);
            }

            public override bool Equals(object? obj) => obj is InjectionAnalysis other && Equals(other);

            public override int GetHashCode()
            {
                var hash = RawInjections.Length;
                foreach (var injection in RawInjections)
                    hash = (hash * 397) ^ injection.GetHashCode();
                return hash;
            }
        }

        /// <summary>
        /// Builds the shared <see cref="InjectionAnalysis"/> for a compilation's discovered
        /// injections. This is the single incremental-pipeline stage both <see cref="GenerateCode"/>
        /// and <see cref="GenerateStaticExtensions"/> consume (see <see cref="Initialize"/>) instead
        /// of each independently calling <see cref="OrderInjections"/>/<see cref="BuildInterfaceInjectors"/>.
        /// </summary>
        private static InjectionAnalysis BuildInjectionAnalysis(ImmutableArray<InjectionData> dataInjections, Compilation compilation, CancellationToken token)
        {
            var ordered = OrderInjections(dataInjections, compilation).ToImmutableArray();
            token.ThrowIfCancellationRequested();
            var (interfaceInjectors, interfaceMemberNames) = BuildInterfaceInjectors(ordered);
            var availableInterfaceFullNames = new HashSet<string>(interfaceInjectors.Keys);
            return new InjectionAnalysis(dataInjections, ordered, interfaceInjectors, interfaceMemberNames, availableInterfaceFullNames);
        }

        private static List<InjectionData> OrderInjections(ImmutableArray<InjectionData> dataInjections, Compilation compilation)
        {
            var ordered = dataInjections.Reverse().ToList();
            var assemblyDistances = BuildAssemblyDistances(compilation, ordered.Select(injection => injection.AssemblyName));

            return ordered
                .OrderBy(injection => injection.AssemblyPriority)
                .ThenByDescending(injection => GetAssemblyDistance(assemblyDistances, injection.AssemblyName))
                .ThenBy(injection => injection.AssemblyName, StringComparer.Ordinal)
                .ToList();
        }

        private static Dictionary<string, int> BuildAssemblyDistances(Compilation compilation, IEnumerable<string> assemblyNames)
        {
            var relevantAssemblyNames = new HashSet<string>(assemblyNames, StringComparer.Ordinal);
            var distances = new Dictionary<string, int>(StringComparer.Ordinal);
            var visited = new HashSet<IAssemblySymbol>(SymbolEqualityComparer.Default);
            var queue = new Queue<(IAssemblySymbol Assembly, int Distance)>();

            visited.Add(compilation.Assembly);
            queue.Enqueue((compilation.Assembly, 0));

            while (queue.Count > 0 && distances.Count < relevantAssemblyNames.Count)
            {
                var current = queue.Dequeue();
                if (relevantAssemblyNames.Contains(current.Assembly.Name)
                    && (!distances.TryGetValue(current.Assembly.Name, out var existingDistance)
                        || current.Distance < existingDistance))
                {
                    distances[current.Assembly.Name] = current.Distance;
                }

                foreach (var referencedAssembly in GetReferencedAssemblies(current.Assembly))
                {
                    if (!visited.Add(referencedAssembly))
                        continue;

                    queue.Enqueue((referencedAssembly, current.Distance + 1));
                }
            }

            return distances;
        }

        private static IEnumerable<IAssemblySymbol> GetReferencedAssemblies(IAssemblySymbol assembly)
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var referencedAssembly in module.ReferencedAssemblySymbols)
                    yield return referencedAssembly;
            }
        }

        private static int GetAssemblyDistance(IReadOnlyDictionary<string, int> assemblyDistances, string assemblyName)
        {
            return assemblyDistances.TryGetValue(assemblyName, out var distance)
                ? distance
                : int.MaxValue;
        }

        private static (Dictionary<string, List<InjectionData>> InterfaceInjectors, Dictionary<string, string> InterfaceMemberNames) BuildInterfaceInjectors(IEnumerable<InjectionData> ordered)
        {
            var interfaceInjectors = new Dictionary<string, List<InjectionData>>();
            var interfaceMemberNames = new Dictionary<string, string>();

            foreach (var injection in ordered)
            {
                for (var i = 0; i < injection.InterfaceFullNames.Length; i++)
                {
                    var ifaceFull = injection.InterfaceFullNames[i];
                    var ifaceMember = injection.InterfaceMemberNames[i];
                    if (!interfaceInjectors.TryGetValue(ifaceFull, out var injectors))
                    {
                        injectors = new List<InjectionData>();
                        interfaceInjectors[ifaceFull] = injectors;
                        interfaceMemberNames[ifaceFull] = ifaceMember;
                    }

                    injectors.Add(injection);
                }
            }

            return (interfaceInjectors, interfaceMemberNames);
        }
    }
}
