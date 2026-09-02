using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Robust.Shared.Log;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Config;

/// <summary>
/// Накладка на прототипы форка: YAML, лежащий в <c>ai_data/config.d/</c>, а не в <c>Resources/</c>.
///
/// <para>
/// <b>Зачем она есть.</b> Всё, чем настраивается агент, — профили провайдеров, правила режимов,
/// веса секретного пула — живёт прототипами в <c>Resources/Prototypes/_AiAgent/</c>. Это верное
/// место для того, что форк везёт с собой, и неверное для того, что отличает ОДИН сервер от
/// другого. У прототипа в <c>Resources/</c> три свойства, и каждое мешает:
/// </para>
/// <list type="number">
/// <item>он в индексе git — правка эндпоинта под своё железо становится коммитом, который потом
/// конфликтует с каждым обновлением форка;</item>
/// <item>его раздаёт ACZ (<c>Content.Server/Acz/ContentMagicAczProvider.cs</c> отдаёт всю папку
/// <c>Resources/</c> каждому подключившемуся) — то есть в него нельзя положить ничего своего;</item>
/// <item>чтобы его перечитать, нужна пересборка — а <c>ai_data/</c> перечитывается командой
/// <c>aiagent config reload</c> между раундами.</item>
/// </list>
/// <para>
/// Накладка снимает все три сразу. <c>ai_data/</c> уже перечислен в <c>.gitignore</c>, уже не
/// раздаётся игрокам и уже содержит ключи и файлы личности — то есть ровно всё «своё для этого
/// сервера». Файлы конфигурации садятся туда же.
/// </para>
/// <para>
/// <b>Формат — обычный прототипный YAML, без своей схемы.</b> Файл скармливается
/// <see cref="IPrototypeManager.LoadString"/> с <c>overwrite: true</c>, поэтому в нём можно писать
/// то же самое, что в <c>Resources/Prototypes/</c>: <c>aiLlmProfile</c>, <c>entity</c> с
/// <c>RogueAiRule</c>, <c>gamePreset</c>, <c>weightedRandom</c>. Своего разбора нет намеренно —
/// параллельная схема разошлась бы с прототипом на первом же добавленном поле, и разошлась бы
/// молча.
/// </para>
/// <para>
/// <b>Что значит overwrite.</b> Прототип заменяется ЦЕЛИКОМ, а не сливается по полям: запись с
/// тем же <c>id</c> вытесняет прежнюю. Поэтому, переопределяя профиль, перечислите все нужные
/// поля, а не только изменённое, — иначе остальные вернутся к умолчаниям типа, а не к значениям
/// из <c>Resources/</c>. Для наследования есть <c>parent:</c>, и он работает как обычно.
/// </para>
/// </summary>
public static class AiConfigOverlay
{
    /// <summary>Каталог внутри <c>ai.data_dir</c>. Пример содержимого — <c>Tools/examples/llamacpp/</c>.</summary>
    public const string DirName = "config.d";

    /// <summary>
    /// Один файл накладки и что с ним стало.
    /// </summary>
    /// <param name="Prototypes">
    /// Что файл добавил или переопределил, в виде <c>aiLlmProfile: local</c>. Печатается в
    /// <c>aiagent config</c>, и это главная строчка отчёта: «файл прочитан» и «файл что-то сделал» —
    /// разные события, а пустой список отличает опечатку в имени поля от опечатки в имени типа.
    /// </param>
    /// <param name="Error">
    /// Сообщение разбора, или null. Ошибка ОДНОГО файла не отменяет остальные: накладка — это
    /// операционная настройка живого сервера, и падать целиком из-за забытой запятой она не должна.
    /// </param>
    public sealed record OverlayFile(
        string Name,
        long Bytes,
        DateTime WrittenUtc,
        IReadOnlyList<string> Prototypes,
        string? Error);

    public sealed record OverlayReport(
        string Dir,
        bool DirExists,
        IReadOnlyList<OverlayFile> Files)
    {
        public int Ok => Files.Count(f => f.Error == null);
        public int Failed => Files.Count(f => f.Error != null);
        public int Changed => Files.Where(f => f.Error == null).Sum(f => f.Prototypes.Count);
    }

