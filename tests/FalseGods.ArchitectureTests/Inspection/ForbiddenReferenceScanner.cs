using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>One forbidden reference, in one project, in one configuration.</summary>
public sealed record ForbiddenReference(
    string ProjectName,
    string Configuration,
    string ForbiddenAssembly,
    string Description);

/// <summary>
/// The shared body of every "assembly X must not be referenced by project Y" check: given already-evaluated
/// project graphs and a list of forbidden assembly names, report every way one reaches the other.
///
/// Kept as a pure function over <see cref="EvaluatedReferences"/> so the reporting logic can be tested
/// against a graph built by hand, without MSBuild and without a project that violates anything on disk.
/// The evaluation itself — the part that needs MSBuild — stays in <see cref="ProjectGraphInspector"/>.
///
/// SCOPE, and it is narrower than the rules these checks serve. This sees a project's OWN evaluated
/// references: `ProjectReference`, `Reference`, and `HintPath`, wherever an import or a condition put them.
/// It does not see a transitive CLR dependency dragged in through a referenced assembly's own references,
/// because that never appears in this project's item lists — that is what the compiled-metadata layer is
/// for (Docs/ArchitectureEnforcement.md §6). A check built on this alone must not be described as covering
/// its whole rule.
/// </summary>
public static class ForbiddenReferenceScanner
{
    public static IReadOnlyList<ForbiddenReference> Scan(
        IEnumerable<EvaluatedReferences> evaluations,
        IEnumerable<string> forbiddenAssemblies)
    {
        var forbidden = forbiddenAssemblies.ToList();

        // A scan for nothing passes for every input. That is a check that stopped checking, and it must
        // fail loudly rather than report a clean result.
        if (forbidden.Count == 0)
        {
            throw new ArgumentException(
                "The forbidden-assembly list is empty, so this scan would pass unconditionally.",
                nameof(forbiddenAssemblies));
        }

        var offences = new List<ForbiddenReference>();

        foreach (var evaluated in evaluations)
        {
            var projectName = Path.GetFileNameWithoutExtension(evaluated.ProjectPath);

            foreach (var assembly in forbidden)
            {
                if (!evaluated.ReferencesAssemblyNamed(assembly))
                    continue;

                offences.AddRange(evaluated
                    .DescribeReferencesTo(assembly)
                    .Select(description => new ForbiddenReference(
                        projectName, evaluated.Configuration, assembly, description)));
            }
        }

        return offences;
    }

    /// <summary>Renders offences for a failure message: which project, which configuration, which reference.</summary>
    public static string Format(IEnumerable<ForbiddenReference> offences) =>
        string.Join(Environment.NewLine, offences.Select(offence =>
            $"  {offence.ProjectName} [{offence.Configuration}] references {offence.ForbiddenAssembly}" +
            $"{Environment.NewLine}      via {offence.Description}"));
}
