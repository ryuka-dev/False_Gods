using System;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.RuntimeContracts.Multiplayer;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// The capability bundle this adapter registers with <c>FalseGodsIntegrations</c>: the three RuntimeContracts
    /// ports implemented over the ST public bridge. A plain holder — only project-owned contracts cross the seam
    /// (ADR-004).
    /// </summary>
    internal sealed class SulfurTogetherIntegration : IFalseGodsIntegration
    {
        public SulfurTogetherIntegration(
            IMultiplayerSession session,
            IEncounterChannel channel,
            IPlayerRoster roster,
            IParticipantPeerMap players,
            ISpawnOwnership spawns,
            IPlayerLifeQuery lives)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            Channel = channel ?? throw new ArgumentNullException(nameof(channel));
            Roster = roster ?? throw new ArgumentNullException(nameof(roster));
            Players = players ?? throw new ArgumentNullException(nameof(players));
            Spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
            Lives = lives ?? throw new ArgumentNullException(nameof(lives));
        }

        public IMultiplayerSession Session { get; }

        public IEncounterChannel Channel { get; }

        public IPlayerRoster Roster { get; }

        public IParticipantPeerMap Players { get; }

        public ISpawnOwnership Spawns { get; }

        public IPlayerLifeQuery Lives { get; }
    }
}
