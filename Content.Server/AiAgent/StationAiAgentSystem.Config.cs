using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Config;
using Content.Server.AiAgent.Llm;
using Content.Server.AiAgent.RogueAi;
using Content.Server.GameTicking.Presets;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent;

/// <summary>
/// Реконфигурация без пересборки: накладка прототипов, проверка эндпоинта, обзор режимов.
///
/// <para>
/// Отдельным файлом, потому что это операционный слой, а не игровой: ничего отсюда не вызывается
/// из петли хода. Всё, что здесь есть, существует ради одного вопроса — «почему сервер ведёт себя
/// не так, как написано в конфиге» — и отвечает на него ДО раунда, а не по журналу через сутки.
/// </para>
/// </summary>
public sealed partial class StationAiAgentSystem
{
    private AiConfigOverlay.OverlayReport? _overlay;

    /// <summary>Что накладка сделала в последний раз. Null — её ни разу не читали.</summary>
    public AiConfigOverlay.OverlayReport? Overlay => _overlay;

    /// <summary>
    /// Прочитать <c>ai_data/config.d/*.yml</c> поверх прототипов из <c>Resources/</c>.
    /// </summary>
    /// <param name="live">
    /// Живая перезагрузка (из консоли) против стартовой. Разница — в способе доложить об изменении
    /// системам; подробности в <see cref="AiConfigOverlay.Load"/>.
    /// </param>
    /// <remarks>
    /// Живая перезагрузка меняет ДАННЫЕ, а не уже существующие сущности. Правило раунда,
    /// поставленное на старте, продолжит жить со старыми полями до конца смены — новые значения
    /// достанутся следующему раунду. Профили модели переберутся раньше: клиент собирается на
    /// первом ходу агента, и <c>aiagent release</c> заставит собрать его заново.
    /// </remarks>
    public AiConfigOverlay.OverlayReport LoadOverlay(bool live)
    {
        _overlay = AiConfigOverlay.Load(DataDir(), _protoMan, live, _sawmill);
        return _overlay;
    }

    // ------------------------------------------------------------------ профили

    /// <summary>
    /// Собрать всё, что нужно, чтобы сходить к профилю: прототип, адрес и параметры.
    /// </summary>
    /// <remarks>
    /// Тем же <c>EndpointFor</c>/<c>SamplingFor</c>, что и боевой ход. Собрать проверке свой
    /// адрес значило бы проверять не то, что играет: половина поломок настройки — это прокси,
    /// ключ и диалект, то есть ровно те поля, которые здесь и вычисляются.
    /// </remarks>
    public bool TryProfile(
        string id,
        out AiLlmProfilePrototype profile,
        out LlmEndpoint endpoint,
        out LlmSampling sampling)
    {
        if (!_protoMan.TryIndex(id, out AiLlmProfilePrototype? found))
        {
            profile = default!;
            endpoint = default!;
            sampling = default!;
            return false;
        }

        profile = found;
        endpoint = EndpointFor(found);
        sampling = SamplingFor(found);
        return true;
    }

    /// <summary>Все известные профили, по алфавиту.</summary>
    public IEnumerable<AiLlmProfilePrototype> Profiles() =>
        _protoMan.EnumeratePrototypes<AiLlmProfilePrototype>().OrderBy(p => p.ID, StringComparer.Ordinal);

