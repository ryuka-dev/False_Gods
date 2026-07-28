using System;
using System.Collections.Generic;
using FalseGods.Application.Combat;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
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
    /// (<c>SpawnUnit</c> activates it), and its only condition is a target within the creature's own ranged attack
    /// range (30 m, line of sight). So the whole of this class is <i>where</i>, and <i>how big</i>.</para>
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

        // Where the arms were last told to be, so one that finishes loading after the others can be put in its
        // place immediately rather than standing at its spawn point until the next frame.
        private ArmPlacement _placement;

        // Which raising the arms in flight belong to. An arm is loaded asynchronously, so a rage that ends while
        // one is still on its way would otherwise leave it standing and throwing after the boss had calmed: the
        // arrival checks that it is still wanted before it is kept.
        private int _generation;

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
            }
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

            var artRoot = FindArtRoot(unit);
            _raised.Add(unit);
            _stations.Add(station);
            _artRoots.Add(artRoot);

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
                }
            }
        }
    }
}
