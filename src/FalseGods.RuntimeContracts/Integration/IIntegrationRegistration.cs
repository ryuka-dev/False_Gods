using System;

namespace FalseGods.RuntimeContracts.Integration
{
    /// <summary>
    /// The token a successful <see cref="FalseGodsIntegrations.Register"/> returns. Disposing it revokes the
    /// registration: the slot is cleared and the change event is raised, so the Composition Root tears down the
    /// multiplayer composition (Docs/Architecture.md §4.1, ADR-004).
    /// </summary>
    /// <remarks>
    /// Only the holder of the token can revoke — there is no <c>Clear()</c> for third parties. The adapter
    /// disposes its token on plugin unload. Dispose is idempotent, and a token that has already been superseded
    /// (its registration revoked) disposes to a no-op rather than clearing someone else's registration.
    /// </remarks>
    public interface IIntegrationRegistration : IDisposable
    {
    }
}