    /// <summary>
    /// Цепочка, по которой сейчас пошёл бы новый агент ядра: своя у режима, иначе общая.
    /// </summary>
    public IReadOnlyList<string> ActiveChain()
    {
        var raw = StationLlmChain();

        if (string.IsNullOrWhiteSpace(raw))
            raw = _cfg.GetCVar(AiCVars.LlmChain);

        return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Откуда взялся ключ профиля, в печатном виде. Значение не показывается никогда.</summary>
    public string KeyNote(AiLlmProfilePrototype profile)
    {
        if (string.IsNullOrWhiteSpace(profile.KeyFile))
        {
            var inline = _cfg.GetCVar(AiCVars.ApiKey);
            return string.IsNullOrWhiteSpace(inline)
                ? "не задан (ни keyFile, ни ai.api_key) — запросы уйдут без авторизации"
                : $"ai.api_key, {inline.Length} символов";
        }

        var path = System.IO.Path.Combine(DataDir(), profile.KeyFile);

        if (!System.IO.File.Exists(path))
            return $"ФАЙЛА НЕТ: {path}";

        try
        {
            var length = System.IO.File.ReadAllText(path).Trim().Length;
            return length == 0
                ? $"{profile.KeyFile} — файл пустой"
                : $"{profile.KeyFile}, {length} символов";
        }
        catch (Exception e)
        {
            return $"{profile.KeyFile} — не прочитать: {e.Message}";
        }
    }

    // -------------------------------------------------------------------- проверка

    private int _probing;

    /// <summary>
    /// Сходить к каждому профилю по-настоящему и доложить расхождения.
    /// </summary>
    /// <param name="write">
    /// Куда писать. Вызывается ВСЕГДА на главном потоке — консольная оболочка привязана к сессии
    /// админа и писать в неё из чужого потока нельзя.
    /// </param>
    /// <remarks>
    /// <para>
    /// Не блокирует главный поток, и это не украшение: облачный профиль отвечает секунды, а
    /// таймаут у него — минуты. Синхронная проверка на сервере с людьми означала бы зависший
    /// такт ровно в тот момент, когда админ пытается понять, почему ИИ молчит.
    /// </para>
    /// <para>
    /// Одновременно идёт одна проверка. Второй вызов не встаёт в очередь, а отказывается: это
    /// платные запросы, и два отчёта вперемешку в одной консоли всё равно нечитаемы.
    /// </para>
    /// </remarks>
    public bool StartProbe(IReadOnlyList<string> ids, Action<string> write)
    {
        if (Interlocked.CompareExchange(ref _probing, 1, 0) != 0)
            return false;

        var jobs = new List<(AiLlmProfilePrototype, LlmEndpoint, LlmSampling, string)>();
        var missing = new List<string>();

        foreach (var id in ids)
        {
            if (TryProfile(id, out var profile, out var endpoint, out var sampling))
                jobs.Add((profile, endpoint, sampling, KeyNote(profile)));
            else
                missing.Add(id);
        }

        var compactHigh = _cfg.GetCVar(AiCVars.CompactHigh);
        var total = _cfg.GetCVar(AiCVars.LlmTotalTimeout);
        var sawmill = _sawmill;

        // Захвачено значением до ухода в фон: читать cvar'ы с чужого потока нельзя.
        Task.Run(async () =>
        {
            try
            {
                foreach (var id in missing)
                    Post(write, $"{id}: такого профиля нет (aiLlmProfile)");

                foreach (var (profile, endpoint, sampling, keyNote) in jobs)
                {
                    List<string> lines;

                    try
                    {
                        lines = await LlmProbe
                            .RunAsync(profile, endpoint, sampling, keyNote, compactHigh, total, sawmill,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception e)
                    {
                        lines = new List<string> { $"{profile.ID}: проверка сорвалась — {e.Message}" };
                    }

                    foreach (var line in lines)
                        Post(write, line);
                }

                Post(write, "проверка закончена");
            }
            finally
            {
                Volatile.Write(ref _probing, 0);
            }
        });

        return true;
    }

    /// <summary>
    /// Строка отчёта — на главный поток, и молча, если писать уже некому.
    /// </summary>
    /// <remarks>
    /// Админ, закрывший консоль или отвалившийся по сети, — обычный исход долгой проверки, а не
    /// повод залить журнал стеками. Строка при этом не теряется: <see cref="LlmProbe"/> пишет
    /// собственный лог, и отчёт целиком остаётся в журнале сервера.
    /// </remarks>
    private void Post(Action<string> write, string line)
    {
        _sawmill.Info($"проверка эндпоинта: {line}");

        _taskManager.RunOnMainThread(() =>
        {
            try
            {
                write(line);
            }
            catch (Exception)
            {
                // Консоль отвалилась. См. комментарий выше.
            }
        });
    }

    // --------------------------------------------------------------------- режимы

    /// <summary>Пресет, его правило ИИ и то, во что это правило разрешилось.</summary>
    public sealed record ModeView(
        string Preset,
        string Rule,
        string Lawset,
        string SoulFile,
        string Chain,
        bool Announces,
        bool EndsRoundOnAiDeath,
        bool GrantDoors,
        bool GrantConsoles,
        bool GrantTurrets,
        IReadOnlyList<string> Borgs,
        IReadOnlyList<string> Beacons);

    /// <summary>
    /// Все пресеты, у которых есть правило с <see cref="RogueAiRuleComponent"/>.
    /// </summary>
    /// <remarks>
    /// Читается из ПРОТОТИПОВ, а не из живого раунда, и потому отвечает на вопрос «что будет,
    /// если выпадет этот режим» — тот самый, который иначе решается чтением трёх YAML подряд и
    /// держанием в голове того, что накладка могла их переписать.
    /// </remarks>
    public List<ModeView> Modes()
    {
        var result = new List<ModeView>();

        foreach (var preset in _protoMan.EnumeratePrototypes<GamePresetPrototype>()
                     .OrderBy(p => p.ID, StringComparer.Ordinal))
        {
            foreach (var ruleId in preset.Rules)
            {
                if (!_protoMan.TryIndex<EntityPrototype>(ruleId, out var rule))
                    continue;

                if (!rule.TryComp<RogueAiRuleComponent>(out var comp, Factory))
                    continue;

                result.Add(new ModeView(
                    preset.ID,
                    ruleId,
                    comp.Lawset.Id,
                    comp.SoulFile,
                    string.IsNullOrWhiteSpace(comp.LlmChain) ? "(общая ai.llm_chain)" : comp.LlmChain,
                    comp.AnnounceOnStart,
                    comp.EndsRoundOnAiDeath,
                    comp.GrantDoors,
                    comp.GrantConsoles,
                    comp.GrantTurrets,
                    comp.SupportBorgs.Select(b => b.Id).ToList(),
                    comp.SupportBorgBeacons.ToList()));
            }
        }

        return result;
    }
}
