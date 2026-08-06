using Content.Server.Station.Events;
using Content.Server.Station.Systems;
using Robust.Shared.Configuration;

namespace Content.Server.AiAgent;

/// <summary>
/// Одно имя станции на все карты — <c>ai.station_name</c>.
///
/// В ваниле имя собирает <see cref="StationNameSystem"/> из шаблона в прототипе карты:
/// «TG Box Station 14-Alpha», и меняется оно с каждой картой в ротации. Для сервера, где имя
/// станции — часть его лица, это значит, что лица нет: экипаж каждую смену прилетает куда-то
/// ещё, а объявления Центрального командования адресованы каждый раз новому месту.
///
/// Правится здесь, а не в прототипах карт, по правилу форка: ни одного изменённого файла
/// апстрима. Иначе пришлось бы трогать каждую карту в пуле и заново трогать любую новую.
///
/// Пустое значение отключает подмену целиком, и это важнее, чем кажется: так ведут себя
/// бенчмарки и тесты, которым ванильное поведение и нужно.
/// </summary>
public sealed class StationNameOverrideSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private StationSystem _station = default!;

    public override void Initialize()
    {
        SubscribeLocalEvent<StationPostInitEvent>(OnStationPostInit);
    }

    private void OnStationPostInit(ref StationPostInitEvent ev)
    {
        var name = _cfg.GetCVar(AiCVars.StationName);
        if (string.IsNullOrWhiteSpace(name))
            return;

        // loud: false — иначе первым, что услышит экипаж на брифинге, будет объявление
        // «станция X переименована в Y» про станцию, которой ни одной секунды не существовало.
        _station.RenameStation(ev.Station, name.Trim(), loud: false);
    }
}
