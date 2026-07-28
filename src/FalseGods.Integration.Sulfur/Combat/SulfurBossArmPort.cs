using System;
using System.Collections.Generic;
using FalseGods.Application.Combat;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Core.Units.AI;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IBossArmPort"/>: the arms a starved boss puts up are the cave
    /// boss's own, raised through the game's own entry point and then carried with it.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing here drives the arm's behaviour.</b> Measured on v0.18.5: the creature's animator starts
    /// in <c>Submerged</c>, rises into <c>Shoot</c> the moment its behaviour tree raises <c>Attack</c>, throws on
    /// an animation event, and sinks again when the clip ends — so appearing, throwing and sinking are the game's,
    /// repeating for as long as it has someone to aim at. The tree is already running when the unit is spawned
    /// (<c>SpawnUnit</c> activates it), and its only condition is a target inside its throwing range. So this
    /// class is <i>where</i>, <i>how big</i>, <i>at whom</i> and <i>how far</i> — never how.</para>
    /// <para><b>And where is the boss.</b> The creature has no navigation agent at all — it cannot take one step —
    /// so its position is set here every frame from the boss's own. Reading the level instead was measured to be
    /// wrong twice over: the arena's scenery is deliberately kept off the navigation layers, so snapping to
    /// navigable ground puts an arm <i>under</i> whatever prop is standing on it (an arm rose in the middle of a
    /// sludge pool, buried to the waist), and a fixed distance from a fixed point knows nothing about what is
    /// there anyway. The boss already stands where the room authored, at the height the room authored; taking the
    /// arms' place from the boss inherits both and measures nothing.</para>
    /// <para><b>Moving the transform is what a client sees.</b> The arms are ordinary host-spawned units, so the
    /// session layer mirrors them and follows their transform; they are deliberately left parented where the game
    /// puts its own units rather than under the boss's presentation rig, which carries a display scale that would
    /// be inherited by anything hung from it.</para>
    /// <para><b>Host only.</b> The host owns enemies; a client's arms are mirrored puppets.</para>
    /// </remarks>
    public sealed class SulfurBossArmPort : IBossArmPort
    {
        /// <summary>
        /// The child everything the creature draws hangs from, and therefore where it really is.
        /// </summary>
        /// <remarks>
        /// <b>This prefab's origin is not where it stands.</b> Measured on v0.18.5: the whole rig sits about 2.45 m
        /// along local X of the unit's own transform, while the references the game reads for its feet
        /// (<c>FeetFlat</c>, <c>FeetPitched</c>) sit at the origin. So placing two arms symmetrically about the
        /// boss puts their <i>origins</i> either side of it and every visible arm the same 2.45 m off — which is
        /// exactly how it looked in game: two arms evenly spaced about a point that was not the boss. The offset is
        /// read off the live object rather than written down here, so it survives the prefab changing and comes out
        /// already multiplied by whatever scale the arm is wearing.
        /// </remarks>
        private const string ArtRootName = "Root";

        /// <summary>
        /// How far an arm will throw, replacing the range the creature was authored with.
        /// </summary>
        /// <remarks>
        /// The 30 m it ships with is the size of the room the vanilla cave boss is fought in. Ours is eighty
        /// metres across, so a player who simply walked to the far side stood outside the arms' reach and the
        /// rage stopped costing anything — which is the one thing it exists to do. Set past the far corner rather
        /// than to it, so the answer does not depend on where in the room the boss happens to be standing.
        /// <para>This is the only number of the creature's own that is overridden, and it is a room fact rather
        /// than a boss one: the arm still decides everything about how it throws.</para>
        /// </remarks>
        private const float ThrowRange = 100f;

        private readonly MonoBehaviour _host;
        private readonly ILogger? _logger;
        private readonly List<Unit> _raised = new List<Unit>();

        // Which of the boss's stations each arm holds - right, left, second rank out, and so on - index-aligned
        // with _raised as they arrive. Deliberately the station's NUMBER and not the offset it works out to: the
        // distances are being tuned while a fight runs, so baking them in here would freeze an arm at whatever
        // they were when it rose. An arm's station is decided when it is asked for, not when it lands, so a slow
        // load cannot put two on the same side.
        private readonly List<int> _stations = new List<int>();

        // Each arm's art root, so the standing-point correction costs one transform read rather than a search.
        // Null for an arm whose prefab has no such child: it is then placed by its origin, which is the old
        // behaviour and visibly off, but not broken.
        private readonly List<Transform?> _artRoots = new List<Transform?>();

        // Each arm's own list of who it may throw at, handed to its agent once and refilled in place afterwards.
        // One list per arm and never shared, because the game takes the reference and prunes it as it reads it.
        private readonly List<List<Unit>> _quarry = new List<List<Unit>>();

        private static readonly List<Unit> EmptyQuarry = new List<Unit>(0);

        // Where the arms were last told to be, so one that finishes loading after the others can be put in its
        // place immediately rather than standing at its spawn point until the next frame.
        private ArmPlacement _placement;

        // Which raising the arms in flight belong to. An arm is loaded asynchronously, so a rage that ends while
        // one is still on its way would otherwise leave it standing and throwing after the boss had calmed: the
        // arrival checks that it is still wanted before it is kept.
        private int _generation;

        // False while the arms were adopted rather than raised. An adopted arm belongs to the host: this peer only
        // carries it, and must not tell a puppet who to throw at.
        private bool _ours;

        /// <param name="host">The behaviour whose lifetime scopes the asynchronous load — the game cancels the
        /// spawn if it is destroyed first, which is what should happen on a plugin unload.</param>
        public SulfurBossArmPort(MonoBehaviour host, ILogger? logger = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _logger = logger;
        }

        public int Raised
        {
            get
            {
                Forget();
                return _raised.Count;
            }
        }

        public void Raise(int count, ArmPlacement placement)
        {
            if (count <= 0 || Raised > 0)
            {
                return; // nothing asked for, or already up: the rage raises them once and keeps them
            }

            var definition = UnitIds.GoblinCousinArm.GetAsset();
            if (definition == null)
            {
                _logger?.LogWarning("[arm] the game has no definition for the cousin's arm; the boss rages "
                    + "empty-handed.");
                return;
            }

            _placement = placement;
            _ours = true;
            var generation = ++_generation;
            for (var i = 0; i < count; i++)
            {
                RaiseOne(definition, StandingPoint(placement, i), i, generation);
            }

            _logger?.Log($"[arm] {count} arm(s) rising at the boss's sides ({placement.SideDistance:0.#}m out, "
                + $"{placement.ForwardOffset:0.#}m forward, {placement.Lift:0.#}m up, {placement.Scale:0.##}x); "
                + "they follow it until it is supplied again.");
        }

        public void Follow(ArmPlacement placement)
        {
            _placement = placement;
            for (var i = 0; i < _raised.Count; i++)
            {
                Put(_raised[i], _artRoots[i], _stations[i], placement);
                if (_ours)
                {
                    FightingPlayers.FillFighting(_quarry[i]);
                }
            }
        }

        public void Adopt(int count)
        {
            if (count <= 0 || Raised >= count)
            {
                return; // nothing wanted, or already carrying them
            }

            var gameManager = StaticInstance<GameManager>.Instance;
            var npcs = gameManager != null ? gameManager.aliveNpcs : null;
            if (npcs == null)
            {
                return;
            }

            var wanted = UnitIds.GoblinCousinArm.value;
            for (var i = 0; i < npcs.Count && _raised.Count < count; i++)
            {
                var npc = npcs[i];
                if (npc == null || !npc.IsAlive || npc.unitSO == null || npc.unitSO.id.value != wanted)
                {
                    continue;
                }

                if (Holding(npc))
                {
                    continue;
                }

                _ours = false;
                _raised.Add(npc);
                _stations.Add(_raised.Count - 1);
                _artRoots.Add(FindArtRoot(npc));
                _quarry.Add(EmptyQuarry); // never read: an adopted arm is a puppet and decides nothing
            }

            if (_raised.Count > 0)
            {
                _logger?.Log($"[arm] carrying {_raised.Count} arm(s) the session put here; this peer places them "
                    + "itself from now on.");
            }
        }

        public void Release()
        {
            if (_raised.Count == 0)
            {
                return;
            }

            _generation++;
            _logger?.Log($"[arm] letting go of {_raised.Count} arm(s); they are the host's to end.");
            _raised.Clear();
            _stations.Clear();
            _artRoots.Clear();
            _quarry.Clear();
        }

        /// <summary>Whether this arm is already one of ours, so a repeated adopt cannot take it twice.</summary>
        private bool Holding(Unit arm)
        {
            for (var i = 0; i < _raised.Count; i++)
            {
                if (ReferenceEquals(_raised[i], arm))
                {
                    return true;
                }
            }

            return false;
        }

        public void LowerAll()
        {
            // Anything still being loaded belongs to a rage that is over.
            _generation++;

            var lowered = 0;
            for (var i = 0; i < _raised.Count; i++)
            {
                var arm = _raised[i];
                if (arm == null)
                {
                    continue;
                }

                try
                {
                    // The game's own way of taking an arm away: dying is what plays the sink, and it works on a
                    // creature that cannot be killed by damage.
                    arm.Die();
                    lowered++;
                }
                catch (Exception exception)
                {
                    // Routine while the game is shutting down: the managers Die() reports to are gone by then.
                    _logger?.LogWarning($"[arm] an arm could not be taken down ({exception.Message}); leaving it.");
                }
            }

            _raised.Clear();
            _stations.Clear();
            _artRoots.Clear();
            _quarry.Clear();
            if (lowered > 0)
            {
                _logger?.Log($"[arm] {lowered} arm(s) sink back into the ground.");
            }
        }

        /// <summary>Fire the asynchronous spawn and keep the arm when it lands, if the rage that asked for it is
        /// still running. Deliberately not awaited: raising is a moment in the fight.</summary>
        private async void RaiseOne(UnitSO definition, Vector3 position, int station, int generation)
        {
            Unit unit;
            try
            {
                unit = await definition.SpawnUnitAsync(_host, position);
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arm] an arm failed to rise: {exception.Message}");
                return;
            }

            if (unit == null)
            {
                _logger?.LogWarning("[arm] an arm failed to rise (the game returned no unit).");
                return;
            }

            if (generation != _generation)
            {
                // The boss was supplied again, or the fight ended, while this one was loading.
                try { unit.Die(); } catch (Exception) { }
                return;
            }

            // Belt and braces: the creature already carries enough health to be unkillable, and the game's own
            // spawners mark it invulnerable besides. An arm is scenery with a throw, not a health bar.
            try { unit.SetInvulnerable(state: true); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arm] an arm could not be made invulnerable ({exception.Message}).");
            }

            ReachAcrossTheRoom(unit);

            var artRoot = FindArtRoot(unit);
            var quarry = new List<Unit>(4);
            FightingPlayers.FillFighting(quarry);
            AimWithoutSight(unit, quarry);

            _raised.Add(unit);
            _stations.Add(station);
            _artRoots.Add(artRoot);
            _quarry.Add(quarry);

            // Put it where it belongs now rather than a frame late: a big arm dropped at its spawn point and
            // corrected next frame is a visible jump.
            Put(unit, artRoot, station, _placement);
        }

        /// <summary>
        /// Size an arm and stand it at its station, correcting for the fact that this creature is not drawn at its
        /// own origin.
        /// </summary>
        /// <remarks>
        /// <para>The scale goes on first, because the art offset that follows has to be read at the size the arm
        /// is actually wearing — scaling the rig scales how far off-origin it is drawn.</para>
        /// <para>Only the horizontal part of the offset is taken out. The vertical belongs to how deep in the
        /// ground the creature should sit, which is what the placement's lift is for and is set by eye.</para>
        /// </remarks>
        private static void Put(Unit? arm, Transform? artRoot, int station, ArmPlacement placement)
        {
            if (arm == null)
            {
                return;
            }

            var transform = arm.transform;
            if (placement.Scale > 0f)
            {
                transform.localScale = new Vector3(placement.Scale, placement.Scale, placement.Scale);
            }

            var drift = Vector3.zero;
            if (artRoot != null)
            {
                drift = artRoot.position - transform.position;
                drift.y = 0f;
            }

            transform.position = StandingPoint(placement, station) - drift;
        }

        /// <summary>Let an arm throw the length of this room rather than the length of the one it was drawn
        /// for. Failing is a shorter-ranged arm, not a broken one.</summary>
        private void ReachAcrossTheRoom(Unit arm)
        {
            try
            {
                if (arm is Npc npc)
                {
                    npc.rangedAttackRange = ThrowRange;
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[arm] an arm kept its own throwing range ({exception.Message}); it will "
                    + "only answer players close to the boss.");
            }
        }

        /// <summary>
        /// Tell an arm who it may throw at, without asking whether it can see them.
        /// </summary>
        /// <remarks>
        /// <para><b>Why it needs telling.</b> Left to itself the creature acquires a target through the game's
        /// ordinary detection, which is line-of-sight gated: a player standing lower than the arm, or behind the
        /// lip of a terrace, is simply not there as far as it is concerned, and it stands with nothing to do.
        /// That is right for a goblin that has to notice you and wrong for a boss's own limb, which is not
        /// searching for anyone — the players are already in the fight with it.</para>
        /// <para><b>Through the game's own seam, not by flipping its sight off.</b> <c>overridetargets</c> is what
        /// the game itself uses to hand a creature a fixed set of enemies (endless mode does it every wave, the
        /// crypt spawner does it on spawn), and the agent consults it <i>before</i> anything about sight. The
        /// obvious-looking alternative — clearing <c>useLineOfSight</c> and setting <c>onlyTargetPlayer</c> — is a
        /// trap already paid for once: the agent reads that pair as "the target is the local player singleton",
        /// so on a host every arm would throw at the host's own player and at nobody else's.</para>
        /// <para>The list is handed over once and refilled in place from then on: the agent keeps the reference
        /// and prunes it as it reads, so each arm gets its own and nothing allocates per frame. The creature's own
        /// range still applies — this says who, never how far.</para>
        /// </remarks>
        private void AimWithoutSight(Unit arm, List<Unit> quarry)
        {
            try
            {
                var agent = (arm as Npc)?.AiAgent;
                if (agent == null)
                {
                    return;
                }

                agent.overridetargets.AddUnits(quarry, AiAgent.OverrideTarget.TargetType.Closest);
            }
            catch (Exception exception)
            {
                // An arm that has to see you before it throws is a worse fight, not a broken one.
                _logger?.LogWarning($"[arm] an arm could not be told who to throw at ({exception.Message}); "
                    + "it will only answer players it can see.");
            }
        }

        /// <summary>The creature's own rig root, or null when this prefab does not have one — in which case the
        /// arm is placed by its origin and nothing is corrected.</summary>
        private static Transform? FindArtRoot(Unit unit)
        {
            var found = unit.transform.Find(ArtRootName);
            return found != null ? found : null;
        }

        /// <summary>
        /// Where the arm holding <paramref name="station"/> should be seen standing: its offset in the boss's own
        /// frame, rotated by whichever way the boss is facing, so the arms stay at its sides as it turns.
        /// </summary>
        /// <remarks>
        /// Worked out from the placement every time it is asked for rather than once when the arm rose, which is
        /// what lets the distances be moved while the arms are standing. A boss facing nowhere — which is what a
        /// dead or newly spawned one reports — leaves the offset in world axes rather than collapsing both arms
        /// onto the boss.
        /// </remarks>
        private static Vector3 StandingPoint(ArmPlacement placement, int station)
        {
            var side = (station % 2 == 0) ? 1f : -1f;
            var rank = (station / 2) + 1;

            var forward = new Vector3(placement.BossFacing.X, 0f, placement.BossFacing.Z);
            var right = Vector3.right;
            if (forward.sqrMagnitude > 1e-4f)
            {
                forward = forward.normalized;
                right = Vector3.Cross(Vector3.up, forward);
            }
            else
            {
                forward = Vector3.forward;
            }

            return new Vector3(placement.BossAt.X, placement.BossAt.Y, placement.BossAt.Z)
                + right * (side * placement.SideDistance * rank)
                + Vector3.up * placement.Lift
                + forward * placement.ForwardOffset;
        }

        /// <summary>Drop the arms that are gone — taken down by us, or taken with a level — keeping the stations
        /// and art roots index-aligned with them.</summary>
        private void Forget()
        {
            for (var i = _raised.Count - 1; i >= 0; i--)
            {
                var arm = _raised[i];
                if (arm == null || !arm.IsAlive)
                {
                    _raised.RemoveAt(i);
                    _stations.RemoveAt(i);
                    _artRoots.RemoveAt(i);
                    _quarry.RemoveAt(i);
                }
            }
        }
    }
}
