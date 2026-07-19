using System;

namespace FalseGods.RuntimeContracts.Integration
{
    /// <summary>
    /// The single-slot registration broker for the optional multiplayer integration — the one seam through which
    /// a separately-loaded adapter reaches the Composition Root (Docs/Architecture.md §4.1, ADR-004).
    /// </summary>
    /// <remarks>
    /// This is a registration point, <b>not</b> a service locator: one slot holding at most one
    /// <see cref="IFalseGodsIntegration"/>, plus a change event. There is no <c>Resolve&lt;T&gt;()</c>, no
    /// type-keyed dictionary, and no way to ask it for an arbitrary service.
    ///
    /// <para>
    /// The rules that keep it from decaying into a global lookup table (ADR-004 names all five; relaxing any of
    /// them violates that ADR):
    /// <list type="bullet">
    /// <item><b>One reader.</b> <c>FalseGods.Plugin</c> is the only permitted reader of <see cref="Current"/> and
    /// <see cref="Changed"/>; every other type is constructor-injected (FG-ARCH-005). Unit tests inject
    /// <see cref="IFalseGodsIntegration"/> fakes and never touch this class.</item>
    /// <item><b>First registration wins.</b> A second <see cref="Register"/> while one is live is rejected —
    /// <c>null</c>, no throw, no replacement — so load order never silently decides which integration is
    /// authoritative. The rejected adapter logs and stays inert.</item>
    /// <item><b>Token-scoped revocation.</b> Only the returned token's holder can revoke; disposing it clears the
    /// slot and raises <see cref="Changed"/>. There is no <c>Clear()</c>.</item>
    /// <item><b>No second piece of static state.</b> This slot is the deliberate, bounded exception to "no global
    /// mutable state" (Docs/RiskList.md R26).</item>
    /// <item><b>No initialization order of its own.</b> Static state in a type with no BepInEx, Unity, Protocol,
    /// or ST dependency; the base plugin subscribes before the adapter loads (hard BepInEx dependency), so a
    /// registration always lands in an initialized seam.</item>
    /// </list>
    /// </para>
    /// </remarks>
    public static class FalseGodsIntegrations
    {
        private static readonly object Gate = new object();
        private static RegistrationToken? _active;

        /// <summary>
        /// The registered integration, or <c>null</c> when none is registered. Read by the Composition Root only.
        /// </summary>
        public static IFalseGodsIntegration? Current
        {
            get
            {
                lock (Gate)
                {
                    return _active?.Integration;
                }
            }
        }

        /// <summary>
        /// Raised after the slot changes — a registration was accepted or the active one was revoked. Read
        /// <see cref="Current"/> for the new state. Subscribed by the Composition Root only.
        /// </summary>
        public static event Action? Changed;

        /// <summary>
        /// Offer an integration. Returns the revocation token if the slot was empty and this registration is now
        /// live; returns <c>null</c> if an integration is already registered — the first registration stays
        /// authoritative and the caller should log "multiplayer integration already provided" and stay inert.
        /// </summary>
        public static IIntegrationRegistration? Register(IFalseGodsIntegration integration)
        {
            if (integration is null)
            {
                throw new ArgumentNullException(nameof(integration));
            }

            RegistrationToken token;
            lock (Gate)
            {
                if (_active != null)
                {
                    return null; // first registration wins; no throw, no replacement.
                }

                token = new RegistrationToken(integration);
                _active = token;
            }

            RaiseChanged();
            return token;
        }

        private static void Revoke(RegistrationToken token)
        {
            lock (Gate)
            {
                if (!ReferenceEquals(_active, token))
                {
                    return; // already revoked, or not the active registration — never clear someone else's.
                }

                _active = null;
            }

            RaiseChanged();
        }

        // Raised outside the lock so a handler reading Current (or even re-registering) cannot deadlock.
        private static void RaiseChanged() => Changed?.Invoke();

        private sealed class RegistrationToken : IIntegrationRegistration
        {
            public RegistrationToken(IFalseGodsIntegration integration) => Integration = integration;

            public IFalseGodsIntegration Integration { get; }

            public void Dispose() => Revoke(this);
        }
    }
}
