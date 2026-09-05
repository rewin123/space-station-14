using System.Collections.Generic;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>
/// Backup power tuning for a specific station. Keyed by the <c>gameMap</c> prototype id
/// (<c>Packed</c>, <c>Box</c>, <c>Bagel</c>…).
///
/// <para>
/// <b>Data, not a map edit.</b> Maps (<c>Resources/Maps/*.yml</c>) are upstream files, and the
/// fork does not touch them. So a "per-station patch" here means a table of numbers read by
/// <see cref="BackupPowerSystem"/>, not an edited map. The side benefit outweighs the patch
/// itself: a table like this survives any map update, whereas generators baked into the map would
/// vanish at the very first rebase.
/// </para>
/// <para>
/// <b>A missing entry is a normal path, not an error.</b> Upstream adds maps, and a new one will
/// enter rotation before anyone gets around to giving it a row here. In that case the system takes
/// the power figure from <c>ai.backup_power_watts</c> and finds a spot the generic way — next to
/// any SMES on the station. The table only exists so that on known maps the numbers are specific
/// to that station rather than an average.
/// </para>
/// </summary>
// No explicit name: RA0042 reports that "aiBackupPower" is already derived from the class name,
// and duplicating it would create a second place for it to drift out of sync with the first.
[Prototype]
public sealed partial class AiBackupPowerPrototype : IPrototype
{
    /// <summary>Id of the <c>gameMap</c> prototype this tuning applies to.</summary>
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>
    /// Total backup circuit power for this station, watts.
    ///
    /// Computed as "number of APCs x 1200W", roughly, and this is a **proxy, not a measurement**:
    /// an APC roughly corresponds to one powered room, and 1200W is a rough estimate of a room's
    /// load during a low-population shift. Real consumption is measured live with the
    /// <c>powerstat</c> command; until that measurement exists, these numbers are at least
    /// proportionate to the station, rather than the same for all of them.
    /// </summary>
    [DataField]
    public int Watts;

    /// <summary>
    /// Names of navigation beacons near which a generator looks natural — usually the engineering
    /// bay.
    ///
    /// <para>
    /// A soft preference, not an address. The system still only places a generator on a tile with a
    /// high-voltage cable next to an SMES (otherwise it would not connect at all), and the list
    /// only decides which SMES gets picked first: the one closest to a named beacon. An empty list
    /// or a name that is not found breaks nothing — the order simply stays arbitrary.
    /// </para>
    /// <para>
    /// Beacons rather than tile coordinates, deliberately. A coordinate breaks silently on any map
    /// edit; a beacon's name either is found or is honestly not found, and in the second case we
    /// still place the generator, just without a preference.
    /// </para>
    /// </summary>
    [DataField]
    public List<string> Anchors = new();
}
