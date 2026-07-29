// Addressables / Unity interop (none of those APIs carry nullable annotations), so this file opts out of the
// nullable-reference context like the other game-facing implementations.
#nullable disable

using System;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using PerfectRandom.Sulfur.Core.Units;
using PerfectRandom.Sulfur.Gameplay;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// The way in: a lit doorway that opens in the vanilla cave boss's own room once that boss is dead.
    /// </summary>
    /// <remarks>
    /// <para><b>Where it belongs in the game.</b> Beating the cave boss already ends with a choice of one — ride
    /// the elevator out. This adds a second door beside it rather than replacing anything, so a player who wants
    /// nothing to do with this can leave exactly as they always did, and a player who does gets somewhere to go
    /// at the moment they have just proved they can fight.</para>
    /// <para><b>Hung on the boss's own death.</b> Not on the checkpoint the game writes, not on a name, and not on
    /// noticing the room has gone quiet: the vanilla helper hooks the creature's <c>onDeath</c> and so does this,
    /// which means the door opens on the same event that ends the fight.</para>
    /// <para><b>The room is found by the boss, not the boss by the room.</b> A generated level names nothing
    /// reliably, but exactly one room in it carries a <c>CousinHelper</c>, and that helper sits on the boss. So
    /// finding the helper answers both questions at once and neither answer is a guess.</para>
    /// <para><b>Each peer opens its own.</b> The door is a local trigger, and walking into it asks to go — which
    /// is already a request either machine may make, with the host declaring and leading. Nothing here is
    /// authoritative and nothing is sent.</para>
    /// </remarks>
    public sealed class SulfurCaveBossPortal
    {
        /// <summary>
        /// Where the door stands in the boss room's own coordinates, and how big it is.
        /// </summary>
        /// <remarks>
        /// Measured off the room in an editor rather than guessed from the geometry: a flat panel on the wall left
        /// of the arena, turned a little to sit flush with it. Room-local, so it survives wherever the generator
        /// happens to place that room and however it is turned.
        /// </remarks>
        private static readonly Vector3 DoorwayPosition = new Vector3(-8.15f, 3.06f, 16.17f);

        private static readonly Vector3 DoorwayRotation = new Vector3(0f, 348.53f, 0f);

        private static readonly Vector3 DoorwaySize = new Vector3(5.73f, 6.35f, 1f);

        /// <summary>The room whose pit this borrows its embers from — the same donor the arena's own way out is.
        /// </summary>
        private const string EmberDonorKey =
            "Assets/_Core/Prefabs/LevelGeneration/Chunks/Caves/CaveEndRoom1.prefab";

        private const string EmberPath = "CaveEndParticles";

        /// <summary>How the embers are turned once they stand in the doorway. The donor's own pit points them
        /// straight up out of the floor, which is what a hole in the ground wants and not what a door does.
        /// </summary>
        private static readonly Vector3 EmberRotation = new Vector3(270f, 0f, 0f);

        private readonly ILogger _logger;
        private readonly Action _walkThrough;

        private CousinHelper _watched;
        private Unit _boss;
        private GameObject _doorway;
        private AssetReference _donor;

        /// <param name="walkThrough">Called when a player walks into the door. What that <i>does</i> is not this
        /// class's business — it says a player asked to go, and whoever owns the session decides the rest.</param>
        public SulfurCaveBossPortal(Action walkThrough, ILogger logger = null)
        {
            _walkThrough = walkThrough ?? throw new ArgumentNullException(nameof(walkThrough));
            _logger = logger;
        }

        /// <summary>
        /// Keep up with the level: find the boss while it lives, and forget everything when it and its room are
        /// gone. Cheap enough to call every frame — it is a field read once the boss has been found, and a single
        /// scene query at most once per level before that.
        /// </summary>
        public void Watch()
        {
            // A destroyed object compares equal to null through Unity's operator, so this is also how leaving the
            // level is noticed: the helper goes with its room, this stops being true, and the next cave with that
            // boss in it is found from scratch. The corpse lingers after the fight, which is why a door already
            // opened does not send this looking again.
            if (_watched != null)
            {
                return;
            }

            if (_boss != null || _donor != null)
            {
                Release(); // the level that held them is gone; let go before following another
                _boss = null;
                _doorway = null;
            }

            CousinHelper helper;
            try { helper = UnityEngine.Object.FindAnyObjectByType<CousinHelper>(FindObjectsInactive.Exclude); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[cave-door] could not look for the cave boss ({exception.Message}).");
                return;
            }

            if (helper == null)
            {
                return; // not a level with that boss in it
            }

            var boss = helper.GetComponent<Unit>();
            if (boss == null)
            {
                _logger?.LogWarning("[cave-door] the cave boss's helper is not on a unit; no door will open.");
                return;
            }

            _watched = helper;
            _boss = boss;
            boss.onDeath = (Unit.OnDeath)Delegate.Combine(boss.onDeath, new Unit.OnDeath(BossDied));
            _logger?.Log("[cave-door] the cave boss is in this level; a way through will open where it falls.");
        }

        /// <summary>Drop everything held for a level that is going away. Safe to call twice.</summary>
        public void Forget()
        {
            if (_boss != null)
            {
                _boss.onDeath = (Unit.OnDeath)Delegate.Remove(_boss.onDeath, new Unit.OnDeath(BossDied));
            }

            _boss = null;
            _watched = null;
            _doorway = null; // owned by the room, which the level destroys with everything else in it
            Release();
        }

        private void BossDied(Unit unit)
        {
            if (_doorway != null)
            {
                return;
            }

            var room = _watched != null ? _watched.GetComponentInParent<Room>() : null;
            if (room == null)
            {
                _logger?.LogWarning("[cave-door] the cave boss is not inside a room, so there is nowhere to put a "
                    + "door; none opened.");
                return;
            }

            try
            {
                _doorway = new GameObject("FalseGodsCaveDoor");
                _doorway.transform.SetParent(room.transform, worldPositionStays: false);
                _doorway.transform.localPosition = DoorwayPosition;
                _doorway.transform.localRotation = Quaternion.Euler(DoorwayRotation);

                var volume = _doorway.AddComponent<BoxCollider>();
                volume.isTrigger = true;
                volume.size = DoorwaySize;

                // The layer the game puts its own level-change triggers on, asked of the game rather than named,
                // because a layer index is not stable across builds.
                var manager = StaticInstance<GameManager>.Instance;
                if (manager != null)
                {
                    _doorway.layer = manager.TriggerLayer;
                }

                AddEmbers(_doorway.transform);
                _doorway.AddComponent<CaveDoorTrigger>().WalkedThrough = _walkThrough;

                _logger?.Log($"[cave-door] the cave boss is dead; a way through has opened in its room at "
                    + $"{_doorway.transform.position}.");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[cave-door] the door could not be opened ({exception.Message}); the room's "
                    + "own way out still works.");
            }
        }

        /// <summary>
        /// Put the game's own pit embers in the doorway.
        /// </summary>
        /// <remarks>
        /// Borrowed rather than made: this is the light the game already uses to say "through here", so a player
        /// who has seen one cave exit knows what this is without being told. Failing to load it costs the look and
        /// not the door — a way through that nobody notices is still a way through, and it is better than none.
        /// </remarks>
        private void AddEmbers(Transform doorway)
        {
            GameObject donor;
            try
            {
                _donor = new AssetReference(EmberDonorKey);
                var handle = _donor.LoadAssetAsync<GameObject>();
                donor = handle.WaitForCompletion();
                if (handle.Status != AsyncOperationStatus.Succeeded || donor == null)
                {
                    _logger?.LogWarning($"[cave-door] the embers' donor room did not load ({handle.Status}); the "
                        + "door is there but unlit.");
                    Release();
                    return;
                }
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[cave-door] the embers' donor room did not load ({exception.Message}); the "
                    + "door is there but unlit.");
                Release();
                return;
            }

            var source = donor.transform.Find(EmberPath);
            if (source == null)
            {
                _logger?.LogWarning($"[cave-door] the donor room has no '{EmberPath}'; the door is there but "
                    + "unlit.");
                return;
            }

            var embers = UnityEngine.Object.Instantiate(source.gameObject, doorway);
            embers.name = "Embers";
            embers.transform.localPosition = Vector3.zero;
            embers.transform.localRotation = Quaternion.Euler(EmberRotation);
            embers.transform.localScale = source.localScale;
        }

        private void Release()
        {
            if (_donor == null)
            {
                return;
            }

            try { _donor.ReleaseAsset(); }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[cave-door] the embers' donor room would not release ({exception.Message}).");
            }

            _donor = null;
        }

        /// <summary>
        /// The door itself: the local player walking in, once.
        /// </summary>
        /// <remarks>
        /// A collider rather than a distance test, unlike the arena's own start trigger — and for the opposite
        /// reason. That one has to answer for the whole session, so it is asked of the host, which has everyone's
        /// position. This one is a door each player opens for themselves: it is the game's own idiom for a level
        /// change (<c>NextLevelTrigger</c> does exactly this), and what happens next already knows how to take a
        /// session somewhere whichever machine asked.
        /// </remarks>
        private sealed class CaveDoorTrigger : MonoBehaviour
        {
            private GameObject _player;
            private bool _used;

            public Action WalkedThrough { get; set; }

            private void OnTriggerEnter(Collider other)
            {
                if (_used || other == null)
                {
                    return;
                }

                if (_player == null)
                {
                    var manager = StaticInstance<GameManager>.Instance;
                    _player = manager != null ? manager.PlayerObject : null;
                }

                if (_player == null || other.gameObject != _player)
                {
                    return;
                }

                _used = true;
                WalkedThrough?.Invoke();
            }
        }
    }
}
