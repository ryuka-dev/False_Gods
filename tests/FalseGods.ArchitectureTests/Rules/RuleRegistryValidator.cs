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
    /// Rules claiming to be <see cref="CheckStatus.Implemented"/> or <see cref="CheckStatus.RequiredInCi"/>
    /// that no check actually cites. This is the half that catches a rule quietly declared done.
    /// </summary>
    public static IReadOnlyList<string> FindEnforcedRulesWithoutChecks(
        IEnumerable<ArchitectureRule> registry,
        IEnumerable<string> citedRuleIds)
    {
        var cited = citedRuleIds.ToHashSet(StringComparer.Ordinal);
        return registry
            .Where(r => r.CheckStatus is CheckStatus.Implemented or CheckStatus.RequiredInCi)
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
}
