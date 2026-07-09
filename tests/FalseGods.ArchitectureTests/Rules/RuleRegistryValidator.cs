using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace FalseGods.ArchitectureTests.Rules;

/// <summary>
/// The logic behind FG-ARCH-010, kept as pure functions over their inputs.
///
/// This matters: the check that guards the registry has to be testable against a registry that is
/// deliberately wrong, and it cannot be if it can only ever read the real one.
/// </summary>
public static class RuleRegistryValidator
{
    private static readonly Regex RuleIdPattern = new(@"^FG-ARCH-\d{3}$", RegexOptions.Compiled);

    /// <summary>Rule ids cited by checks that no registry entry defines.</summary>
    public static IReadOnlyList<string> FindUnknownRuleIds(
        IEnumerable<string> citedRuleIds,
        IEnumerable<ArchitectureRule> registry)
    {
        var known = registry.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        return citedRuleIds.Distinct(StringComparer.Ordinal)
                           .Where(id => !known.Contains(id))
                           .OrderBy(id => id, StringComparer.Ordinal)
                           .ToList();
    }

    /// <summary>
    /// Rules with at least one <see cref="CheckStatus.Implemented"/> or <see cref="CheckStatus.RequiredInCi"/>
    /// layer that no check actually cites. This is the half that catches a rule quietly declared done.
    /// </summary>
    public static IReadOnlyList<string> FindEnforcedRulesWithoutChecks(
        IEnumerable<ArchitectureRule> registry,
        IEnumerable<string> citedRuleIds)
    {
        var cited = citedRuleIds.ToHashSet(StringComparer.Ordinal);
        return registry
            .Where(r => r.IsEnforcedByAnyLayer)
            .Where(r => !cited.Contains(r.Id))
            .Select(r => r.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    public static IReadOnlyList<string> FindMalformedRuleIds(IEnumerable<ArchitectureRule> registry) =>
        registry.Select(r => r.Id)
                .Where(id => !RuleIdPattern.IsMatch(id))
                .ToList();

    public static IReadOnlyList<string> FindDuplicateRuleIds(IEnumerable<ArchitectureRule> registry) =>
        registry.GroupBy(r => r.Id, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();

    /// <summary>
    /// Every rule id cited by an <see cref="ArchitectureRuleAttribute"/> anywhere in the given assembly,
    /// on a type or on a method.
    /// </summary>
    public static IReadOnlyList<string> CollectCitedRuleIds(Assembly assembly)
    {
        var ids = new List<string>();

        foreach (var type in assembly.GetTypes())
        {
            ids.AddRange(type.GetCustomAttributes<ArchitectureRuleAttribute>(inherit: false)
                             .Select(a => a.RuleId));

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                   BindingFlags.Instance | BindingFlags.Static |
                                                   BindingFlags.DeclaredOnly))
            {
                ids.AddRange(method.GetCustomAttributes<ArchitectureRuleAttribute>(inherit: false)
                                   .Select(a => a.RuleId));
            }
        }

        return ids.Distinct(StringComparer.Ordinal)
                  .OrderBy(id => id, StringComparer.Ordinal)
                  .ToList();
    }

    /// <summary>Rule ids that have a <c>### FG-ARCH-0NN</c> heading in the enforcement document.</summary>
    public static IReadOnlyList<string> ParseRuleIdsFromDocument(string markdown) =>
        Regex.Matches(markdown, @"^###\s+(FG-ARCH-\d{3})\b", RegexOptions.Multiline)
             .Select(m => m.Groups[1].Value)
             .Distinct(StringComparer.Ordinal)
             .OrderBy(id => id, StringComparer.Ordinal)
             .ToList();

    /// <summary>
    /// Rule ids whose document heading has no <c>&lt;a id="fg-arch-0nn"&gt;</c> anchor.
    ///
    /// Without the anchor, the doc link every failure message prints does not resolve, and a failure a
    /// developer cannot act on is a failed check whatever it found.
    /// </summary>
    public static IReadOnlyList<string> FindRuleIdsMissingDocAnchor(
        IEnumerable<ArchitectureRule> registry,
        string markdown)
    {
        return registry
            .Select(r => r.Id)
            .Where(id => !markdown.Contains($"<a id=\"{id.ToLowerInvariant()}\"></a>", StringComparison.Ordinal))
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    // ------------------------------------------------------------ layer status

    /// <summary>One row of the document's layer-status table, or of the registry's projection onto it.</summary>
    public sealed record LayerStatusRow(string RuleId, string Layer, string Status)
    {
        public override string ToString() => $"{RuleId} | {Layer} | {Status}";
    }

    public const string LayerTableBeginMarker = "<!-- BEGIN FG-ARCH-LAYER-STATUS -->";
    public const string LayerTableEndMarker = "<!-- END FG-ARCH-LAYER-STATUS -->";

    /// <summary>
    /// The layer-status table the document publishes, parsed from between the two markers.
    ///
    /// A prose "Check status:" line cannot be parsed without a brittle regex over English, and a brittle
    /// check is worse than none (§12). So the document carries one delimited table whose three columns are
    /// exactly the registry's three fields, and this reads it back.
    ///
    /// Returns an empty list when the markers are absent or reversed. That is not "the table is fine" —
    /// callers must treat an empty result as a failure, or renaming a marker would silently disable the
    /// comparison.
    /// </summary>
    public static IReadOnlyList<LayerStatusRow> ParseLayerStatusTable(string markdown)
    {
        var start = markdown.IndexOf(LayerTableBeginMarker, StringComparison.Ordinal);
        var end = markdown.IndexOf(LayerTableEndMarker, StringComparison.Ordinal);

        if (start < 0 || end < 0 || end <= start)
            return Array.Empty<LayerStatusRow>();

        var block = markdown[(start + LayerTableBeginMarker.Length)..end];
        var rows = new List<LayerStatusRow>();

        foreach (var line in block.Split('\n'))
        {
            var trimmed = line.Trim();

            if (!trimmed.StartsWith('|'))
                continue;

            var cells = trimmed.Trim('|')
                               .Split('|')
                               // Tolerate the markdown a human would reach for: `code`, **bold**.
                               .Select(cell => cell.Trim().Trim('`', '*', ' '))
                               .ToList();

            // Skips the header row and the |---|---|---| separator, neither of which starts with a rule id.
            if (cells.Count != 3 || !RuleIdPattern.IsMatch(cells[0]))
                continue;

            rows.Add(new LayerStatusRow(cells[0], cells[1], cells[2]));
        }

        return rows;
    }

    /// <summary>The registry, expressed in the same three columns, so the two can be compared as sets.</summary>
    public static IReadOnlyList<LayerStatusRow> ProjectRegistryOntoLayerStatusRows(IEnumerable<ArchitectureRule> registry) =>
        registry.SelectMany(rule => rule.Layers.Select(layer =>
                    new LayerStatusRow(rule.Id, layer.Name, ArchitectureRuleRegistry.Display(layer.Status))))
                .ToList();

    /// <summary>
    /// Rows present in one side and not the other, as printable strings. The registry and the document must
    /// agree exactly: a layer promoted in code but not in the table is a rule whose documented enforcement
    /// is now a lie, and vice versa.
    /// </summary>
    public static (IReadOnlyList<string> OnlyInDocument, IReadOnlyList<string> OnlyInRegistry) DiffLayerStatusRows(
        IEnumerable<LayerStatusRow> documentRows,
        IEnumerable<LayerStatusRow> registryRows)
    {
        var document = documentRows.Select(r => r.ToString()).ToList();
        var registry = registryRows.Select(r => r.ToString()).ToList();

        return (Sorted(document.Except(registry, StringComparer.Ordinal)),
                Sorted(registry.Except(document, StringComparer.Ordinal)));

        static IReadOnlyList<string> Sorted(IEnumerable<string> rows) =>
            rows.OrderBy(row => row, StringComparer.Ordinal).ToList();
    }
}
