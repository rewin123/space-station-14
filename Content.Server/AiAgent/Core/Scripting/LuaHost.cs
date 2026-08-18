using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MoonSharp.Interpreter;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>Чем кончился запуск скрипта. Коды те же, что уедут модели в <see cref="Tools.ToolError"/>.</summary>
public static class LuaError
{
    public const string Syntax = Tools.ToolError.ScriptSyntax;
    public const string Runtime = Tools.ToolError.ScriptError;
    public const string Budget = Tools.ToolError.ScriptBudget;
}

/// <summary>Потолки одного запуска. Не регуляторы темпа, а предохранители от зациклившегося скрипта.</summary>
public sealed class LuaLimits
{
    /// <summary>Инструкций Lua всего. Пять миллионов — это секунды счёта, а не цикл «пока не надоест».</summary>
    public long MaxSteps { get; init; } = 5_000_000;

    /// <summary>
    /// Инструкций между проверками отмены. Чем меньше — тем отзывчивее <c>bp_stop</c> и тем дороже
    /// счёт: замер на этой машине дал 533 среза по 20000 инструкций за 300 мс, то есть проверка
    /// обходится в доли миллисекунды.
    /// </summary>
    public int SliceInstructions { get; init; } = 20_000;

    /// <summary>
    /// Потолок по реальному времени, а не по раундовому — в отличие от таймеров агента.
    ///
    /// Таймеры живут в раундовом времени, чтобы не будить агента в замороженном мире. Здесь задача
    /// обратная: не дать потоку крутиться вечно. На паузе раундовые часы стоят, и раундовый потолок
    /// не наступил бы никогда — как раз в том случае, ради которого потолок и заведён.
    /// </summary>
    public TimeSpan MaxWall { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>Итог запуска: либо значение, которое вернул чанк, либо код ошибки с номером строки.</summary>
public sealed record LuaOutcome(bool Ok, string? Error, string? Detail, DynValue Return, long Steps);

/// <summary>
/// Клетка, в которой исполняется скрипт агента.
///
/// <para>
/// Набор модулей собран руками, потому что готовый <c>Preset_HardSandbox</c> не подошёл ни одной
/// стороной: он выкидывает не только опасное, но и <c>pcall</c> с метатаблицами. Без <c>pcall</c>
/// у скрипта нет способа пережить отказ инструмента, а вся конвенция режима на нём и держится.
/// Чего в клетке нет и не будет: <c>io</c>, <c>os</c>, <c>require</c>, <c>load</c>, <c>dofile</c>,
/// <c>debug</c> — то есть файлов, процессов и загрузки постороннего кода. Это граница по
/// построению, а не список запретов: таких понятий в этом интерпретаторе просто не существует.
/// </para>
/// <para>
/// Чанк исполняется корутиной с <c>AutoYieldCounter</c>, а не прямым вызовом. Это единственный
/// способ отобрать управление у <c>while true do end</c>: между срезами проверяются отмена и
/// потолки. Без него <c>bp_stop</c> был бы обещанием, которое нечем сдержать, — ровно то, из-за
/// чего для этой задачи не годится штатный C#-скриптинг движка.
/// </para>
/// <para>
/// Один хост — один скрипт — один поток. MoonSharp не потокобезопасен, и общего состояния между
/// процессами здесь нет намеренно: два скрипта не могут испортить друг другу таблицу глобалов.
/// </para>
/// </summary>
public sealed class LuaHost
{
    private const CoreModules Caged =
        CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling | CoreModules.Metatables;

    private readonly Script _script;
    private readonly LuaLimits _limits;

    public LuaHost(LuaLimits? limits = null, Action<string>? print = null)
    {
        _limits = limits ?? new LuaLimits();
        _script = new Script(Caged);

        // print уходит в буфер процесса, а не в консоль сервера: это вывод скрипта, его читает
        // модель через bp_get_output.
        if (print != null)
            _script.Options.DebugPrint = print;
    }

    /// <summary>Функция, видимая скрипту под именем <paramref name="name"/>.</summary>
    public void Bind(string name, Func<CallbackArguments, DynValue> body)
    {
        _script.Globals[name] = DynValue.NewCallback((_, args) => body(args), name);
    }

    /// <summary>Таблица, видимая скрипту под именем <paramref name="name"/> — для <c>raw</c>.</summary>
    public Table NewTable(string name)
    {
        var table = new Table(_script);
        _script.Globals[name] = table;
        return table;
    }

    /// <summary>Таблица из одной пары, принадлежащая этому скрипту — для подстановки голого аргумента.</summary>
    public DynValue NewValue(string key, DynValue value)
    {
        var table = new Table(_script);
        table.Set(key, value);
        return DynValue.NewTable(table);
    }

    /// <summary>Функция внутри таблицы — так живут инструменты, чьи имена заняты словами Lua.</summary>
    public void BindInto(Table table, string name, Func<CallbackArguments, DynValue> body)
    {
        table[name] = DynValue.NewCallback((_, args) => body(args), name);
    }

    /// <summary>Все имена, известные скрипту. Нужны линтеру опечаток до запуска.</summary>
    public IEnumerable<string> GlobalNames()
    {
        foreach (var pair in _script.Globals.Pairs)
        {
            if (pair.Key.Type == DataType.String)
                yield return pair.Key.String;
        }
    }

    /// <summary>
    /// Результат инструмента таблицей Lua. Живёт здесь, а не у вызывающего, потому что таблица
    /// обязана принадлежать этому же скрипту: чужая уронила бы интерпретатор при первом обращении.
    /// </summary>
    public DynValue ToLua(System.Text.Json.JsonElement element) => LuaBridge.FromJson(_script, element);

    /// <summary>Собственный код агента (прелюдия) — исполняется до скрипта и объявляет свои функции.</summary>
    public void LoadPrelude(string source)
    {
        // Прелюдия наша, не модели: синтаксическая ошибка здесь — дефект сборки, а не поведения,
        // и падать она обязана на тесте, а не тихо оставлять агента без go().
        _script.DoString(source, null, "прелюдия");
    }

    /// <summary>
    /// Исполнить скрипт модели. Отмена (<c>bp_stop</c>, смерть сессии) выходит наружу
    /// <see cref="OperationCanceledException"/> — проверено, что она проходит корутину насквозь.
    /// </summary>
    public LuaOutcome Run(string code, CancellationToken ct)
    {
        DynValue chunk;
        try
        {
            chunk = _script.LoadString(code, null, "скрипт");
        }
        catch (SyntaxErrorException e)
        {
            return new LuaOutcome(false, LuaError.Syntax, e.DecoratedMessage, DynValue.Nil, 0);
        }

        var coroutine = _script.CreateCoroutine(chunk).Coroutine;
        coroutine.AutoYieldCounter = _limits.SliceInstructions;

        long steps = 0;
        var watch = Stopwatch.StartNew();

        try
        {
            var result = coroutine.Resume();

            while (coroutine.State == CoroutineState.ForceSuspended)
            {
                steps += _limits.SliceInstructions;
                ct.ThrowIfCancellationRequested();

                if (steps > _limits.MaxSteps)
                {
                    return new LuaOutcome(false, LuaError.Budget,
                        $"скрипт съел {_limits.MaxSteps} инструкций и снят — похоже на вечный цикл",
                        DynValue.Nil, steps);
                }

                if (watch.Elapsed > _limits.MaxWall)
                {
                    return new LuaOutcome(false, LuaError.Budget,
                        $"скрипт работает дольше {_limits.MaxWall.TotalMinutes:0} мин и снят",
                        DynValue.Nil, steps);
                }

                result = coroutine.Resume();
            }

            return new LuaOutcome(true, null, null, result, steps);
        }
        catch (SyntaxErrorException e)
        {
            return new LuaOutcome(false, LuaError.Syntax, e.DecoratedMessage, DynValue.Nil, steps);
        }
        catch (ScriptRuntimeException e)
        {
            return new LuaOutcome(false, LuaError.Runtime, e.DecoratedMessage, DynValue.Nil, steps);
        }
    }
}
