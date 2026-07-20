using System;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Configuration;
using FalseGods.RuntimeContracts.Integration;
using UnityEngine.InputSystem;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.SulfurTogether
{
    /// <summary>
    /// The optional SULFUR Together adapter — a companion BepInEx plugin that maps the RuntimeContracts
    /// multiplayer ports onto ST's public bridge and self-registers through the <see cref="FalseGodsIntegrations"/>
    /// broker (ADR-004, Architecture §4.1).
    /// </summary>
    /// <remarks>
    /// Both <c>[BepInDependency]</c> attributes are hard and by GUID string, never a CLR reference: the base
    /// plugin's, so its <c>Awake</c> has subscribed to the broker before this one runs (nothing polls, nothing
    /// races), and ST's, because this adapter is meaningless without it — with either absent, BepInEx simply skips
    /// this plugin and the base game plays single-player.
    ///
    /// <para>
    /// A matching ST <i>GUID</i> does not guarantee the bridge <i>API</i>: an older ST build has the GUID but no
    /// <c>SULFURTogether.Api</c>. Every first bridge touch therefore happens inside <see cref="TryCompose"/>'s
    /// guarded, non-inlined calls, and a missing surface degrades to a logged "multiplayer unavailable" — never a
    /// type-load failure (Docs/DependencyRules.md §6, RiskList R20/R29). The same discipline keeps ST types out of
    /// every field in this assembly (FG-ARCH-011; see <see cref="StEncounterChannel"/>'s remarks).
    /// </para>
    ///
    /// <para>
    /// The two dev keys are a TEMPORARY harness for PoC B0's seam checks (register-twice rejected, revoke falls
    /// back to single-player); they drive only the broker, never gameplay.
    /// </para>
    /// </remarks>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(BasePluginGuid)]
    [BepInDependency(SulfurTogetherGuid)]
    public sealed class SulfurTogetherPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "ryuka.sulfur.false_gods_sulfur_together";
        public const string PluginName = "False Gods - SULFUR Together Adapter";
        public const string PluginVersion = "0.1.0";

        /// <summary>The False Gods base plugin (FalseGodsPlugin.PluginGuid — a string on purpose, FG-ARCH-002).</summary>
        private const string BasePluginGuid = "ryuka_labs.falsegods";

        /// <summary>SULFUR Together's plugin GUID (verified against the ST source's ModInfo).</summary>
        private const string SulfurTogetherGuid = "com.ryuka.sulfur.together";

        // Initialised in Awake (Unity's lifecycle entry point, not the constructor); null! documents that contract.
        private ConfigEntry<Key> _registerKey = null!;
        private ConfigEntry<Key> _revokeKey = null!;

        private ILogger _log = null!;
        private StEncounterChannel? _channel;
        private SulfurTogetherIntegration? _integration;
        private IIntegrationRegistration? _token;

        private void Awake()
        {
            _registerKey = Config.Bind("Dev", "RegisterKey", Key.F10,
                "TEMPORARY B0 harness: attempt a broker registration. With ours already live this demonstrates "
                + "the duplicate being rejected (first registration wins); after a revoke it re-registers.");

            _revokeKey = Config.Bind("Dev", "RevokeKey", Key.F11,
                "TEMPORARY B0 harness: dispose the registration token. The base plugin must fall back to the "
                + "single-player composition until a registration returns.");

            _log = new Diagnostics.BepInExLogger(Logger);

            Logger.LogMessage($"{PluginName} {PluginVersion} loaded. Dev keys: register {_registerKey.Value}, "
                + $"revoke {_revokeKey.Value}.");

            TryCompose("plugin load");
        }

        private void Update()
        {
            if (KeyPressed(_registerKey.Value))
            {
                TryCompose("dev key");
            }
            else if (KeyPressed(_revokeKey.Value))
            {
                Revoke();
            }
        }

        private void OnDestroy()
        {
            Revoke();
            _channel?.Dispose();
            _channel = null;
        }

        /// <summary>
        /// Build the bridge-backed composition once, then offer it to the broker. Safe to call repeatedly: with our
        /// registration live, the broker rejects the duplicate and the first registration stays authoritative.
        /// </summary>
        private void TryCompose(string trigger)
        {
            if (!EnsureComposed())
            {
                return;
            }

            var token = FalseGodsIntegrations.Register(_integration!);
            if (token != null)
            {
                _token = token;
                _log.Log($"Multiplayer integration registered with the base plugin ({trigger}).");
            }
            else if (_token != null)
            {
                _log.Log($"Broker rejected the duplicate registration ({trigger}); the first registration stays authoritative.");
            }
            else
            {
                _log.LogWarning($"Multiplayer integration already provided by someone else ({trigger}); staying inert.");
            }
        }

        private void Revoke()
        {
            if (_token is null)
            {
                return;
            }

            _token.Dispose();
            _token = null;
            _log.Log("Registration token disposed; the base plugin falls back to the single-player composition.");
        }

        /// <summary>Construct the adapters and register the bridge channel, once. False = stay inert.</summary>
        private bool EnsureComposed()
        {
            if (_integration != null)
            {
                return true;
            }

            // Everything that JITs against the bridge stays inside this guard: DescribeBridge is the deliberate
            // first touch, and TryRegister is the first real call — either can fail on an ST build whose GUID
            // matches but whose Api surface is missing or drifted.
            try
            {
                var bridgeVersion = DescribeBridge();
                var peers = new StPeerDirectory();
                var channel = new StEncounterChannel(peers, _log);
                if (!channel.TryRegister())
                {
                    _log.LogWarning("Encounter channel registration refused; multiplayer unavailable.");
                    return false;
                }

                _channel = channel;
                _integration = new SulfurTogetherIntegration(
                    new StMultiplayerSession(peers), channel, new StPlayerRoster(peers), new StParticipantPeerMap(peers));
                _log.Log($"ST adapter composed over {bridgeVersion}.");
                return true;
            }
            catch (Exception ex) when (
                ex is TypeLoadException || ex is MissingMethodException || ex is MissingFieldException ||
                ex is TypeInitializationException || ex is BadImageFormatException)
            {
                _log.LogWarning("The installed SULFUR Together does not carry the expected public bridge "
                    + $"(SULFURTogether.Api). Multiplayer unavailable; single-player is unaffected. "
                    + $"({ex.GetType().Name}: {ex.Message})");
                return false;
            }
        }

        /// <summary>
        /// The first bridge touch. Kept out of <see cref="Awake"/>/<see cref="EnsureComposed"/> and never inlined,
        /// so that on a bridge-less ST the JIT failure surfaces here — inside the caller's catch — instead of
        /// aborting the calling method itself.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string DescribeBridge() =>
            $"ST bridge API v{SULFURTogether.Api.NetExternalChannel.ApiVersion}";

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
