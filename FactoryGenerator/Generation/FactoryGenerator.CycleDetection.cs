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
    /// Detects cyclic dependencies between injections ahead of code generation, so a misconfigured
    /// dependency graph fails fast with a readable diagnostic instead of producing code that
    /// would recurse infinitely (or fail to compile) at runtime.
    /// </summary>
    public partial class FactoryGenerator
    {
        private static List<InjectionData> GetReachableImplementations(List<InjectionData> possibilities)
        {
            if (possibilities.Count == 0)
                return new List<InjectionData>();

            if (possibilities.All(i => i.BooleanInjection == null))
                return new List<InjectionData> { possibilities.Last() };

            var reachable = new List<InjectionData>();
            var fallback = possibilities.LastOrDefault(p => p.BooleanInjection == null);
            var keys = possibilities.Select(p => p.BooleanInjection?.Key).OfType<string>().Distinct();

            foreach (var key in keys)
            {
                var selected = possibilities.LastOrDefault(p => p.BooleanInjection?.Value == true && p.BooleanInjection?.Key == key) ?? fallback;
                if (selected is not null && !reachable.Contains(selected))
                    reachable.Add(selected);
            }

            if (fallback is not null && !reachable.Contains(fallback))
                reachable.Add(fallback);

            return reachable;
        }

        private static IEnumerable<string> GetCycleDependencies(InjectionData injection, InjectionResolution resolution, HashSet<string> availableInterfaceFullNames)
        {
            if (injection.Lambda is LambdaData lambda)
            {
                if (availableInterfaceFullNames.Contains(lambda.ContainingTypeFullName))
                    yield return lambda.ContainingTypeFullName;

                if (!lambda.IsMethod)
                    yield break;

                foreach (var dependency in GetParameterDependencies(lambda.MethodParameters, availableInterfaceFullNames))
                    yield return dependency;

                yield break;
            }

            if (resolution.ConstructorParameters is not { } parameters)
                yield break;

            foreach (var dependency in GetParameterDependencies(parameters, availableInterfaceFullNames))
                yield return dependency;
        }

        private static IEnumerable<string> GetParameterDependencies(
            ImmutableArray<ParameterData> parameters,
            HashSet<string> availableInterfaceFullNames)
        {
            foreach (var parameter in parameters)
            {
                if (parameter.IsCollection)
                    continue;

                var typeLookup = parameter.IsNullable
                    ? parameter.TypeFullName.TrimEnd('?')
                    : parameter.TypeFullName;
                if (!availableInterfaceFullNames.Contains(typeLookup))
                    continue;

                yield return typeLookup;
            }
        }


        private static void CheckForCycles(
            Dictionary<string, List<InjectionData>> interfaceInjectors,
            HashSet<string> availableInterfaceFullNames,
            IReadOnlyDictionary<InjectionData, InjectionResolution> resolutions)
        {
            var graph = new Dictionary<string, HashSet<string>>();
            var nodeOwner = new Dictionary<string, List<InjectionData>>();

            foreach (var interfaceInjector in interfaceInjectors)
            {
                var ifaceName = interfaceInjector.Key;
                var possibilities = interfaceInjector.Value;
                var reachable = GetReachableImplementations(possibilities);
                var deps = new HashSet<string>();

                foreach (var injection in reachable)
                {
                    foreach (var dep in GetCycleDependencies(injection, resolutions[injection], availableInterfaceFullNames))
                        deps.Add(dep);
                }

                graph[ifaceName] = deps;
                nodeOwner[ifaceName] = reachable;
            }

            var state = new Dictionary<string, int>();
            var path = new List<string>();

            foreach (var node in graph.Keys)
            {
                if (!state.TryGetValue(node, out var s) || s == 0)
                    DfsCycleCheck(node, graph, state, path, nodeOwner);
            }
        }

        private static void DfsCycleCheck(string node, Dictionary<string, HashSet<string>> graph,
                                          Dictionary<string, int> state, List<string> path,
                                          Dictionary<string, List<InjectionData>> nodeOwner)
        {
            state[node] = 1; // in-progress
            path.Add(node);

            if (graph.TryGetValue(node, out var deps))
            {
                foreach (var dep in deps)
                {
                    state.TryGetValue(dep, out var depState);
                    if (depState == 1)
                    {
                        // Back-edge found — extract the cycle. The owner description is only ever
                        // needed here, on the rare path where a cycle actually exists, so it's built
                        // lazily instead of unconditionally for every interface on every run.
                        var cycleStart = path.IndexOf(dep);
                        var cycle = path.GetRange(cycleStart, path.Count - cycleStart);
                        cycle.Add(dep);
                        var owner = nodeOwner.TryGetValue(node, out var reachable)
                            ? string.Join(", ", reachable.Select(injection => injection.TypeFullName).Distinct())
                            : node;
                        throw new InvalidOperationException(
                            $"Cyclic Dependency Detected: {string.Join(" \u2192 ", cycle)} (via {owner})");
                    }

                    if (depState == 0 && graph.ContainsKey(dep))
                        DfsCycleCheck(dep, graph, state, path, nodeOwner);
                }
            }

            path.RemoveAt(path.Count - 1);
            state[node] = 2; // done
        }
    }
}
