namespace FalseGods.Probe.Boss
{
    /// <summary>
    /// How the boss's 2D sprite chooses which way to face — the three orientation strategies a billboard boss may
    /// want, modelled on SULFUR's own <c>BillboardNpc</c> flags (Docs/Decompiled <c>NpcUpdateManager.UpdateFromPOV</c>).
    /// </summary>
    /// <remarks>
    /// Facing is split by <em>who decides it</em>. <see cref="LocalBillboard"/> is a pure <b>presentation</b> choice —
    /// each client independently turns the sprite toward its own camera, so it can differ per viewer and never needs
    /// replication. <see cref="NearestPlayer"/> is <b>authoritative</b> — it uses the direction the host simulation
    /// already computes (<c>PresentationState.Facing</c>, toward the nearest participant), so every client shows the
    /// same facing. <see cref="Fixed"/> is scripted/static. The probe exposes all three so the visual trade-offs can
    /// be judged in-game; the production renderer will pick per boss (a huge boss wants Fixed or NearestPlayer, a
    /// small mook wants LocalBillboard).
    /// </remarks>
    internal enum BossFacingMode
    {
        /// <summary>
        /// Mode 1 — a fixed world facing that ignores the camera (SULFUR's <c>disableBillboardRotation</c>). For a
        /// very large boss, or one whose facing is driven by script/movement rather than the viewer.
        /// </summary>
        Fixed,

        /// <summary>
        /// Mode 2 — face the local camera/player position (SULFUR's default <c>BillboardNpc</c>): every player sees
        /// the sprite turned toward themselves. Turning the view does not rotate it; only moving does. Honours
        /// <c>LockPitch</c> (yaw-only vs yaw + natural elevation pitch).
        /// </summary>
        LocalBillboard,

        /// <summary>
        /// Mode 3 — face the authoritative nearest-player direction (<c>PresentationState.Facing</c>), the same for
        /// every viewer. Yaw only; the boss has one real front that all clients agree on.
        /// </summary>
        NearestPlayer,
    }
}
