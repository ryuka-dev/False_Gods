namespace FalseGods.RuntimeContracts.Presentation
{
    /// <summary>
    /// Which attack visual a renderer should play — the presentation-side counterpart of Core's
    /// <c>BossAttackKind</c>.
    /// </summary>
    /// <remarks>
    /// Separate from the domain enum for the same reason as <see cref="BossVisualActivity"/>: presentation owns its
    /// own vocabulary so a domain or wire change does not reach into renderer code (Docs/Architecture.md §7). The
    /// mapper in <c>FalseGods.Application</c> is the one place that knows both spellings.
    /// </remarks>
    public enum AttackVisualKind
    {
        /// <summary>A projectile fired along an aim direction.</summary>
        Projectile = 1,

        /// <summary>An area-of-effect attack telegraphed on the ground.</summary>
        Area = 2,
    }
}
