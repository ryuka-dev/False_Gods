using System;
using System.Collections.Generic;
using FalseGods.RuntimeContracts.Integration;
using FalseGods.RuntimeContracts.Multiplayer;
using FalseGods.RuntimeContracts.Transport;
using Xunit;

namespace FalseGods.RuntimeContractsTests
{
    /// <summary>
    /// The FalseGodsIntegrations broker's registration semantics (ADR-004, PoC B0): first registration wins, a
    /// duplicate is rejected without replacing, only the token's holder can revoke, and every slot change raises
    /// the change event exactly once.
    /// </summary>
    /// <remarks>
    /// The broker is process-global by design, so every test here restores the empty slot before it returns —
    /// register/assert/dispose, including on the failure path (<c>try/finally</c>). Keeping all broker tests in
    /// this one class makes xunit run them sequentially, so no two tests race the shared slot.
    /// </remarks>
    public sealed class FalseGodsIntegrationsTests
    {
        [Fact]
        public void RegisterFillsTheEmptySlotAndReturnsAToken()
        {
            var integration = new FakeIntegration();
            var token = FalseGodsIntegrations.Register(integration);
            try
            {
                Assert.NotNull(token);
                Assert.Same(integration, FalseGodsIntegrations.Current);
            }
            finally
            {
                token?.Dispose();
            }

            Assert.Null(FalseGodsIntegrations.Current);
        }

        [Fact]
        public void RegisterNullThrows()
        {
            Assert.Throws<ArgumentNullException>(() => FalseGodsIntegrations.Register(null!));
            Assert.Null(FalseGodsIntegrations.Current);
        }

        [Fact]
        public void SecondRegistrationIsRejectedAndTheFirstStaysAuthoritative()
        {
            var first = new FakeIntegration();
            var second = new FakeIntegration();
            var firstToken = FalseGodsIntegrations.Register(first);
            try
            {
                Assert.NotNull(firstToken);

                var secondToken = FalseGodsIntegrations.Register(second);

                Assert.Null(secondToken); // rejected: no throw, no replacement.
                Assert.Same(first, FalseGodsIntegrations.Current);
            }
            finally
            {
                firstToken?.Dispose();
            }
        }

        [Fact]
        public void DisposingTheTokenClearsTheSlotAndAllowsANewRegistration()
        {
            var first = new FakeIntegration();
            var firstToken = FalseGodsIntegrations.Register(first);
            Assert.NotNull(firstToken);

            firstToken!.Dispose();
            Assert.Null(FalseGodsIntegrations.Current);

            var second = new FakeIntegration();
            var secondToken = FalseGodsIntegrations.Register(second);
            try
            {
                Assert.NotNull(secondToken);
                Assert.Same(second, FalseGodsIntegrations.Current);
            }
            finally
            {
                secondToken?.Dispose();
            }
        }

        [Fact]
        public void DisposeIsIdempotentAndNeverClearsASuccessorRegistration()
        {
            var first = new FakeIntegration();
            var firstToken = FalseGodsIntegrations.Register(first);
            Assert.NotNull(firstToken);
            firstToken!.Dispose();

            var second = new FakeIntegration();
            var secondToken = FalseGodsIntegrations.Register(second);
            try
            {
                Assert.NotNull(secondToken);

                // A stale token disposed again must be a no-op, not a revocation of the successor.
                firstToken.Dispose();

                Assert.Same(second, FalseGodsIntegrations.Current);
            }
            finally
            {
                secondToken?.Dispose();
            }
        }

        [Fact]
        public void ChangedFiresOnceOnRegisterAndOnceOnRevoke()
        {
            var observed = new List<IFalseGodsIntegration?>();
            Action handler = () => observed.Add(FalseGodsIntegrations.Current);
            FalseGodsIntegrations.Changed += handler;
            IIntegrationRegistration? token = null;
            try
            {
                var integration = new FakeIntegration();
                token = FalseGodsIntegrations.Register(integration);
                Assert.NotNull(token);

                token!.Dispose();
                token = null;

                // A stale dispose must not raise the event again.
                Assert.Equal(new IFalseGodsIntegration?[] { integration, null }, observed);
            }
            finally
            {
                FalseGodsIntegrations.Changed -= handler;
                token?.Dispose();
            }
        }

        [Fact]
        public void RejectedRegistrationDoesNotRaiseChanged()
        {
            var first = new FakeIntegration();
            var firstToken = FalseGodsIntegrations.Register(first);
            Assert.NotNull(firstToken);

            var changes = 0;
            Action handler = () => changes++;
            FalseGodsIntegrations.Changed += handler;
            try
            {
                Assert.Null(FalseGodsIntegrations.Register(new FakeIntegration()));
                Assert.Equal(0, changes);
            }
            finally
            {
                FalseGodsIntegrations.Changed -= handler;
                firstToken?.Dispose();
            }
        }

        private sealed class FakeIntegration : IFalseGodsIntegration
        {
            public IMultiplayerSession Session { get; } = new FakeSession();

            public IEncounterChannel Channel { get; } = new FakeChannel();

            public IPlayerRoster Roster { get; } = new FakeRoster();
        }

        private sealed class FakeSession : IMultiplayerSession
        {
            public SessionRole Role => SessionRole.Host;

            public bool IsActive => false;

            public SessionPeerId LocalPeer => new SessionPeerId(1);

            public SessionPeerId HostPeer => new SessionPeerId(1);
        }

        private sealed class FakeChannel : IEncounterChannel
        {
            public event Action<SessionPeerId, EncodedPayload>? Received
            {
                add { }
                remove { }
            }

            public void Send(EncodedPayload payload, MessageDelivery delivery, MessageTarget target)
            {
            }
        }

        private sealed class FakeRoster : IPlayerRoster
        {
            public IReadOnlyList<SessionPeerId> Members { get; } = Array.Empty<SessionPeerId>();
        }
    }
}
