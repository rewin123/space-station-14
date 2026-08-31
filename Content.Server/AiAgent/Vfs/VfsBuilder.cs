using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Vfs.Mounts;
using Robust.Shared.ContentPack;
using Robust.Shared.Prototypes;

namespace Content.Server.AiAgent.Vfs;

/// <summary>
/// Единственный способ собрать файловую систему агента.
///
/// <code>
/// var vfs = new VfsBuilder(sawmill)
///     .AddShared(library,                          "wiki_ru",   VfsAccess.Read,  "справочник по игре")
///     .AddGuidebook(proto, res,                    "wiki_en",   VfsAccess.Read,  "вика игры по-английски")
///     .AddFolder(Path.Combine(dir, "skills"),      "skills",    VfsAccess.Write, "что ты понял сам")
///     .AddNotes (Path.Combine(dir, "people"),      "players",   VfsAccess.Write, "заметки о людях", Stamp)
///     .AddMemory(Path.Combine(dir, "memory"),      "memory.md", VfsAccess.Write, "факты о станции")
///     .AddText  (RoleFile(dir, "CURATOR.md"),      "curator.md",VfsAccess.Read,  "чем ты руководствуешься на разборе")
///     .Build();
/// </code>
///
/// <para>
/// Глаголов пять, и каждый называет то, что за ним стоит, вместо того чтобы делать вид, будто за
/// всеми монтированиями одна машинерия. За <see cref="AddNotes"/> и <see cref="AddMemory"/> стоят
/// нетронутые сторы со штампами раунда и лимитами; за <see cref="AddGuidebook"/> — прототипы, у
/// которых вообще нет каталога на диске. Один общий <c>AddFolder(путь, точка, права)</c> заставил
/// бы каждого читателя гадать, что именно произойдёт с его файлами.
/// </para>
/// </summary>
public sealed class VfsBuilder
{
    private readonly ISawmill _sawmill;
    private readonly List<VfsMount> _mounts = new();

    /// <summary>Противоречия в самой таблице. Собираются все разом и роняют <see cref="Build"/>.</summary>
    private readonly List<string> _problems = new();

    /// <summary>
    /// Беды с содержимым: пустой справочник, нечитаемый каталог.
    ///
    /// <para>
    /// Отдельно от <see cref="_problems"/> намеренно. Кривая таблица монтирований — ошибка
    /// программиста, и падать на ней правильно. Пустая вика — беда развёртывания, и падать на ней
    /// НЕЛЬЗЯ: исключение при сборке тела означает, что агент на станции не появится вовсе, то
    /// есть раунд без ИИ вместо раунда с ИИ, который не знает справочника. Второе хуже, но первое
    /// катастрофичнее.
    /// </para>
    /// <para>
    /// Молчать при этом тоже нельзя — «агент разучился» без единой строки в логе разбирают
    /// сутками. Поэтому громко: <c>Error</c> в саймилл и список на самой
    /// <see cref="Vfs.Complaints"/>, где его видно тестам и отладчику.
    /// </para>
    /// </summary>
    private readonly List<string> _complaints = new();

    public VfsBuilder(ISawmill sawmill)
    {
        _sawmill = sawmill;
    }

    // --------------------------------------------------------------- монтирования

    /// <summary>Дерево статей на диске. Общий случай: и справочник, и личные записи агента.</summary>
    public VfsBuilder AddFolder(string diskPath, string point, VfsAccess access, string description)
    {
        var tree = new DocTree(diskPath, _sawmill);

        if (access == VfsAccess.Write)
            Ensure(diskPath);

        tree.Reload();

        if (access == VfsAccess.Read && tree.Count == 0)
            _complaints.Add($"/{point}: каталог {diskPath} пуст или не читается, а смонтирован только на чтение");

        return Add(new DocMount
        {
            Point = point,
            Description = description,
            Access = access,
            Tree = tree,
        });
    }

    /// <summary>
    /// Уже готовое дерево, общее для всех агентов.
    ///
    /// <para>
    /// Разделяется ЭКЗЕМПЛЯР, а не каталог. Справочник весит полтора мегабайта; копия на каждое из
    /// четырёх тел — это вчетверо больше памяти и вчетверо больше обходов диска на каждой
    /// перестройке префикса, причём внутри ритуала компакции.
    /// </para>
    /// </summary>
    public VfsBuilder AddShared(DocTree tree, string point, VfsAccess access, string description)
    {
        if (access == VfsAccess.Read && tree.Count == 0)
            _complaints.Add($"/{point}: общее дерево {tree.Root} пусто, а смонтировано только на чтение");

        return Add(new DocMount
        {
            Point = point,
            Description = description,
            Access = access,
            Shared = true,
            Tree = tree,
        });
    }

