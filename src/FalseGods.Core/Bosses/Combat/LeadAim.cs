using FalseGods.Core.Simulation;

namespace FalseGods.Core.Bosses.Combat
{
    /// <summary>
    /// Where to aim a thrown thing so it meets a moving target: the target's position carried forward along its
    /// current velocity for the time the throw is in the air.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is one step and not a solve.</b> Leading a projectile is usually a quadratic — the time to
    /// reach the aim point depends on how far away it is, which depends on the lead, which depends on the time. A
    /// crate volley escapes that entirely: a crate's whole journey, the lift off the pile and the hover and the
    /// arc, lasts a fixed span the thrower chooses, not one the distance decides. So the meeting point is the
    /// target's position plus its velocity times that fixed span — a single multiply-add, computed identically on
    /// every peer from the same numbers.</para>
    /// <para><b>Why the ground plane only.</b> The target is a player on foot and the crates land on the floor, so
    /// the lead lives on the X/Z plane; height is the caller's, the same place the arc's endpoints already are.</para>
    /// </remarks>
    public static class LeadAim
    {
        /// <summary>
        /// The point <paramref name="position"/> reaches after <paramref name="seconds"/> travelling at
        /// <paramref name="velocity"/>. A zero velocity or zero time simply gives the position back.
        /// </summary>
        public static SimVector2 Predict(SimVector2 position, SimVector2 velocity, float seconds)
        {
            return new SimVector2(position.X + velocity.X * seconds, position.Z + velocity.Z * seconds);
        }
    }
}
