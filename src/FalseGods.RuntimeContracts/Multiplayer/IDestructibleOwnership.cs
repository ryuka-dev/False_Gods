namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// Claiming a destructible as ours, so the session layer does not also try to replicate it.
    /// </summary>
    /// <remarks>
    /// <para>A session layer mirrors the destruction of the breakables a level generates: those exist on every peer
    /// because every peer generates the same level, so a break can be matched across peers by where the object was
    /// spawned.</para>
    /// <para><b>Ours break that assumption.</b> They are made at runtime, many of them in one place, and we already
    /// replicate them ourselves from the command that created them. Matched by position, a break on one peer
    /// destroys whichever of the heap happens to be nearest on another — including one in the middle of being
    /// thrown. Claiming them says: leave these alone, in both directions.</para>
    /// <para>The destructible crosses this seam as <see cref="object"/> for the same reason a spawn owner does in
    /// <see cref="ISpawnOwnership"/>: it is a game-engine type this assembly may not name.</para>
    /// </remarks>
    public interface IDestructibleOwnership
    {
        /// <summary>
        /// Declare that <paramref name="destructible"/> is ours to replicate, so the session layer neither
        /// broadcasts its destruction nor picks it as the local match for another peer's. Doing nothing is a
        /// valid implementation — the encounter still works, its destructibles are just also handled by the
        /// session layer.
        /// </summary>
        void ClaimAsOurs(object destructible);
    }
}