    /// <summary>
    /// Заметки о людях: под монтированием нетронутый <see cref="PlayerNoteStore"/>.
    /// </summary>
    /// <param name="agentDir">
    /// Каталог АГЕНТА, а не папка заметок: стор сам дописывает к нему «people», и передавать
    /// готовый путь значило бы завести второй способ вычислять тот же самый.
    /// </param>
    public VfsBuilder AddNotes(
        string agentDir,
        string point,
        VfsAccess access,
        string description,
        Func<string> stamp)
    {
        Ensure(Path.Combine(agentDir, "people"));

        var store = new PlayerNoteStore(agentDir, _sawmill);
        store.LoadFromDisk();

        return Add(new NotesMount
        {
            Point = point,
            Description = description,
            Access = access,
            Store = store,
            Stamp = stamp,
        });
    }

    /// <summary>Долгая память: под монтированием нетронутый <see cref="MemoryStore"/>.</summary>
    /// <param name="agentDir">Каталог агента: стор сам дописывает к нему «memory».</param>
    /// <param name="limit">
    /// Потолок памяти в символах. Параметром, а не свойством после сборки: у стора он объявлен
    /// <c>init</c>, и это правильно — потолок, который можно подвинуть на ходу, не потолок.
    /// </param>
    public VfsBuilder AddMemory(
        string agentDir,
        string point,
        VfsAccess access,
        string description,
        int limit = 4000)
    {
        Ensure(Path.Combine(agentDir, "memory"));

        var store = new MemoryStore(agentDir, _sawmill) { MemoryLimit = limit };
        store.LoadFromDisk();
        store.RefreshSnapshot();

        return Add(new MemoryMount
        {
            Point = point,
            Description = description,
            Access = access,
            Store = store,
        });
    }

    /// <summary>
    /// Уже собранное общее монтирование — вика игры, например.
    ///
    /// Экземпляр общий на процесс: у <see cref="Mounts.GuidebookMount"/> дерево строится из
    /// прототипов и от тела не зависит, а строить его заново на каждого из четырёх агентов значило
    /// бы четыре обхода всех прототипов вики вместо одного.
    /// </summary>
    public VfsBuilder AddShared(VfsMount mount)
    {
        if (!mount.Shared)
            _problems.Add($"/{mount.Point}: передано в AddShared, но не помечено общим");

        return Add(mount);
    }

    /// <summary>Вика игры: дерево и имена берутся у прототипов, диска у неё нет.</summary>
    public VfsBuilder AddGuidebook(
        IPrototypeManager proto,
        IResourceManager res,
        string point,
        VfsAccess access,
        string description)
    {
        var mount = new GuidebookMount(proto, res, _sawmill)
        {
            Point = point,
            Description = description,
            Access = access,
            Shared = true,
        };

        return Add(mount);
    }

    /// <summary>Один текстовый файл без разбора на «когда» и тело.</summary>
    public VfsBuilder AddText(string file, string point, VfsAccess access, string description) =>
        Add(new TextMount
        {
            Point = point,
            Description = description,
            Access = access,
            File = file,
        });

    // -------------------------------------------------------------------- сборка

    /// <summary>
    /// Собрать. Падает на противоречивой таблице, а не подстраивается под неё.
    ///
    /// <para>
    /// Ошибки собираются все разом и сообщаются одним исключением. Падать на первой значило бы
    /// заставить чинить таблицу по одной строке за перезапуск сервера.
    /// </para>
    /// </summary>
    public Vfs Build()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var mount in _mounts)
        {
            if (mount.Point.Length == 0 || mount.Point.Contains('/'))
                _problems.Add($"точка монтирования «{mount.Point}» должна быть одним сегментом без слэшей");

            if (!seen.Add(mount.Point))
                _problems.Add($"точка монтирования «/{mount.Point}» объявлена дважды");

            if (mount.Description.Length == 0)
                _problems.Add($"/{mount.Point}: нет описания, а оно едет в системный промпт");
        }

        if (_mounts.Count == 0)
            _problems.Add("не объявлено ни одного монтирования");

        if (_problems.Count > 0)
            throw new InvalidOperationException(
                "файловая система агента не собирается:\n  " + string.Join("\n  ", _problems));

        foreach (var complaint in _complaints)
            _sawmill.Error($"файловая система: {complaint}");

        return new Vfs(_mounts, _complaints);
    }

    private VfsBuilder Add(VfsMount mount)
    {
        _mounts.Add(mount);
        return this;
    }

    /// <summary>
    /// Завести каталог под запись заранее.
    ///
    /// <para>
    /// Первый борг приходит на станцию с пустым каталогом, и первая же его запись иначе упиралась
    /// бы в отсутствующую папку — то есть в отказ, который выглядит как «агент не умеет писать».
    /// Создать пустой каталог дешевле, чем объяснять это в промпте.
    /// </para>
    /// </summary>
    private void Ensure(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception e)
        {
            _problems.Add($"каталог {path} не создаётся: {e.GetType().Name}: {e.Message}");
        }
    }
}
