using System;

namespace FalseGods.ArchitectureTests.Rules;

/// <summary>
/// Declares which registered architecture rule a check enforces.
///
/// The rule id lives in an attribute rather than in the test's name so that FG-ARCH-010 can find it
/// structurally. A rule id buried in free text — a method name, an xUnit trait string, a comment — is
/// invisible to the very check that exists to catch an unregistered or unenforced rule.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class ArchitectureRuleAttribute : Attribute
{
    public ArchitectureRuleAttribute(string ruleId) => RuleId = ruleId;

    public string RuleId { get; }
}
