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

        /// <summary>
        /// Where this borrows its look from: the game's own portal, in the safe area.
        /// </summary>
        /// <remarks>
        /// <para><b>Not the cave's own exit.</b> Every way in or out of a cave in this game is a shaft — a hole in
        /// the floor with embers rising out of it — so the cave has no standing doorway to copy. The church does,
        /// and it is the one every player has already walked through: a lit plate in the opening, a cone of light
        /// thrown out of it, and embers drifting around it. That is what reads as "through here".</para>
        /// <para><b>It already faces the right way.</b> The plate is seven across and five high with its thin axis
        /// along Z, which is how this doorway is oriented too, so nothing has to be turned to make it stand — and
        /// it is left at the size the game itself uses, which is a door built for a player to walk through.</para>
        /// <para>Its wooden gateposts are deliberately left behind. They are the church's carpentry and would read
        /// as somebody having built a door in a goblin cave.</para>
        /// </remarks>
        private const string DoorDonorKey =
            "Assets/_Core/Prefabs/LevelGeneration/Chunks/Hub/ChurchHub.prefab";

        private const string PortalPath = "LODing/9/HedgemazePortal/ON";

        /// <summary>
        /// The pieces of the portal, and where each sits relative to the lit plate.
        /// </summary>
        /// <remarks>
        /// <para>Offsets measured off the donor rather than read from its transforms, because a vanilla prop's
        /// origin is not where it is drawn — this plate's transform sits three metres away from the plate. What is
        /// carried across is each piece's offset from where the plate is <i>drawn</i>, so the composition survives
        /// the move.</para>
        /// <para>The emitters are the exception, and are placed by their transform: a particle system's bounds are
        /// whatever it happens to have thrown into the air at the moment you ask, which is no way to centre
        /// anything.</para>
        /// </remarks>
        private static readonly (string Path, Vector3 Offset, bool ByDrawnCentre)[] DoorPieces =
        {
            ("Shine", Vector3.zero, true),                                     // the lit plate: Sulfur, 7 x 5
            ("HedgeMazeShine", new Vector3(0.01f, 0.02f, 3.25f), true),        // the cone, thrown out of its face
            ("PortalParticleEffects", new Vector3(-0.34f, -0.47f, -1.25f), false),
        };

        /// <summary>
        /// How the borrowed portal is turned to stand in this doorway. Zero because the donor already faces along
        /// Z, as this doorway does. <b>If the light ends up shining into the wall, this is the number to change
        /// and nothing else.</b>
        /// </summary>
        private const float PortalTurnDegrees = 0f;

        /// <summary>How far the plate stands proud of the wall it is set into, so the two do not fight over the
        /// same pixels.</summary>
        private const float ProudOfTheWall = 0.1f;

        private readonly ILogger _logger;
        private readonly Action _walkThrough;

        private CousinHelper _watched;
        private Unit _boss;
        private GameObject _doorway;
        private AssetReference _donor;

        // Where the door will stand, worked out when the room is found rather than when the boss dies. Measured
        // once, at the start of the level, because that is when the hierarchy still says what it was authored to
        // say - see FindTheDoorway.
        private bool _doorwayKnown;
        private Vector3 _doorwayAt;
        private Quaternion _doorwayFacing;

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

            if (_boss != null || _donor != null || _doorwayKnown)
            {
                Forget(); // the level that held them is gone; let go before following another
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
            FindTheDoorway(helper, boss);
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
            _doorwayKnown = false;
            if (_doorway != null)
            {
                // Ours to destroy: it stands free in the level rather than under the room, so that nothing about
                // where it ends up depends on the boss still being parented where it was authored.
                UnityEngine.Object.Destroy(_doorway);
            }

            _doorway = null;
            Release();
        }

        /// <summary>
        /// Work out where the door will stand, while the boss is still alive.
        /// </summary>
        /// <remarks>
        /// <para><b>Now rather than at the death, because the answer stops being available.</b> The boss is a child
        /// of its room in the authored prefab, but by the time it dies it has been re-parented out from under it —
        /// measured, after asking for its room at the death and being told it had none. Asking at the start of the
        /// level gets the authored hierarchy, and the room does not move afterwards.</para>
        /// <para><b>The offset is safe against the generator turning the room around</b>: that room is marked
        /// <c>doNotFlip</c>, so the mirroring the generator does to other chunks never happens to this one and a
        /// left-hand wall stays the left-hand wall.</para>
        /// <para>Two ways of asking, because the first is the authored truth and the second is the game's own
        /// fallback for exactly this — which room is a thing standing here in. Whichever answered is logged, so a
        /// build where the hierarchy changes again says so instead of quietly putting a door nowhere.</para>
        /// </remarks>
        private void FindTheDoorway(CousinHelper helper, Unit boss)
        {
            var room = helper.GetComponentInParent<Room>();
            var how = "its own room in the hierarchy";
            if (room == null)
            {
                room = Room.FindClosestRoom(boss.transform.position, null, 10f);
                how = "the closest room to where it stands";
            }

            if (room == null)
            {
                _logger?.LogWarning("[cave-door] the cave boss is in this level but its room cannot be found, so "
                    + "no way through will open. The room's own exit still works.");
                return;
            }

            _doorwayAt = room.transform.TransformPoint(DoorwayPosition);
            _doorwayFacing = room.transform.rotation * Quaternion.Euler(DoorwayRotation);
            _doorwayKnown = true;
            _logger?.Log($"[cave-door] the cave boss is in this level ({how}); a way through will open at "
                + $"{_doorwayAt} when it falls.");
        }

        private void BossDied(Unit unit)
        {
            if (_doorway != null)
            {
                return;
            }

            if (!_doorwayKnown)
            {
                _logger?.LogWarning("[cave-door] the cave boss is dead, but there was nowhere to put a door; none "
                    + "opened.");
                return;
            }

            try
            {
                _doorway = new GameObject("FalseGodsCaveDoor");
                _doorway.transform.SetPositionAndRotation(_doorwayAt, _doorwayFacing);

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

                AddTheDoor(_doorway.transform);
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
        /// Build the doorway's look out of the church portal's pieces.
        /// </summary>
        /// <remarks>
        /// Borrowed rather than made: this is the door the game already uses, so a player who has walked through
        /// the one in the church knows what this is without being told. Failing to load it costs the look and not
        /// the way through — a door nobody notices is still a door, and better than none.
        /// </remarks>
        private void AddTheDoor(Transform doorway)
        {
            var donor = LoadDonor();
            if (donor == null)
            {
                return;
            }

            var portal = donor.transform.Find(PortalPath);
            if (portal == null)
            {
                _logger?.LogWarning($"[cave-door] the donor has no '{PortalPath}'; the way through is unlit.");
                return;
            }

            var plate = portal.Find(DoorPieces[0].Path);
            if (plate == null)
            {
                _logger?.LogWarning("[cave-door] the donor portal has no lit plate to measure the rest against; "
                    + "the way through is unlit.");
                return;
            }

            // Everything is placed against where the plate is DRAWN, which is not where its transform is.
            var origin = DrawnCentre(plate);
            var placed = 0;
            for (var i = 0; i < DoorPieces.Length; i++)
            {
                if (Clone(portal, DoorPieces[i], origin, doorway))
                {
                    placed++;
                }
            }

            _logger?.Log($"[cave-door] {placed} of {DoorPieces.Length} piece(s) of the church's portal now stand "
                + "in the doorway.");
        }

        private GameObject LoadDonor()
        {
            try
            {
                _donor = new AssetReference(DoorDonorKey);
                var handle = _donor.LoadAssetAsync<GameObject>();
                var donor = handle.WaitForCompletion();
                if (handle.Status == AsyncOperationStatus.Succeeded && donor != null)
                {
                    return donor;
                }

                _logger?.LogWarning($"[cave-door] the portal's donor did not load ({handle.Status}); the way "
                    + "through is there but unlit.");
            }
            catch (Exception exception)
            {
                _logger?.LogWarning($"[cave-door] the portal's donor did not load ({exception.Message}); the way "
                    + "through is there but unlit.");
            }

            Release();
            return null;
        }

        /// <summary>
        /// Copy one piece of the donor's portal into ours, keeping the turn it was authored with.
        /// </summary>
        /// <remarks>
        /// Staged inactive so nothing of the donor's gets a lifecycle, and stripped of colliders: this is a thing
        /// to walk through, and the plate the donor uses is solid. Each piece keeps its own authored rotation —
        /// the emitters are turned on their side in the donor and are not decoration if they are not.
        /// </remarks>
        private bool Clone(
            Transform portal, (string Path, Vector3 Offset, bool ByDrawnCentre) piece, Vector3 origin,
            Transform doorway)
        {
            var source = portal.Find(piece.Path);
            if (source == null)
            {
                _logger?.LogWarning($"[cave-door] the donor portal has no '{piece.Path}'; that piece is missing.");
                return false;
            }

            var staging = new GameObject("FalseGodsDoorStaging");
            staging.SetActive(false);
            try
            {
                var clone = UnityEngine.Object.Instantiate(source.gameObject, staging.transform);
                clone.name = source.name;
                foreach (var collider in clone.GetComponentsInChildren<Collider>(includeInactive: true))
                {
                    UnityEngine.Object.DestroyImmediate(collider);
                }

                // What the piece is drawn around, versus where its transform is - the correction that has to come
                // out before it can be placed by an offset.
                var drawn = piece.ByDrawnCentre ? DrawnCentre(source) : source.position;
                var toItsOwnOrigin = source.position - drawn;

                var turn = Quaternion.Euler(0f, PortalTurnDegrees, 0f);
                clone.transform.SetParent(doorway, worldPositionStays: false);
                clone.transform.localRotation = turn * source.localRotation;
                clone.transform.localScale = source.localScale;
                clone.transform.localPosition =
                    turn * (piece.Offset + toItsOwnOrigin) + new Vector3(0f, 0f, ProudOfTheWall);
                clone.SetActive(true);
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(staging);
            }
        }

        /// <summary>Where a piece is actually drawn, which for a vanilla prop is not where its transform is.</summary>
        private static Vector3 DrawnCentre(Transform piece)
        {
            var renderers = piece.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers.Length == 0)
            {
                return piece.position;
            }

            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            return bounds.center;
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
