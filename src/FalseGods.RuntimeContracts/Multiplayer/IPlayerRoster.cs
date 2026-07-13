using System.Collections.Generic;
using FalseGods.RuntimeContracts.Transport;

namespace FalseGods.RuntimeContracts.Multiplayer
{
    /// <summary>
    /// The current session membership — who must be accounted for before the encounter starts.
    /// </summary>
    /// <remarks>
    /// The ready gate's required set is every current member of this roster, including the host's own local peer
    /// (Docs/MultiplayerLoadingContract.md §5.3): the gate passes only when every member has reported ready over
    /// matching content. Membership is a session concept (a set of <see cref="SessionPeerId"/>), deliberately
    /// distinct from the boss domain's participant/target query (Docs/DependencyRules.md §3). In single-player
    /// <c>Integration.Sulfur</c> supplies exactly one member, so the gate resolves the instant the local peer
    /// readies — the same code path as multiplayer.
    ///
    /// <para>
    /// Identity and positions (the fuller <c>IPlayerRoster</c> of Architecture §6) are added when a consumer needs
    /// them (Docs/RiskList.md R31); the gate needs only membership.
    /// </para>
    /// </remarks>
    public interface IPlayerRoster
    {
        /// <summary>The peers that are currently required members of the session.</summary>
        IReadOnlyList<SessionPeerId> Members { get; }
    }
}
