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

        // FORK: то, что клиент всё равно не рисует — внутренности мобов, содержимое закрытых
        // непрозрачных контейнеров, поддерево занятого агентом шасси.
        //
        // Бита нет ни у одного глаза (EyeComponent.DefaultVisibilityMask == Normal) и его не
        // выставляет ни один GetVisMaskEvent — то есть сущность с ним не попадает в PVS НИКОМУ.
        // Смысл именно в этом: дельта сущности, которой у клиента нет, стоит ему полного
        // состояния на 250 КБ (docs/problems.md, №19), а нарисовать он её всё равно не мог.
        Internal = 1 << 4,
    }
}
