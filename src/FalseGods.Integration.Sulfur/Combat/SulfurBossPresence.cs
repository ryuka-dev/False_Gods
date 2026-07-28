using System;
using System.Reflection;
using FalseGods.Application.Combat;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.Units;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Combat
{
    /// <summary>
    /// The SULFUR implementation of <see cref="IBossPresencePort"/>: the boss is put on the list the game's weapon
    /// systems read, without being handed to the game to run.
    /// </summary>
    /// <remarks>
    /// <para><b>Which list, and why there is no smaller seam.</b> Measured on v0.18.5: aim assist and weapon homing
    /// both walk <c>GameManager.aliveNpcs</c>, and the homing one is typed to return an <c>Npc</c> and reads a
    /// <c>Transform</c> off it to fly towards. So the boss has to carry a real <c>Npc</c> — nothing less is
    /// visible to either.</para>
    /// <para><b>How it stays a listing and not a life.</b> A creature only starts being run by the game when
    /// <c>Unit.Spawn()</c> is called: that is what raises <c>hasBeenSpawned</c>, and both <c>Npc.Update</c> and
    /// <c>Unit.UpdateUnit</c> return immediately while it is false. We never call it. The component is therefore
    /// inert — it does not path, animate, regenerate, drop anything or die — and it is the game's own switch, not
    /// a trick. It also keeps the session layer out of this: a session mirrors what it sees spawn, and nothing
    /// spawned.</para>
    /// <para><b>Listed, but not adopted.</b> Registration goes straight into <c>aliveNpcs</c> rather than through
    /// <c>GameManager.AddNpc</c>, which would also put the boss in the level's own <c>npcs</c> list and rebuild
    /// the line-of-sight and projectile tables around it. That list is walked by the per-frame billboard pass,
    /// which is the one thing that would actually fight our renderer.</para>
    /// <para><b>It borrows a definition rather than inventing one.</b> Several of the game's readers reach through
    /// <c>unitSO</c> without checking it, and one is dereferenced on the first frame the component exists, so it
    /// cannot be left empty. The cave boss's own definition is the honest thing to hand them: our boss is an
    /// upgraded one of those, and it is already loaded because the roar comes off the same creature.</para>
    /// <para><b>Every peer for itself</b>, and nothing here is replicated: aim assist runs on the machine doing
    /// the aiming.</para>
    /// </remarks>
    public sealed class SulfurBossPresence : IBossPresencePort
    {
        /// <summary>Armour pieces a unit's own <c>Start</c> counts on the first frame, without checking whether
        /// there are any. A component added at runtime cannot be trusted to have been given the empty array the
        /// editor would have serialised — the same care a runtime-built <c>Room</c> needed for its baked lists.
        /// </summary>
        private const string ArmourFieldName = "armor";

        private readonly Func<Collider?> _solidBody;
        private readonly ILogger? _logger;

        private GameObject? _listing;
        private Npc? _npc;

        /// <param name="solidBody">The boss's own solid capsule — what the game is told to measure it by, and what
        /// the listing hangs from so it goes wherever the boss goes.</param>
        public SulfurBossPresence(Func<Collider?> solidBody, ILogger? logger = null)
        {
            _solidBody = solidBody ?? throw new ArgumentNullException(nameof(solidBody));
            _logger = logger;
        }

        public void Declare()
        {
            if (_npc != null)
            {
                return;
            }

            var body = _solidBody();
            if (body == null)
            {
                return; // no boss standing; nothing to declare
            }

            UnitSO definition;
            try
            {
                definition = UnitIds.GoblinCousin.GetAsset();
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[presence] the game would not give up a creature definition to borrow "
                    + $"({exception.Message}); homing weapons and aim assist will not see the boss.");
                return;
            }

            if (definition == null)
            {
                _logger?.LogWarning("[presence] the game has no cave-boss definition to borrow; homing weapons and "
                    + "aim assist will not see the boss.");
                return;
            }

            try
            {
                // Stands at the middle of the boss rather than at its feet, because this object is both what a
                // homing shot flies towards and where the game measures the boss from. Mapped through the collider
                // rather than read off its bounds, which the physics scene has not caught up with in the frame an
                // object is made (Docs/BossEncounterRunbook.md §3.13's neighbour).
                _listing = new GameObject("FalseGodsBossPresence");
                _listing.transform.SetParent(body.transform, worldPositionStays: false);
                _listing.transform.localPosition = MiddleOf(body);

                var npc = _listing.AddComponent<Npc>();
                npc.unitSO = definition;
                npc.mainCollider = body;      // what aim assist measures; the boss's own, not a second one
                npc.center = _listing.transform; // what a homing shot flies at
                EmptyTheArmour(npc);

                StaticInstance<GameManager>.Instance.aliveNpcs.Add(npc);
                _npc = npc;
                _logger?.Log("[presence] the boss is on the game's enemy list: homing weapons follow it and aim "
                    + "assist holds on it. It is listed only - the game does not run it.");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[presence] the boss could not be listed as an enemy ({exception.Message}); "
                    + "homing weapons and aim assist will not see it.");
                Withdraw();
            }
        }

        public void Withdraw()
        {
            var npc = _npc;
            _npc = null;
            if (npc != null)
            {
                try
                {
                    var gameManager = StaticInstance<GameManager>.Instance;
                    gameManager?.aliveNpcs.Remove(npc);
                }
                catch (Exception exception)
                {
                    _logger?.LogWarning($"[presence] the boss could not be taken off the enemy list "
                        + $"({exception.Message}).");
                }
            }

            if (_listing != null)
            {
                try { UnityEngine.Object.Destroy(_listing); } catch (Exception) { }
                _listing = null;
            }
        }

        /// <summary>The middle of the boss in the solid body's own local space — its collider's centre, mapped
        /// through the transform rather than read off bounds the physics scene has not settled.</summary>
        private static Vector3 MiddleOf(Collider body) =>
            body is CapsuleCollider capsule ? capsule.center
            : body is BoxCollider box ? box.center
            : Vector3.zero;

        /// <summary>
        /// Give the component the empty armour array the editor would have serialised for it.
        /// </summary>
        /// <remarks>
        /// A unit counts its armour on its first frame without asking whether it has any, and a component added at
        /// runtime is not the same thing as one authored in a prefab. Left to chance this is a null reference on
        /// frame one, which is a hard way to learn it.
        /// </remarks>
        private void EmptyTheArmour(Npc npc)
        {
            try
            {
                var field = typeof(Unit).GetField(
                    ArmourFieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var element = field?.FieldType.GetElementType();
                if (field != null && element != null && field.GetValue(npc) == null)
                {
                    field.SetValue(npc, Array.CreateInstance(element, 0));
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[presence] the listing's armour could not be emptied ({exception.Message}).");
            }
        }
    }
}
