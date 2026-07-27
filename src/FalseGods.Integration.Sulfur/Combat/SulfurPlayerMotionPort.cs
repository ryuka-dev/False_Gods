using System.Collections.Generic;
using FalseGods.Application.Combat;
using FalseGods.Core.Simulation;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// <see cref="IPlayerMotionPort"/> over the game's own player. Position is the player's transform; velocity is
    /// its movement controller's own reading — the walk velocity plus whatever momentum it is carrying — projected
    /// to the arena's ground plane.
    /// </summary>
    /// <remarks>
    /// The velocity comes from <c>Player.movement.GetVelocity()</c> (the controller's <c>savedVelocity</c>: the
    /// grounded movement velocity plus world-space momentum, verified against the live
    /// <c>CharacterMovementFundamentals</c> build), not from the rigidbody — the character controller drives the
    /// transform, so the rigidbody's own velocity does not track a walking player. All members read are public;
    /// no reflection. That the player and its controller exist is a lifecycle fact the compiler cannot prove, so a
    /// missing one is reported as an unknown motion rather than assumed.
    /// </remarks>
    public sealed class SulfurPlayerMotionPort : IPlayerMotionPort
    {
        public PlayerMotion TryReadLocalPlayer()
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            var player = gameManager != null ? gameManager.PlayerScript : null;
            if (player == null)
            {
                return default; // no level loaded — Known stays false
            }

            var position = player.transform.position;

            var controller = player.movement;
            if (controller == null)
            {
                // A player without a live movement controller (mid-spawn); its position is still worth having, but
                // with no velocity there is nothing to lead, so report it as still.
                return new PlayerMotion(new SimVector2(position.x, position.z), SimVector2.Zero);
            }

            var velocity = controller.GetVelocity();
            return new PlayerMotion(
                new SimVector2(position.x, position.z),
                new SimVector2(velocity.x, velocity.z));
        }

        public void ReadPlayersToThrowAt(IList<PlayerAim> into)
        {
            into.Clear();

            var gameManager = StaticInstance<GameManager>.Instance;
            var players = gameManager != null ? gameManager.Players : null;
            if (players == null)
            {
                return;
            }

            for (var i = 0; i < players.Count; i++)
            {
                var player = players[i];
                if (player == null || !FightingPlayers.IsFighting(player))
                {
                    continue;
                }

                var position = player.transform.position;
                var ground = new SimVector2(position.x, position.z);

                // Only a player with a live movement controller can be asked how fast it is going — which in a
                // session means the one on this machine. The rest are figures the session keeps up to date, so
                // their speed has to be worked out from how their position moves; that is the caller's to track.
                var controller = player.movement;
                if (controller == null)
                {
                    into.Add(new PlayerAim(player.playerIndex, ground, position.y, SimVector2.Zero, false));
                    continue;
                }

                var velocity = controller.GetVelocity();
                into.Add(new PlayerAim(
                    player.playerIndex, ground, position.y, new SimVector2(velocity.x, velocity.z), true));
            }
        }
    }
}
