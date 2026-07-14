using System.Collections.Generic;
using FalseGods.Core.Simulation;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;

namespace FalseGods.Integration.Sulfur.Simulation
{
    /// <summary>
    /// The single-player implementation of the boss domain's <see cref="IEncounterParticipantQuery"/>: it projects
    /// the game's live players onto <see cref="ParticipantId"/> / <see cref="SimVector2"/> so the boss has real
    /// targets to face, approach, and aim at.
    /// </summary>
    /// <remarks>
    /// This replaces the probe's camera stand-in with the actual roster. It reads the game's <b>public</b>
    /// <c>GameManager</c> surface (verified against <c>Decompiled/PerfectRandom.Sulfur.Core/.../GameManager.cs</c> and
    /// <c>Units/Player.cs</c>): <c>StaticInstance&lt;GameManager&gt;.Instance</c>, its <c>Players</c> list, and each
    /// <c>Player</c>'s <c>playerIndex</c> and <c>transform</c>. Because those members are public, this is a
    /// compile-time-typed call against the referenced game assemblies — <b>no reflection</b>, so a game update that
    /// changed a signature would break the build rather than fail silently at runtime.
    ///
    /// <para>
    /// It only ever <em>observes</em> the roster (Docs/DependencyRules.md §3, Docs/Architecture.md §9): it never
    /// mutates it. The boss reasons on the flat arena plane, so a player's world position is projected to its
    /// (X, Z). The contract's empty-roster and unknown-participant paths are honoured so the boss idles rather than
    /// attacking nothing when no player is present or a player has gone.
    /// </para>
    ///
    /// <para>
    /// <b>Runtime behaviour to confirm in-game (Phase 2 verification):</b> that <c>Instance</c> and <c>Players</c>
    /// are populated once a level and player exist (and are safely null/empty before that), and that
    /// <c>Player.transform.position</c> tracks the moving player. These are lifecycle facts the compiler cannot
    /// prove; the Composition Root's boss facing/approach is the observable check.
    /// </para>
    /// </remarks>
    public sealed class SulfurParticipantQuery : IEncounterParticipantQuery
    {
        // Reused so the per-advance Participants read does not allocate. Safe because the boss enumerates the roster
        // immediately within one advance and never retains the reference (FalseGods.Core BossSimulation).
        private readonly List<ParticipantId> _ids = new List<ParticipantId>();

        public IReadOnlyList<ParticipantId> Participants
        {
            get
            {
                _ids.Clear();

                var players = ActivePlayers();
                if (players == null)
                {
                    return _ids;
                }

                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    if (player != null)
                    {
                        _ids.Add(new ParticipantId(player.playerIndex));
                    }
                }

                return _ids;
            }
        }

        public bool TryGetPosition(ParticipantId participant, out SimVector2 position)
        {
            var players = ActivePlayers();
            if (players != null)
            {
                for (var i = 0; i < players.Count; i++)
                {
                    var player = players[i];
                    if (player != null && player.playerIndex == participant.Value)
                    {
                        // The arena floor is the horizontal plane; the boss reasons in (X, Z) only (SimVector2).
                        var world = player.transform.position;
                        position = new SimVector2(world.x, world.z);
                        return true;
                    }
                }
            }

            position = default;
            return false;
        }

        /// <summary>
        /// The game's live player list, or null when there is no game manager yet (before a level loads). Uses Unity's
        /// null semantics so a destroyed manager also reads as absent.
        /// </summary>
        private static List<Player>? ActivePlayers()
        {
            var gameManager = StaticInstance<GameManager>.Instance;
            return gameManager != null ? gameManager.Players : null;
        }
    }
}
