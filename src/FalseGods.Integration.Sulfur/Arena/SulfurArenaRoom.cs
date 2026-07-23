// Heavy Unity / game-type interop (none of those APIs carry nullable annotations), so this file opts out of
// the nullable-reference context like the other game-facing implementations.
#nullable disable

using System;
using System.Collections.Generic;
using PerfectRandom.Sulfur.Core;
using PerfectRandom.Sulfur.Core.LevelGeneration;
using UnityEngine;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.Sulfur.Arena
{
    /// <summary>
    /// Dresses our realized arena as a <see cref="Room"/> — the chunk type SULFUR's level generation places,
    /// navigates, and spawns the player into. This is what lets the arena BE the level (Strategy A) instead of
    /// being overlaid onto one.
    /// </summary>
    /// <remarks>
    /// <para><b>The room is built inactive, on purpose.</b> <c>Room.Awake</c> reaches straight through its
    /// <c>roomLOD</c> reference to size a blood-decal occupancy grid. On a room that was authored in the editor
    /// that reference is serialized; on one we assemble at runtime it starts empty, so letting <c>Awake</c> run
    /// before the fields are set is a guaranteed null dereference. Adding a component to an inactive object defers
    /// its <c>Awake</c> to activation, which is why every field is set between the
    /// <see cref="GameObject.SetActive(bool)"/> pair rather than after it.</para>
    /// <para><b>Every baked list is emptied explicitly.</b> <c>Room.GetBakedList</c> hands its arrays back
    /// untouched, and the generation steps that keep running natively index into them without a null check —
    /// loot reads <c>containers</c>, finalisation reads <c>bezierArms</c>/<c>triggerSpawners</c>/
    /// <c>connectors</c>/<c>serviceStations</c>/<c>hiddenChests</c>, unit finalisation reads <c>NPCs</c>,
    /// navigation reads <c>nodeLinks</c>/<c>bakedNavMeshLinks</c>. Rather than bet on what a runtime-added
    /// component leaves them as, every one is set to an empty array.</para>
    /// <para><b>No navigation anchors, deliberately.</b> The game's navmesh cleaner marks every graph node
    /// <i>outside</i> the areas containing the level's connectors and room anchors as unwalkable — so a single
    /// badly placed anchor makes the whole arena unwalkable. With no anchors and no connectors the cleaner has
    /// nothing to check and leaves the graph alone, which is right here: our arena is the only geometry in the
    /// level, so there is nothing to clean away.</para>
    /// <para><b>Sealed, single room.</b> No connectors (nothing may attach to us) and no
    /// <c>NextLevelTrigger</c> yet — the exit belongs with the boss-death unlock, not with loading.</para>
    /// </remarks>
    public static class SulfurArenaRoom
    {
        /// <summary>
        /// Wrap <paramref name="arenaGeometry"/> in a configured <see cref="Room"/> whose player spawn sits at
        /// <paramref name="playerSpawn"/>. The geometry is reparented under the returned room.
        /// </summary>
        public static Room Build(GameObject arenaGeometry, Vector3 playerSpawn, ILogger logger = null)
        {
            if (arenaGeometry == null)
            {
                throw new ArgumentNullException(nameof(arenaGeometry));
            }

            var roomObject = new GameObject("FalseGodsArenaRoom");
            roomObject.SetActive(false);

            // Room.Awake dereferences roomLOD, so it exists before Room does. Its own lists are field-initialised,
            // so an added component is already in a usable state.
            var lod = roomObject.AddComponent<RoomLODBase>();
            var room = roomObject.AddComponent<Room>();

            room.roomLOD = lod;
            // The occupancy grid is sized from roomLOD bounds we do not have; blood decals do not need it.
            room.disableBloodOccupancyGrid = true;

            room.roomSize = RoomSize.Arena;
            room.doNotFlip = true;      // a mirrored boss arena is not the arena we authored
            room.doNotBarricade = true;
            room.uniquePerLevel = true;
            room.uniquePerRun = true;

            room.Structure = arenaGeometry;
            room.Decoration = new GameObject("Decoration");
            room.Decoration.transform.SetParent(roomObject.transform, worldPositionStays: false);

            EmptyEveryBakedList(room);

            arenaGeometry.transform.SetParent(roomObject.transform, worldPositionStays: true);

            var spawnObject = new GameObject("PlayerSpawn");
            spawnObject.transform.SetParent(roomObject.transform, worldPositionStays: false);
            spawnObject.transform.position = playerSpawn;
            // The empty identifier is what an ordinary level load asks for (GameManager.requestedSpawnIdentifier).
            spawnObject.AddComponent<PlayerSpawnPoint>().identifier = string.Empty;

            roomObject.SetActive(true);

            // Properties, not serialized fields — no Awake ordering to respect.
            room.partOfMainFlow = true;
            room.roomIndex = 0;
            room.disallowEnemySpawn = true;

            logger?.Log($"[arena-room] arena wrapped as a Room; player spawn at "
                + $"({playerSpawn.x:0.0}, {playerSpawn.y:0.0}, {playerSpawn.z:0.0}).");
            return room;
        }

        /// <summary>
        /// Give every baked list a real empty array. The generation steps index into these without null checks,
        /// and what a runtime-added component leaves them as is not something worth betting a level load on.
        /// </summary>
        private static void EmptyEveryBakedList(Room room)
        {
            room.containers = None(room.containers);
            room.connectors = None(room.connectors);
            room.NPCSpawns = None(room.NPCSpawns);
            room.NPCs = None(room.NPCs);
            room.Interactables = None(room.Interactables);
            room.pickups = None(room.pickups);
            room.eventSpawners = None(room.eventSpawners);
            room.bezierArms = None(room.bezierArms);
            room.hiddenChests = None(room.hiddenChests);
            room.randomizeXOnSprites = None(room.randomizeXOnSprites);
            room.randomChildSubsets = None(room.randomChildSubsets);
            room.randomChanceSelects = None(room.randomChanceSelects);
            room.randomlyDisables = None(room.randomlyDisables);
            room.randomizeDecals = None(room.randomizeDecals);
            room.serviceStations = None(room.serviceStations);
            room.triggerSpawners = None(room.triggerSpawners);
            room.endlessModeSpawnPoints = None(room.endlessModeSpawnPoints);
            room.nodeLinks = None(room.nodeLinks);
            room.bakedNPCSpawns = None(room.bakedNPCSpawns);
            room.bakedEventSpawners = None(room.bakedEventSpawners);
            room.bakedNavMeshLinks = None(room.bakedNavMeshLinks);
            room.navMeshAnchors = new List<Transform>();
        }

        /// <summary>An empty array of the field's own element type. The argument is read for its <i>static</i>
        /// type only — the field being null is precisely the case this exists to fix.</summary>
        private static T[] None<T>(T[] bakedList) => Array.Empty<T>();
    }
}
