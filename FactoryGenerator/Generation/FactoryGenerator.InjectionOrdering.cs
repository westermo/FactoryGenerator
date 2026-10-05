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

            /// <summary>The name of the assembly being compiled (used as the generated namespace root).</summary>
            public string AssemblyName { get; private set; } = string.Empty;

            private InjectionScanResult? m_source;

            // Equality (and hashing) is defined purely in terms of the source InjectionScanResult:
            // everything else (Ordered, InterfaceInjectors, InterfaceMemberNames,
            // AvailableInterfaceFullNames) is a deterministic pure function of it. Comparing the raw,
            // order-sensitive sequence — rather than the re-sorted Ordered sequence — is required
            // for correctness: two different discovery orders can sort into an identical Ordered
            // sequence while still needing different generated boolean-parameter ordering.
            public bool Equals(InjectionAnalysis? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(m_source, other.m_source);
            }

            public override bool Equals(object? obj) => obj is InjectionAnalysis other && Equals(other);

            public override int GetHashCode() => m_source?.GetHashCode() ?? 0;

            internal static InjectionAnalysis From(InjectionScanResult source,
                                                   ImmutableArray<InjectionData> ordered,
                                                   Dictionary<string, List<InjectionData>> interfaceInjectors,
                                                   Dictionary<string, string> interfaceMemberNames)
            {
                return new InjectionAnalysis(source.RawInjections, ordered, interfaceInjectors, interfaceMemberNames,
                                             new HashSet<string>(interfaceInjectors.Keys))
                {
                    AssemblyName = source.AssemblyName,
                    m_source = source
                };
            }
        }

        /// <summary>
        /// The fully symbol-free result of scanning a <see cref="Compilation"/>: the discovered
        /// injections (in discovery order), the compiling assembly's name, and each injection
        /// assembly's reference-graph distance from it. This is the only pipeline stage that
        /// touches the Compilation; because it is value-equatable, Roslyn can cache every
        /// downstream stage (analysis and source output) whenever an edit doesn't change it.
        /// </summary>
        private sealed class InjectionScanResult : IEquatable<InjectionScanResult>
        {
            public InjectionScanResult(ImmutableArray<InjectionData> rawInjections,
                                       string assemblyName,
                                       ImmutableArray<KeyValuePair<string, int>> assemblyDistances)
            {
                RawInjections = rawInjections;
                AssemblyName = assemblyName;
                AssemblyDistances = assemblyDistances;
            }

            public ImmutableArray<InjectionData> RawInjections { get; }
            public string AssemblyName { get; }

            /// <summary>Assembly name → reference distance from the compiling assembly, sorted by name.</summary>
            public ImmutableArray<KeyValuePair<string, int>> AssemblyDistances { get; }

            public bool Equals(InjectionScanResult? other)
            {
                if (other is null) return false;
                if (ReferenceEquals(this, other)) return true;
                return AssemblyName == other.AssemblyName
                    && RawInjections.SequenceEqual(other.RawInjections)
                    && AssemblyDistances.SequenceEqual(other.AssemblyDistances);
            }

            public override bool Equals(object? obj) => obj is InjectionScanResult other && Equals(other);

            public override int GetHashCode()
            {
                var hash = (AssemblyName.GetHashCode() * 397) ^ RawInjections.Length;
                foreach (var injection in RawInjections)
                    hash = (hash * 397) ^ injection.GetHashCode();
                return hash;
            }
        }

        /// <summary>
        /// Scans the compilation for injections and captures everything later stages need from it
        /// as plain, equatable data (see <see cref="InjectionScanResult"/>).
        /// </summary>
        private static InjectionScanResult ScanCompilation(Compilation compilation, CancellationToken token)
        {
            var scope = GetInjectionScanScope(compilation, token);
            var rawInjections = FindMethods(scope, token).ToImmutableArray();
            token.ThrowIfCancellationRequested();
            var distances = BuildAssemblyDistances(compilation, rawInjections.Select(injection => injection.AssemblyName))
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToImmutableArray();
            return new InjectionScanResult(rawInjections, compilation.Assembly.Name, distances);
        }

        /// <summary>
        /// Builds the shared <see cref="InjectionAnalysis"/> for a compilation's discovered
        /// injections. This is the single incremental-pipeline stage both <see cref="GenerateCode"/>
        /// and <see cref="GenerateStaticExtensions"/> consume (see <see cref="Initialize"/>) instead
        /// of each independently calling <see cref="OrderInjections"/>/<see cref="BuildInterfaceInjectors"/>.
        /// </summary>
        private static InjectionAnalysis BuildInjectionAnalysis(InjectionScanResult scan, CancellationToken token)
        {
            var ordered = OrderInjections(scan).ToImmutableArray();
            token.ThrowIfCancellationRequested();
            var (interfaceInjectors, interfaceMemberNames) = BuildInterfaceInjectors(ordered);
            return InjectionAnalysis.From(scan, ordered, interfaceInjectors, interfaceMemberNames);
        }

        private static List<InjectionData> OrderInjections(InjectionScanResult scan)
        {
            var ordered = scan.RawInjections.Reverse().ToList();
            var assemblyDistances = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in scan.AssemblyDistances)
                assemblyDistances[pair.Key] = pair.Value;

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
