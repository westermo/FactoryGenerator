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
        private static List<InjectionData> OrderInjections(ImmutableArray<InjectionData> dataInjections, Compilation compilation, ILogger? log = null)
        {
            var ordered = dataInjections.Reverse().ToList();
            var assemblyDistances = BuildAssemblyDistances(compilation, ordered.Select(injection => injection.AssemblyName));

            foreach (var injection in ordered)
                log?.Log(LogLevel.Debug, $"Traversing {injection.Name} from {injection.AssemblyName} with priority {injection.AssemblyPriority}");

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
                    if (!interfaceInjectors.ContainsKey(ifaceFull))
                    {
                        interfaceInjectors[ifaceFull] = new List<InjectionData>();
                        interfaceMemberNames[ifaceFull] = ifaceMember;
                    }

                    interfaceInjectors[ifaceFull].Add(injection);
                }
            }

            return (interfaceInjectors, interfaceMemberNames);
        }
    }
}
