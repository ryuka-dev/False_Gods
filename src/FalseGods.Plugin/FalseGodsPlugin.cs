using System;
using BepInEx;
using BepInEx.Configuration;
using FalseGods.Application.Replication;
using FalseGods.Core.Simulation;
using FalseGods.Plugin.Diagnostics;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine.InputSystem;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The False Gods base plugin — the BepInEx entry point and Composition Root.
    /// </summary>
    /// <remarks>
    /// This is the only module that constructs concrete implementations and wires them through ports, and the
    /// <b>only permitted reader</b> of the <see cref="FalseGodsIntegrations"/> broker (Docs/Architecture.md §4,
    /// FG-ARCH-005). It holds no CLR dependency on <c>FalseGods.Integration.SulfurTogether</c> (FG-ARCH-002): the
    /// optional ST adapter is a separate companion plugin that self-registers through the broker after this
    /// plugin's <c>Awake</c> has subscribed (its hard <c>[BepInDependency]</c> on <see cref="PluginGuid"/> pins
    /// that order).
    ///
    /// <para>
    /// The three compositions (Architecture §4.3), re-evaluated every frame from the registered integration's
    /// live session state — a session starts and ends in-game, not at plugin load:
    /// <list type="bullet">
    /// <item><b>Single-player</b> (no integration, or no active session): the local
    /// <see cref="LocalEncounterController"/> stack; replication absent.</item>
    /// <item><b>Host</b>: the same controller with an <see cref="EncounterHostReplication"/> attached — the host
    /// adds replication, it does not swap boss implementations.</item>
    /// <item><b>Client</b>: a <see cref="ClientBossController"/> — presentation only, driven by the host's
    /// stream; the raise/damage keys are inert.</item>
    /// </list>
    /// When the adapter's registration token is disposed, the change event fires and the next frame falls back to
    /// the single-player composition (PoC B0).
    /// </para>
    ///
    /// <para>
    /// <see cref="PluginGuid"/> is stable because the ST adapter declares a <c>[BepInDependency]</c> on it. The
    /// raise key is a development affordance, not shipping gameplay; damage is the real weapon path (the game's
    /// projectile/melee systems hitting the boss's collision body). The arena identity, hash, and origin all come
    /// from the real load flow — a raise without valid arena content fails closed.
    /// </para>
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FalseGodsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka_labs.falsegods";
        public const string PluginName = "False Gods";
        public const string PluginVersion = "0.4.0";

        private const int TestBossDefinition = 1;

        // Initialised in Awake (Unity's lifecycle entry point, not the constructor); null! documents that contract.
        private ConfigEntry<Key> _raiseKey = null!;
        private ConfigEntry<BossFacingMode> _facingMode = null!;
        private ConfigEntry<bool> _lockPitch = null!;
        private ConfigEntry<float> _spriteScale = null!;
        private ConfigEntry<float> _maxClientHitDamage = null!;

        private BepInExLogger _log = null!;
        private LocalEncounterController _boss = null!;
        private ClientBossController? _client;
        private IFalseGodsIntegration? _clientIntegration; // the integration _client was composed on

        private int _nextEncounter = 1;
        private EncounterId _currentEncounter;

        private void Awake()
        {
            _raiseKey = Config.Bind("Boss", "RaiseKey", Key.B,
                "Raise the test boss in front of you, or tear it down if it is already up. "
                + "Stand in a loaded level first. On a multiplayer client the key is inert - the host drives the boss. "
                + "Damage the boss with your real weapons (bullets and melee); hits during the weak-point window "
                + "are amplified. The game uses the new Input System.");

            _facingMode = Config.Bind("Boss", "FacingMode", BossFacingMode.LocalBillboard,
                "Sprite facing: Fixed = a static/scripted world facing (for a very large boss); LocalBillboard = "
                + "face the local player's camera position (the vanilla NPC default; honours LockPitch); "
                + "NearestPlayer = face the authoritative nearest-player direction, shared by every viewer.");

            _lockPitch = Config.Bind("Boss", "LockPitch", false,
                "In LocalBillboard facing, false = yaw + natural elevation pitch toward the camera (vanilla), "
                + "true = yaw only (upright). Ignored by the Fixed and NearestPlayer modes.");

            // TEMPORARY dev affordance — like the whole "Boss" config section, this exists only to tune the boss
            // in-engine during development. A shipping build fixes the boss size in authored content; players must
            // not be able to change it. Remove before release (do not let it ossify into shipped config).
            _spriteScale = Config.Bind("Boss", "SpriteScale", 2.0f,
                "[DEV/TEMPORARY - removed before release] Uniform visual size of the boss (body sprite, hitboxes, "
                + "and health bar scale together, the way the vanilla boss is one scaled sprite). 1.0 is the base "
                + "size; the default is tuned so the boss reads at roughly the vanilla cave boss's size. Changeable "
                + "live; non-positive values are ignored.");

            _maxClientHitDamage = Config.Bind("Multiplayer", "MaxClientHitDamage",
                LocalEncounterController.DefaultMaxClientHitDamage,
                "Host only: the largest single hit a multiplayer client may report against the boss. A sanity "
                + "ceiling on a forged message, not a substitute for rate limiting - set it above any legitimate "
                + "single weapon hit. The host clamps to this; the simulation still decides weak-point, phase, and "
                + "death. Read once at load.");

            _log = new BepInExLogger(Logger);
            _boss = new LocalEncounterController(_log, _maxClientHitDamage.Value);

            // Subscribe before any adapter can load (their hard BepInDependency on this GUID guarantees the order),
            // so a registration always lands in an initialized seam. Composition changes are applied in Update, in
            // one place; the handler only reports the transition.
            FalseGodsIntegrations.Changed += OnIntegrationChanged;

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Raise/drop the boss: {_raiseKey.Value}; "
                + $"damage it with real weapons. Facing: {_facingMode.Value}, lockPitch: {_lockPitch.Value}. "
                + $"Multiplayer integration: {(FalseGodsIntegrations.Current != null ? "registered" : "none (single-player)")}.");
        }

        private void Update()
        {
            var integration = FalseGodsIntegrations.Current;
            var role = EvaluateRole(integration);

            if (role == CompositionRole.Client)
            {
                RunClientComposition(integration!);
                return;
            }

            TearDownClientComposition();
            RunLocalComposition(integration, role);
        }

        private void OnDestroy()
        {
            FalseGodsIntegrations.Changed -= OnIntegrationChanged;

            // Never leave a boss behind if the plugin unloads while one is up.
            if (_boss != null && _boss.IsActiveEncounter)
            {
                _boss.Drop();
            }

            TearDownClientComposition();
        }

        private enum CompositionRole
        {
            SinglePlayer,
            Host,
            Client,
        }

        private CompositionRole EvaluateRole(IFalseGodsIntegration? integration)
        {
            if (integration is null || !integration.Session.IsActive)
            {
                return CompositionRole.SinglePlayer;
            }

            return integration.Session.Role == RuntimeContracts.Multiplayer.SessionRole.Host
                ? CompositionRole.Host
                : CompositionRole.Client;
        }

        /// <summary>Single-player and host: the local simulation stack, with replication attached iff hosting.</summary>
        private void RunLocalComposition(IFalseGodsIntegration? integration, CompositionRole role)
        {
            if (KeyPressed(_raiseKey.Value))
            {
                if (_boss.IsActiveEncounter)
                {
                    _boss.Drop();
                }
                else
                {
                    // A host raise hands the controller the integration: the controller realizes locally, then
                    // gates the whole roster over the channel and starts (with replication attached) only when
                    // the gate resolves. A single-player raise gates the one local peer and starts immediately.
                    _currentEncounter = new EncounterId(_nextEncounter++);
                    _boss.Raise(_currentEncounter, role == CompositionRole.Host ? integration : null);
                }
            }

            // The session can start or end mid-encounter; keep the attached driver consistent with the live role.
            var wantReplication = role == CompositionRole.Host && _boss.IsUp;
            if (wantReplication && !_boss.HasReplication && _boss.CurrentManifest != null)
            {
                _boss.SetReplication(BuildHostReplication(integration!, _boss.CurrentManifest, _boss.CurrentOrigin));
            }
            else if (!wantReplication && _boss.HasReplication)
            {
                _boss.SetReplication(null);
            }

            _boss.SetFacing(_facingMode.Value, _lockPitch.Value);
            _boss.SetSpriteScale(_spriteScale.Value);
            _boss.Tick(UnityEngine.Time.deltaTime); // also drives a waiting host gate; a no-op when idle
        }

        private void RunClientComposition(IFalseGodsIntegration integration)
        {
            // A boss raised locally (as single-player or host) cannot survive a switch to the client role: the
            // host's stream is now the only authority.
            if (_boss.IsActiveEncounter)
            {
                _boss.Drop();
            }

            if (_client != null && !ReferenceEquals(_clientIntegration, integration))
            {
                TearDownClientComposition(); // a different integration registered; recompose on its channel
            }

            if (_client is null)
            {
                _client = new ClientBossController(_log, integration);
                _clientIntegration = integration;
            }

            if (KeyPressed(_raiseKey.Value))
            {
                Logger.LogMessage("Multiplayer client: the host drives the boss; the raise key is inert here.");
            }

            _client.SetFacing(_facingMode.Value, _lockPitch.Value);
            _client.SetSpriteScale(_spriteScale.Value);
            _client.Tick(UnityEngine.Time.deltaTime);
        }

        private void TearDownClientComposition()
        {
            _client?.Dispose();
            _client = null;
            _clientIntegration = null;
        }

        private EncounterHostReplication BuildHostReplication(
            IFalseGodsIntegration integration,
            Protocol.Arena.ArenaManifest manifest,
            Protocol.Wire.WorldPosition arenaOrigin) =>
            new EncounterHostReplication(
                new ReplicationSender(integration.Channel, integration.Session),
                integration.Session,
                integration.Roster,
                _currentEncounter,
                new DefinitionId(TestBossDefinition),
                manifest,
                arenaOrigin);

        private void OnIntegrationChanged()
        {
            Logger.LogMessage(FalseGodsIntegrations.Current != null
                ? "Multiplayer integration registered; the host/client composition activates with the session."
                : "Multiplayer integration revoked; returning to the single-player composition.");
        }

        private static bool KeyPressed(Key key)
        {
            try
            {
                var keyboard = Keyboard.current;
                return keyboard != null && keyboard[key].wasPressedThisFrame;
            }
            catch (Exception)
            {
                // No keyboard device, or an unmapped key.
                return false;
            }
        }
    }
}
