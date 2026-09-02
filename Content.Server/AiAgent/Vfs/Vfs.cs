using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Vfs.Mounts;

namespace Content.Server.AiAgent.Vfs;

/// <summary>
/// Файловая система одного агента: таблица монтирований и маршрутизация по ней.
///
/// <para>
/// Своя у каждого тела, и это главное отличие от прежнего устройства. Раньше память, скиллы и
/// заметки о людях существовали в одном экземпляре на процесс, поэтому боевой киборг таскал в
/// своём префиксе двадцать килобайт библиотеки Станционного ИИ — включая досье на экипаж, которые
/// ему нечем применить и знать которые он не должен. Общий теперь только справочник, и он общий
/// одним экземпляром, а не копией на агента.
/// </para>
/// </summary>
public sealed class Vfs
{
    /// <summary>
    /// Порядок объявления в builder'е, а не порядок словаря.
    ///
    /// <para>
    /// <see cref="Dictionary{TKey,TValue}"/> порядок перечисления не гарантирует ни в
    /// документации, ни между версиями рантайма. Здесь на нём висит зона 0: переставленные строки
    /// корня — это другой SHA префикса, то есть полный prefill каждый ход, без единой ошибки в
    /// логе. Тот же приём уже применён к массиву инструментов, который сортируется по имени
    /// ровно поэтому.
    /// </para>
    /// </summary>
    private readonly List<VfsMount> _ordered;

    private readonly Dictionary<string, VfsMount> _byPoint;
    private readonly string _root;

    /// <summary>
    /// Беды с содержимым, замеченные при сборке: пустой справочник, нечитаемый каталог.
    ///
    /// Не пустой список означает, что агент работает, но чего-то не знает. Роняющие таблицу
    /// противоречия сюда не попадают — на них <see cref="VfsBuilder.Build"/> бросает исключение.
    /// </summary>
    public IReadOnlyList<string> Complaints { get; }

    internal Vfs(IReadOnlyList<VfsMount> mounts, IReadOnlyList<string>? complaints = null)
    {
        Complaints = complaints ?? System.Array.Empty<string>();

        _ordered = mounts.ToList();

        _byPoint = mounts.ToDictionary(m => m.Point, StringComparer.Ordinal);
        _root = RenderRootText(_ordered);

        foreach (var mount in _ordered)
        {
            switch (mount)
            {
                case MemoryMount memory:
                    Memory = memory.Store;
                    break;
                case NotesMount notes:
                    Notes = notes.Store;
                    break;
                case TextMount text:
                    Curator = text;
                    break;
                case DocMount { Shared: false } doc:
                    Skills = doc.Tree;
                    break;
            }
        }
    }

    public IReadOnlyList<VfsMount> Mounts => _ordered;

    /// <summary>
    /// Долгая память этого агента, или <c>null</c>, если она не смонтирована.
    ///
    /// <para>
    /// Прямая ссылка, а не поиск по точке монтирования: снимок памяти нужен сборке системного
    /// промпта на каждой перестройке префикса, и искать его там строкой значило бы завязать зону 0
    /// на имя папки.
    /// </para>
    /// </summary>
    public MemoryStore? Memory { get; private set; }

    /// <summary>Заметки о людях этого агента. По ним же приходит строка NOTE.</summary>
    public PlayerNoteStore? Notes { get; private set; }

    /// <summary>Личные записи агента — то, куда пишет куратор. Нужны отладчику и консоли.</summary>
    public DocTree? Skills { get; private set; }

    /// <summary>Промпт разбора отрезка, если он смонтирован.</summary>
    public TextMount? Curator { get; private set; }

    /// <summary>
    /// Сколько раз в это дерево что-то записали за жизнь сессии.
    ///
    /// <para>
    /// Существует ради куратора. Тот считал записи по ИМЕНАМ вызовов на проводе
    /// (<c>write_file</c>, <c>edit_file</c>), а в режиме скриптов этих имён на проводе нет вовсе —
    /// они функции Lua, — так что счётчик всегда оставался нулём и отчёт о разборе не уходил в
    /// диалог никогда. Счётчик здесь стоит НИЖЕ обеих дорог: и провод, и Lua зовут один и тот же
    /// обработчик инструмента.
    /// </para>
    /// </summary>
    public int Writes => _writes;

    private int _writes;

    /// <summary>Отметить успешную запись. Зовётся из обработчиков write_file/edit_file.</summary>
    public void NoteWrite() => System.Threading.Interlocked.Increment(ref _writes);

