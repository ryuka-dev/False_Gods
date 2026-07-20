using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace FalseGods.Integration.Sulfur.Simulation
{
    /// <summary>
    /// Reads this machine's own player's roster index — the boss participant id of the player at this keyboard —
    /// from the <b>public</b> <c>GameManager.PlayerScript.playerIndex</c> (verified against
    /// <c>D:\SULFUR_backup</c> v0.18.5), compile-time typed, no reflection.
    /// </summary>
    /// <remarks>
    /// The host uses this to tell its own player's boss hits from a remote peer's; a client uses it to know which
    /// player a host-sent <c>BossHitPlayer</c> lands on (always the receiver's own). It is the same
    /// <c>playerIndex</c> <see cref="SulfurParticipantQuery"/> projects, so the boss's participant ids and this
    /// agree on the local player.
    /// </remarks>
    public sealed class SulfurLocalPlayer
    {
        /// <summary>The local player's <c>playerIndex</c>, or false before a player exists (no level loaded).</summary>
        public bool TryGetLocalParticipantIndex(out int playerIndex)
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            var script = gameManager != null ? gameManager.PlayerScript : null;
            if (script != null)
            {
                playerIndex = script.playerIndex;
                return true;
            }

            playerIndex = -1;
            return false;
        }
    }
}
