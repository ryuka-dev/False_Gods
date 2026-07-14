using System;
using BepInEx;
using BepInEx.Configuration;
using FalseGods.Plugin.Diagnostics;
using FalseGods.UnityRuntime.Presentation;
using UnityEngine.InputSystem;

namespace FalseGods.Plugin
{
    /// <summary>
    /// The False Gods base plugin — the BepInEx entry point and Composition Root for the single-player boss.
    /// </summary>
    /// <remarks>
    /// This is the only module that constructs concrete implementations and wires them through ports
    /// (Docs/Architecture.md §4). Today it builds the <b>single-player composition</b>: a Core
    /// <see cref="SinglePlayerBossController"/> stack with no multiplayer integration registered, so replication is
    /// absent and the boss runs on the same <c>BossSimulation</c> rules a host would use. It holds no CLR dependency
    /// on <c>FalseGods.Integration.SulfurTogether</c> (FG-ARCH-002) — the optional ST adapter arrives later as a
    /// separate companion plugin that self-registers through the <c>FalseGodsIntegrations</c> broker; none of that is
    /// wired here yet.
    ///
    /// <para>
    /// <see cref="PluginGuid"/> is stable because the future ST adapter declares a <c>[BepInDependency]</c> on it.
    /// The raise/damage keys are a development affordance for exercising the boss in-game; the damage key in
    /// particular drives a temporary dev harness (see <see cref="SinglePlayerBossController.Damage"/>), not shipping
    /// gameplay.
    /// </para>
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class FalseGodsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka_labs.falsegods";
        public const string PluginName = "False Gods";
        public const string PluginVersion = "0.1.0";

        // Initialised in Awake (Unity's lifecycle entry point, not the constructor); null! documents that contract.
        private ConfigEntry<Key> _raiseKey = null!;
        private ConfigEntry<Key> _damageKey = null!;
        private ConfigEntry<BossFacingMode> _facingMode = null!;
        private ConfigEntry<bool> _lockPitch = null!;

        private SinglePlayerBossController _boss = null!;

        private void Awake()
        {
            _raiseKey = Config.Bind("Boss", "RaiseKey", Key.B,
                "Raise the single-player test boss in front of you, or tear it down if it is already up. "
                + "Stand in a loaded level first. The game uses the new Input System.");

            _damageKey = Config.Bind("Boss", "DamageKey", Key.N,
                "Deal damage to the boss where the screen centre is aimed (a development harness, not real weapon "
                + "damage): hit it during the weak-point window for amplified damage, drop it to half for phase two, "
                + "to zero for death. Damage goes to the authoritative BossSimulation.");

            _facingMode = Config.Bind("Boss", "FacingMode", BossFacingMode.LocalBillboard,
                "Sprite facing: Fixed = a static/scripted world facing (for a very large boss); LocalBillboard = "
                + "face the local player's camera position (the vanilla NPC default; honours LockPitch); "
                + "NearestPlayer = face the authoritative nearest-player direction, shared by every viewer.");

            _lockPitch = Config.Bind("Boss", "LockPitch", false,
                "In LocalBillboard facing, false = yaw + natural elevation pitch toward the camera (vanilla), "
                + "true = yaw only (upright). Ignored by the Fixed and NearestPlayer modes.");

            _boss = new SinglePlayerBossController(new BepInExLogger(Logger));

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded (single-player). "
                + $"Raise/drop the boss: {_raiseKey.Value}. Damage (dev): {_damageKey.Value}. "
                + $"Facing: {_facingMode.Value}, lockPitch: {_lockPitch.Value}.");
        }

        private void Update()
        {
            if (KeyPressed(_raiseKey.Value))
            {
                if (_boss.IsUp)
                {
                    _boss.Drop();
                }
                else
                {
                    _boss.Raise();
                }
            }
            else if (_boss.IsUp && KeyPressed(_damageKey.Value))
            {
                _boss.Damage();
            }

            if (_boss.IsUp)
            {
                _boss.SetFacing(_facingMode.Value, _lockPitch.Value);
                _boss.Tick(UnityEngine.Time.deltaTime);
            }
        }

        private void OnDestroy()
        {
            // Never leave the boss behind if the plugin unloads while it is up.
            if (_boss != null && _boss.IsUp)
            {
                _boss.Drop();
            }
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