    /// <summary>
    /// Прочитать все <c>*.yml</c> из <c>&lt;dataDir&gt;/config.d/</c> в порядке имён.
    /// </summary>
    /// <param name="live">
    /// <c>true</c> — перезагрузка на живом сервере: изменения уезжают через
    /// <see cref="IPrototypeManager.ReloadPrototypes"/>, то есть с событием, на которое подписаны
    /// системы. <c>false</c> — старт процесса: тогда достаточно
    /// <see cref="IPrototypeManager.ResolveResults"/>, а рассылать событие некому — подписки систем
    /// ещё не расставлены.
    /// </param>
    /// <remarks>
    /// Порядок файлов — по имени, ordinal. Это не косметика: два файла могут трогать один
    /// прототип, и тогда побеждает последний. Отсюда же рекомендация в документации нумеровать
    /// файлы (<c>10-endpoints.yml</c>, <c>20-modes.yml</c>) — «по алфавиту» перестаёт быть
    /// случайностью, как только файлов становится больше двух.
    /// </remarks>
    public static OverlayReport Load(string dataDir, IPrototypeManager proto, bool live, ISawmill sawmill)
    {
        var dir = Path.Combine(dataDir, DirName);
        var files = new List<OverlayFile>();

        if (!Directory.Exists(dir))
        {
            // Не ошибка и даже не предупреждение. Пустая накладка — обычное состояние сборки,
            // которую только что склонировали: она обязана работать на том, что лежит в Resources/.
            sawmill.Debug($"накладка: каталога {dir} нет, работаю на прототипах из Resources/");
            return new OverlayReport(dir, DirExists: false, files);
        }

        var all = new Dictionary<Type, HashSet<string>>();
        var names = Directory.GetFiles(dir, "*.yml").OrderBy(f => f, StringComparer.Ordinal).ToList();

        foreach (var path in names)
        {
            var info = new FileInfo(path);
            var changed = new Dictionary<Type, HashSet<string>>();

            try
            {
                proto.LoadString(File.ReadAllText(path), overwrite: true, changed);

                foreach (var (type, ids) in changed)
                {
                    if (!all.TryGetValue(type, out var set))
                        all[type] = set = new HashSet<string>();

                    set.UnionWith(ids);
                }

                files.Add(new OverlayFile(info.Name, info.Length, info.LastWriteTimeUtc, Describe(changed), null));
            }
            catch (Exception e)
            {
                // Одной строкой и без стека: читать это будут в консоли админа, а не в отладчике,
                // и «в 12-й строке ожидался ключ» полезнее сорока кадров YamlDotNet.
                var why = e.Message.Split('\n')[0].Trim();
                sawmill.Error($"накладка: {info.Name} не разобран — {why}");
                files.Add(new OverlayFile(info.Name, info.Length, info.LastWriteTimeUtc, Array.Empty<string>(), why));
            }
        }

        if (all.Count > 0)
        {
            if (live)
                proto.ReloadPrototypes(all);
            else
                proto.ResolveResults();
        }

        var report = new OverlayReport(dir, DirExists: true, files);

        if (report.Ok > 0 || report.Failed > 0)
        {
            sawmill.Info($"накладка {dir}: файлов {report.Ok}, с ошибкой {report.Failed}, " +
                         $"прототипов затронуто {report.Changed}");
        }

        return report;
    }

    /// <summary>
    /// «aiLlmProfile: local» вместо «AiLlmProfilePrototype: local».
    ///
    /// Имя типа в отчёте должно совпадать с тем, что человек пишет в поле <c>type:</c>, иначе отчёт
    /// не помогает найти строчку, которую надо править.
    /// </summary>
    private static List<string> Describe(Dictionary<Type, HashSet<string>> changed)
    {
        var result = new List<string>();

        foreach (var (type, ids) in changed)
        {
            var kind = type.Name;

            if (kind.EndsWith("Prototype", StringComparison.Ordinal))
                kind = kind[..^"Prototype".Length];

            kind = char.ToLowerInvariant(kind[0]) + kind[1..];

            foreach (var id in ids.OrderBy(i => i, StringComparer.Ordinal))
                result.Add($"{kind}: {id}");
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
