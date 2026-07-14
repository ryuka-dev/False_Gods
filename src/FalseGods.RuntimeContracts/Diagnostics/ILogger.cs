namespace FalseGods.RuntimeContracts.Diagnostics
{
    /// <summary>
    /// A minimal project-owned logging seam so inner and presentation code can emit diagnostics without taking a
    /// dependency on BepInEx, Unity, or any concrete logger.
    /// </summary>
    /// <remarks>
    /// This is the <c>ILogger</c> that <c>FalseGods.RuntimeContracts</c> owns (Docs/Architecture.md §3). It exists so
    /// that logging stays a <b>diagnostic</b> concern, fully separable from functionality: consumers accept an
    /// <c>ILogger?</c> and log only when one is supplied, so behaviour never depends on a logger being present (global
    /// engineering rules §7). The one concrete implementation lives in the Composition Root, over BepInEx's logger.
    ///
    /// <para>
    /// Kept deliberately tiny — two levels, plain strings. It is not a structured-logging framework; if a real need
    /// for scopes or levels appears, extend it then, not now.
    /// </para>
    /// </remarks>
    public interface ILogger
    {
        /// <summary>Record an informational diagnostic line.</summary>
        void Log(string message);

        /// <summary>Record a warning — something unexpected that did not stop the operation.</summary>
        void LogWarning(string message);
    }
}
