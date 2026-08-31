using System;
using System.Collections.Generic;

namespace Content.Server.AiAgent.Vfs;

/// <summary>Что монтирование разрешает делать. Свойство монтирования, а не файла.</summary>
/// <remarks>
/// Права, которые может переписать тот, кто по ним ходит, — не права. Поэтому у файла поля доступа
/// нет вовсе: справочник read-only потому, что смонтирован так, и агент не может это изменить
/// никаким вызовом.
/// </remarks>
public enum VfsAccess
{
    Read,
    Write,
}

/// <summary>Одна строка листинга: файл или папка, с описанием.</summary>
/// <param name="Name">Имя без расширения — то, что подставляется в путь.</param>
/// <param name="IsDir">Папка ли. У папок описание берётся из их <c>_index</c>.</param>
/// <param name="Desc">Строка «когда:» — по ней модель и решает, открывать ли.</param>
/// <param name="Size">Размер тела в символах. У папок — число детей.</param>
/// <param name="Modified">Время последней правки, или <c>null</c>, если монтированию оно неизвестно.</param>
public sealed record VfsEntry(string Name, bool IsDir, string Desc, int Size, DateTime? Modified);

/// <summary>Итог изменяющей операции. Отказ объясняется словами, которые увидит модель.</summary>
public sealed record VfsWrite(bool Ok, string Message, IReadOnlyList<string>? Hints = null)
{
    public static VfsWrite Fine(string message) => new(true, message);
    public static VfsWrite No(string message, IReadOnlyList<string>? hints = null) => new(false, message, hints);
}

/// <summary>Одно совпадение <c>grep</c>: путь целиком, номер строки, сама строка.</summary>
public sealed record VfsHit(string Path, int Line, string Text);

/// <summary>
/// Одно монтирование.
///
/// <para>
/// Запись объявлена здесь с отказом по умолчанию, а не отдельным интерфейсом. Причина
/// практическая: монтирований только на чтение больше, чем на запись, и заставлять справочник и
/// вику игры реализовывать шесть методов ради шести одинаковых отказов — это шесть мест, где
/// однажды окажется не тот отказ. Проверку прав делает <see cref="Vfs"/> до вызова, а эти
/// заглушки — второй рубеж на случай прямого обращения к монтированию из кода.
/// </para>
/// </summary>
public abstract class VfsMount
{
    /// <summary>Точка монтирования без ведущего слэша: «wiki_ru», «skills», «memory.md».</summary>
    public required string Point { get; init; }

    /// <summary>Строка для корневого листинга в зоне 0. Постоянна: от содержимого не зависит.</summary>
    public required string Description { get; init; }

    public required VfsAccess Access { get; init; }

    /// <summary>Монтирование — один файл, а не дерево. Тогда <c>ls</c> его не раскрывает.</summary>
    public virtual bool IsFile => false;

    /// <summary>
    /// Экземпляр общий для всех агентов, и перечитывать его должен не каждый из них.
    ///
    /// <para>
    /// Ставится только через <c>VfsBuilder.AddShared</c>. Разделяется именно ЭКЗЕМПЛЯР, а не
    /// каталог: справочник весит полтора мегабайта, и держать его копию на тело значило бы
    /// вчетверо больше памяти и вчетверо больше работы на каждой перестройке префикса.
    /// </para>
    /// </summary>
    public bool Shared { get; init; }

    public bool Writable => Access == VfsAccess.Write;

    // ------------------------------------------------------------------- чтение

    /// <summary>Содержимое папки. Пустой путь — корень монтирования.</summary>
    public abstract IReadOnlyList<VfsEntry> List(VfsPath relative, out string error);

    /// <summary>Тело файла. Для папки — тело её <c>_index</c>, если оно есть.</summary>
    public abstract bool TryRead(VfsPath relative, out string content, out string error);

    /// <summary>Поиск по словам. Реализация обязана уважать <paramref name="limit"/>.</summary>
    public abstract IReadOnlyList<VfsHit> Grep(string needle, VfsPath relative, int limit);

    /// <summary>Перечитать с диска. Зовётся на шаге перестройки префикса.</summary>
    public virtual void Reload() { }

    // -------------------------------------------------------------------- запись

    public virtual VfsWrite Write(VfsPath relative, string desc, string content) => Denied();
    public virtual VfsWrite Edit(VfsPath relative, string match, string replacement) => Denied();
    public virtual VfsWrite MakeDir(VfsPath relative) => Denied();
    public virtual VfsWrite Remove(VfsPath relative) => Denied();
    public virtual VfsWrite Move(VfsPath from, VfsPath to) => Denied();

    protected VfsWrite Denied() =>
        VfsWrite.No($"/{Point} — только для чтения, менять его нельзя");
}