    /// <summary>
    /// Начать сообщать о правках на шину отладки.
    ///
    /// Общие монтирования пропускаются: справочник один на процесс, и привязать его к стоку
    /// одного агента значило бы приписать его правки чужой сессии.
    /// </summary>
    public void AttachSink(IAgentEventSink sink)
    {
        foreach (var mount in _ordered)
        {
            if (mount.Shared)
                continue;

            switch (mount)
            {
                case DocMount doc:
                    doc.Tree.AttachSink(sink);
                    break;
                case NotesMount notes:
                    notes.Store.AttachSink(sink);
                    break;
                case MemoryMount memory:
                    memory.Store.AttachSink(sink);
                    break;
            }
        }
    }

    /// <summary>
    /// Блок для зоны 0: как ходить и что где лежит.
    ///
    /// <para>
    /// Строится один раз в конструкторе и от содержимого дерева НЕ зависит — ни счётчиков, ни
    /// «229 статей». Прежний индекс менялся от каждой записи и тянул за собой перестройку
    /// префикса; этот блок постоянен, пока постоянна таблица монтирований. Зона 0 из растущей
    /// становится неподвижной, и это, а не экономия символов, — главный выигрыш.
    /// </para>
    /// </summary>
    public string RenderRoot() => _root;

    private static string RenderRootText(IReadOnlyList<VfsMount> mounts)
    {
        var sb = new StringBuilder();

        sb.Append("ФАЙЛОВАЯ СИСТЕМА\n");
        sb.Append("Всё, что ты знаешь, лежит файлами. В этом сообщении их нет — ходи сам.\n");
        sb.Append("  sh {\"cmd\":\"ls /wiki_ru\"}                      — что есть в разделе\n");
        sb.Append("  sh {\"cmd\":\"grep насос /wiki_ru\"}              — искать по словам\n");
        sb.Append("  sh {\"cmd\":\"cat /wiki_ru/атмосфера/насосы\"}    — прочитать целиком\n");
        sb.Append("  write_file / edit_file                        — записать своё\n");

        var width = mounts.Count == 0 ? 0 : mounts.Max(m => m.Point.Length);

        foreach (var mount in mounts)
        {
            var point = ("/" + mount.Point).PadRight(width + 2);
            var access = mount.Writable ? "rw-" : "r--";
            sb.Append($"  {point} {access}  {mount.Description}\n");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------ маршрутизация

    /// <summary>
    /// Найти монтирование по пути. Отказ называет существующие точки: гадать модель не обязана.
    /// </summary>
    public bool TryResolve(VfsPath path, out VfsMount mount, out VfsPath relative, out string error)
    {
        mount = null!;
        relative = VfsPath.Root;
        error = string.Empty;

        if (path.IsRoot)
        {
            error = "это корень, у него нет содержимого кроме папок ниже";
            return false;
        }

        // Точная точка монтирования, а если такой нет — с дописанным «.md». Та же поблажка, что и
        // у файлов: в корне лежит «/memory.md», и требовать расширение в каждом пути незачем.
        if (!_byPoint.TryGetValue(path.Mount, out mount!)
            && !_byPoint.TryGetValue(VfsPath.WithExtension(path.Mount), out mount!))
        {
            error = $"нет такой папки в корне: «/{path.Mount}»";
            return false;
        }

        relative = path.WithoutMount();
        return true;
    }

    public IReadOnlyList<string> MountPoints() =>
        _ordered.Select(m => "/" + m.Point).ToList();

    /// <summary>Листинг корня — то же дерево, что в зоне 0, но как ответ инструмента.</summary>
    public IReadOnlyList<VfsEntry> RootEntries() =>
        _ordered
            .Select(m => new VfsEntry(m.Point, !m.IsFile, m.Description, 0, null))
            .ToList();

    /// <summary>
    /// Перечитать своё с диска. Зовётся на шаге перестройки префикса.
    ///
    /// <para>
    /// Общие монтирования пропускаются намеренно: справочник один на процесс, и перечитывать его
    /// по разу на каждого из четырёх агентов — это четыре обхода полутора мегабайт вместо одного,
    /// причём внутри ритуала компакции, где и так платится prefill. Его обновляет система, один
    /// раз, через <see cref="VfsMount.Reload"/> самого экземпляра.
    /// </para>
    /// </summary>
    public void Reload()
    {
        foreach (var mount in _ordered)
        {
            if (mount.Shared)
                continue;

            mount.Reload();
        }
    }
}
