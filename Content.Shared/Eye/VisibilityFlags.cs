using Robust.Shared.Serialization;

namespace Content.Shared.Eye
{
    [Flags]
    [FlagsFor(typeof(VisibilityMaskLayer))]
    public enum VisibilityFlags : int
    {
        None = 0,
        Normal = 1 << 0,
        Ghost = 1 << 1, // Observers and revenants.
        Subfloor = 1 << 2, // Pipes, disposal chutes, cables etc. while hidden under tiles. Can be revealed with a t-ray.
        Admin = 1 << 3, // Reserved for admins in stealth mode and admin tools.

        // FORK PATCH K1 (docs/upstream-patches.md). For things the client wouldn't draw anyway — mob internals,
        // the contents of closed opaque containers, the subtree of a chassis occupied by an agent.
        //
        // No eye has this flag set (EyeComponent.DefaultVisibilityMask == Normal) and no
        // GetVisMaskEvent sets it either — meaning an entity with this flag never enters PVS for
        // ANYONE. That's exactly the point: a delta for an entity the client doesn't have costs
        // it a 250 KB full state (docs/problems.md, #19), and the client couldn't have drawn it
        // anyway.
        Internal = 1 << 4,
    }
}
