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
    /// Helpers for producing valid, collision-free C# identifiers for generated boolean
    /// parameters, externally-supplied constructor parameters, and boolean-gated selection
    /// expressions shared between the container and static-extension code generators.
    /// </summary>
    public partial class FactoryGenerator
    {
        private static void ValidateExternalParameterTypes(List<ParameterData> constructorParameters)
        {
            var ambiguousParameters = constructorParameters
                .GroupBy(parameter => parameter.TypeFullName)
                .Select(group => new
                {
                    TypeFullName = group.Key,
                    Names = group.Select(parameter => parameter.Name).Distinct().OrderBy(name => name).ToArray()
                })
                .Where(group => group.Names.Length > 1)
                .ToList();

            if (ambiguousParameters.Count == 0)
                return;

            var details = string.Join("; ", ambiguousParameters.Select(group => $"{group.TypeFullName} ({string.Join(", ", group.Names)})"));
            throw new InvalidOperationException(
                $"Multiple externally provided values of the same type are not supported because FactoryGenerator resolves external values by type. Conflicting parameters: {details}. Wrap the values in distinct types or inject a dedicated options object.");
        }

        private static List<T> DistinctByTypeName<T>(IEnumerable<T> values, Func<T, string> typeNameSelector)
        {
            var distinct = new List<T>();
            var seenTypes = new HashSet<string>();

            foreach (var value in values)
            {
                if (!seenTypes.Add(typeNameSelector(value)))
                    continue;

                distinct.Add(value);
            }

            return distinct;
        }

        private static Dictionary<string, string> BuildBooleanParameterIdentifiers(IEnumerable<string> booleanKeys, IEnumerable<string> reservedNames)
        {
            var identifiers = new Dictionary<string, string>();
            var usedNames = new HashSet<string>(reservedNames);

            foreach (var booleanKey in booleanKeys)
            {
                var candidate = GetBooleanParameterIdentifier(booleanKey);
                var suffix = 1;
                while (!usedNames.Add(candidate))
                {
                    candidate = $"{candidate}_{suffix}";
                    suffix++;
                }

                identifiers[booleanKey] = candidate;
            }

            return identifiers;
        }

        private static Dictionary<string, string> BuildExternalParameterIdentifiers(IEnumerable<ParameterData> parameters, IEnumerable<string> reservedNames)
        {
            var identifiers = new Dictionary<string, string>();
            var usedNames = new HashSet<string>(reservedNames);

            foreach (var parameter in parameters.OrderBy(parameter => parameter.TypeFullName, StringComparer.Ordinal))
            {
                if (identifiers.ContainsKey(parameter.TypeFullName))
                    continue;

                var candidate = GetExternalParameterIdentifier(parameter.Name);
                var suffix = 1;
                while (!usedNames.Add(candidate))
                {
                    candidate = $"{candidate}_{suffix}";
                    suffix++;
                }

                identifiers[parameter.TypeFullName] = candidate;
            }

            return identifiers;
        }

        private static string BuildBooleanSelectionExpression(
            string typeFullName,
            IReadOnlyList<InjectionData> possibilities,
            IReadOnlyDictionary<string, string> booleanIdentifiers,
            Func<InjectionData, string> implementationExpression)
        {
            if (possibilities.All(possibility => possibility.BooleanInjection is null))
                return implementationExpression(possibilities.Last());

            var keys = possibilities.Select(possibility => possibility.BooleanInjection?.Key)
                .OfType<string>()
                .Distinct()
                .Reverse()
                .ToArray();

            var fallback = possibilities.LastOrDefault(possibility => possibility.BooleanInjection is null);
            var fallbackExpression = fallback is not null
                ? implementationExpression(fallback)
                : BuildMissingImplementationExpression(typeFullName);

            if (keys.Length == 0)
                return fallbackExpression;

            var last = keys.Last();
            var selection = new StringBuilder();
            foreach (var key in keys)
            {
                var selected = possibilities.LastOrDefault(possibility =>
                    possibility.BooleanInjection?.Value == true && possibility.BooleanInjection?.Key == key) ?? fallback;
                var selectedExpression = selected is not null
                    ? implementationExpression(selected)
                    : BuildMissingImplementationExpression(typeFullName);

                selection.Append(key == last
                    ? $"{booleanIdentifiers[key]} ? {selectedExpression} : {fallbackExpression}"
                    : $"{booleanIdentifiers[key]} ? {selectedExpression} : ");
            }

            return selection.ToString();
        }

        private static string GetBooleanParameterIdentifier(string booleanKey)
        {
            return GetSanitizedIdentifier(booleanKey, "boolean_");
        }

        private static string GetExternalParameterIdentifier(string parameterName)
        {
            return GetSanitizedIdentifier(parameterName, "argument_");
        }

        private static string GetSanitizedIdentifier(string value, string prefix)
        {
            if (SyntaxFacts.IsValidIdentifier(value))
                return value;

            var builder = new StringBuilder(prefix);
            foreach (var character in value)
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');

            var candidate = builder.ToString().TrimEnd('_');
            return SyntaxFacts.IsValidIdentifier(candidate) ? candidate : prefix.TrimEnd('_');
        }

        private static string BuildMissingImplementationExpression(string typeFullName)
        {
            return $"throw new global::System.InvalidOperationException(\"Cannot resolve {typeFullName} without a matching implementation\")";
        }
    }
}
