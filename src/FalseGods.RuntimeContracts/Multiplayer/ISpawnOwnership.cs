using System;

namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// Declaring which of this peer's runtime unit spawns are the host's to make.
    /// </summary>
    /// <remarks>
    /// <para>A session layer does not generally replicate the <i>creation</i> of a unit: the enemies a level
    /// generates exist on every peer because every peer generates that level. A unit spawned afterwards — by a
    /// boss summoning minions, say — exists only where it was spawned, and the other peers see an empty room.</para>
    /// <para>Declaring an owner says: <b>only the host spawns these</b>, so the session layer should carry them to
    /// the clients rather than leave them one-sided. It is a promise about our own behaviour, not a request for a
    /// mechanism — a composition that also spawned on its clients would double every unit and each peer would be
    /// fighting a different set.</para>
    /// <para>The owner crosses this seam as <see cref="object"/> on purpose: what identifies a spawn's owner is a
    /// game-engine type this assembly may not name, and the adapter is the only side that needs to understand
    /// it.</para>
    /// </remarks>
    public interface ISpawnOwnership
    {
        /// <summary>
        /// Declare that runtime unit spawns made through <paramref name="spawnOwner"/> are host-authoritative.
        /// Dispose the returned token to withdraw the declaration. Returns <c>null</c> when the integration
        /// cannot carry such spawns — the caller then knows its spawns stay one-sided, rather than assuming they
        /// travelled.
        /// </summary>
        IDisposable? DeclareHostAuthoritative(object spawnOwner);
    }
}
