using System;
using FalseGods.Application.Combat;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Stats;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// Binds the boss's physical collision body into the game's real weapon-damage pipeline, via the game's own
    /// public extension point — <b>no Harmony patch</b>.
    /// </summary>
    /// <remarks>
    /// How the pipeline reaches us (verified against Decompiled/ 2026-07-19): the projectile system resolves hits
    /// against its registered NPC hitboxes by collider id; a hit on any other collider is an "unhandled hit", and
    /// the main-thread pass does <c>collider.GetComponent&lt;IAttackReceiver&gt;()</c> and calls
    /// <c>ReceiveAttack</c> per damage component with the final computed per-hit damage
    /// (<c>ProjectileSystem.ProcessProjectileHits</c> → <c>ProjectileUtilities.ProcessReceiverHit</c>). Melee
    /// (<c>MeleeCollider</c>) and the railgun query the same interface, so those hit the boss too. Explosions do
    /// not consult <c>IAttackReceiver</c> — a known limitation until a boss needs it.
    ///
    /// <para>
    /// The receiver must sit on the <b>same GameObject as the struck collider</b> (the lookup is
    /// <c>GetComponent</c>, not <c>GetComponentInParent</c>) — the composition passes the solid capsule's body.
    /// The projectile raycast only sees layers in <c>ProjectileSystem.collisionLayers</c>; whether the boss's
    /// layer is included is logged at bind time so a silent "bullets pass through" has a diagnosis on record.
    /// </para>
    /// </remarks>
    public static class BossWeaponDamage
    {
        /// <summary>
        /// Attach the damage receiver to <paramref name="collisionBody"/>, delivering hits to
        /// <paramref name="sink"/>. Dispose the returned binding on encounter teardown.
        /// </summary>
        public static IDisposable Bind(GameObject collisionBody, IBossDamageSink sink, ILogger? logger)
        {
            if (collisionBody == null)
            {
                throw new ArgumentNullException(nameof(collisionBody));
            }

            if (sink is null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            var receiver = collisionBody.AddComponent<BossAttackReceiver>();
            receiver.Initialize(sink);
            LogProjectileLayerDiagnosis(collisionBody, logger);
            return new Binding(receiver);
        }

        /// <summary>
        /// One-time diagnostic: is the boss's layer actually in the projectile raycast mask? Diagnostics only —
        /// nothing behavioural hangs off this (Docs/DependencyRules.md; logging never gates functionality).
        /// </summary>
        private static void LogProjectileLayerDiagnosis(GameObject collisionBody, ILogger? logger)
        {
            try
            {
                var projectiles = StaticInstance<ProjectileSystem>.Instance;
                if (projectiles == null)
                {
                    logger?.LogWarning("ProjectileSystem is not up yet; cannot verify the boss layer is in its collision mask.");
                    return;
                }

                int mask = projectiles.collisionLayers.value;
                var layerName = LayerMask.LayerToName(collisionBody.layer);
                var included = (mask & (1 << collisionBody.layer)) != 0;
                logger?.Log($"weapon-damage bound: projectile mask=0x{mask:X8}, boss layer '{layerName}' included={included}.");
                if (!included)
                {
                    logger?.LogWarning($"Projectiles do not raycast layer '{layerName}' - bullets will pass through the boss.");
                }
            }
            catch (Exception ex)
            {
                logger?.LogWarning($"Projectile layer diagnosis failed: {ex.Message}");
            }
        }

        private sealed class Binding : IDisposable
        {
            private BossAttackReceiver? _receiver;

            public Binding(BossAttackReceiver receiver) => _receiver = receiver;

            public void Dispose()
            {
                if (_receiver != null)
                {
                    UnityEngine.Object.Destroy(_receiver);
                }

                _receiver = null;
            }
        }
    }

    /// <summary>
    /// The <see cref="IAttackReceiver"/> the game's weapon systems find on the boss's collision body. It decides
    /// nothing: every hit's final damage number is forwarded to the project-owned sink, and the authoritative
    /// simulation applies its own rules (weak-point window, phase, death).
    /// </summary>
    internal sealed class BossAttackReceiver : MonoBehaviour, IAttackReceiver
    {
        private IBossDamageSink? _sink;

        public void Initialize(IBossDamageSink sink) => _sink = sink;

        public bool ReceiveAttack(float damage, DamageTypes damageType, DamageSourceData sourceData, Hitmesh.Data hitbox, Vector3? collisionPoint = null)
        {
            if (_sink is null || float.IsNaN(damage) || damage <= 0f)
            {
                return false;
            }

            _sink.ApplyWeaponDamage(damage);
            return true;
        }
    }
}
