using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>Ручки режима, снятые с cvar'ов один раз при установке.</summary>
public sealed class ScriptOptions
{
    public required Func<int> ForegroundMs { get; init; }
    public required Func<int> MaxProcesses { get; init; }
    public required Func<int> MaxSeconds { get; init; }
    public required Func<int> MaxCalls { get; init; }
    public required Func<int> MaxSteps { get; init; }
    public required Func<int> OutputLines { get; init; }
}

/// <summary>
/// Три инструмента, которые модель видит в режиме скрипта, — и больше ничего.
///
/// <para>
/// Это и есть «или/или». В классическом режиме нет <c>script</c>; в режиме скрипта нет ни
/// <c>look</c>, ни <c>use</c>, ни <c>goto</c> как отдельных вызовов — они существуют только
/// функциями Lua. Смешение дало бы модели два способа сделать одно и то же и заставило бы её
/// выбирать между ними на каждом ходу.
/// </para>
/// <para>
/// Исключение ровно одно — <c>noop</c>. Это не действие в мире, а способ закрыть ход; без него
/// закончить ход можно было бы только прозой, а проза уходит экипажу как речь.
/// </para>
/// </summary>
public sealed class ScriptTools
{
    /// <summary>Что уходит на провод, когда режим включён.</summary>
    public static readonly IReadOnlySet<string> WireNames =
        new HashSet<string>(StringComparer.Ordinal) { "script", "bp_get_output", "bp_stop", "noop" };

    private readonly ScriptOptions _options;

    public ScriptTools(ScriptOptions options)
    {
        _options = options;
    }

    /// <summary>Перевести сессию в режим скрипта: завести таблицу процессов и подменить провод.</summary>
    public void Install(AgentSession session, AiToolRegistry registry)
    {
        var table = new ScriptProcessTable();
        session.Scripts = table;

        var runtime = new ScriptRuntime(
            session,
            () => session.State.Mode == AgentMode.Review ? DispatchGate.NoGameActions : DispatchGate.None,
            () => new LuaLimits
            {
                MaxSteps = _options.MaxSteps(),
                MaxWall = TimeSpan.FromSeconds(_options.MaxSeconds()),
            },
            _options.MaxCalls);

        registry.Register(new AiTool
        {
            Name = "script",
            Description =
                "Выполнить программу на Lua. Все твои действия — функции этого языка: look, use, " +
                "pickup, drop, go, examine, say, radio, memory и остальные. Отказ инструмента " +
                "бросает ошибку и останавливает скрипт на этой строке; переживать отказ — pcall. " +
                "Скрипт длиннее секунды уходит в фон и присылает итог наблюдением.",
            SchemaJson =
                """
                {"type":"object","required":["code"],"additionalProperties":false,"properties":{
                "code":{"type":"string","description":"код на Lua; последний return станет полем ответ"}}}
                """,
            GameAction = true,
            Handler = (args, ct) => RunAsync(session, runtime, table, args, ct),
        });

        registry.Register(new AiTool
        {
            Name = "bp_get_output",
            Description =
                "Что напечатал фоновый скрипт с прошлого раза. Отдаёт только новые строки, " +
                "а не весь вывод заново.",
            SchemaJson =
                """
                {"type":"object","required":["pid"],"additionalProperties":false,"properties":{
                "pid":{"type":"integer","description":"номер процесса из ответа script"}}}
                """,
            Handler = (args, ct) => Task.FromResult(Output(table, args)),
        });

        registry.Register(new AiTool
        {
            Name = "bp_stop",
            Description = "Снять фоновый скрипт. Уже сделанное не отменяется.",
            SchemaJson =
                """
                {"type":"object","required":["pid"],"additionalProperties":false,"properties":{
                "pid":{"type":"integer","description":"номер процесса из ответа script"}}}
                """,
            GameAction = true,
            Handler = (args, ct) => Task.FromResult(Stop(table, args)),
        });

        registry.Register(new AiTool
        {
            Name = "help",
            Description = "Справка по функциям: что принимает инструмент.",
            SchemaJson =
                """
                {"type":"object","additionalProperties":false,"properties":{
                "tool":{"type":"string"}}}
                """,
            Wire = false,
            Handler = (args, ct) => Task.FromResult(Help(session, args)),
        });

        // Последней строкой: до неё провод должен быть собран целиком.
        registry.WireAllow = WireNames;
    }

    private async Task<ToolResult> RunAsync(
        AgentSession session,
        ScriptRuntime runtime,
        ScriptProcessTable table,
        JsonElement args,
        CancellationToken ct)
    {
        if (!StationAiAgentSystem.TryGetString(args, "code", out var code) || string.IsNullOrWhiteSpace(code))
            return ToolResult.Fail(ToolError.BadArgs, "нужен код на Lua в поле code");

        // Линтер до запуска. Опечатка в имени функции иначе всплыла бы на середине работы, когда
        // робот уже прошёл полстанции и что-то поднял, — и стоила бы отката, которого нет.
        var known = runtime.KnownNames();
        var unknown = ScriptLint.Unknown(code!, known);

        if (unknown.Count > 0)
        {
            var nearest = unknown
                .SelectMany(name => session.Registry.Nearest(name, 2))
                .Distinct(StringComparer.Ordinal)
                .Take(5);

            return ToolResult.Fail(
                ToolError.ScriptSyntax,
                $"таких функций нет: {string.Join(", ", unknown)} — ничего не выполнялось",
                retry: "other_target",
                alternatives: nearest.ToList());
        }

        var running = table.Running();
        if (running.Count >= _options.MaxProcesses())
        {
            return ToolResult.Fail(
                ToolError.Refused,
                $"уже работает скрипт #{running[0].Pid} ({running[0].Elapsed.TotalSeconds:0} с) — " +
                "тело одно, дождись или сними его через bp_stop",
                retry: "later");
        }

        var process = table.Start(code!, _options.OutputLines(), runtime.Build);

        var foreground = Task.Delay(Math.Max(0, _options.ForegroundMs()), ct);
        var finished = await Task.WhenAny(process.Finished, foreground).ConfigureAwait(false);

        if (finished != process.Finished)
        {
            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["pid"] = process.Pid,
                ["статус"] = "идёт",
                ["как_узнать"] = "придёт наблюдение СКРИПТ, когда закончит; можно спросить bp_get_output",
            });
        }

        return Report(process);
    }

    /// <summary>Итог законченного процесса — один и тот же для обоих способов о нём узнать.</summary>
    public static ToolResult Report(ScriptProcess process)
    {
        var effect = new Dictionary<string, object?>
        {
            ["pid"] = process.Pid,
            ["статус"] = process.StatusWord(),
            ["секунд"] = Math.Round(process.Elapsed.TotalSeconds, 1),
            ["вызовов"] = process.Calls,
            ["вывод"] = process.ReadNew(),
        };

        if (process.Answer != null)
            effect["ответ"] = process.Answer;

        if (process.Status == ScriptStatus.Done)
            return ToolResult.Success(effect);

        if (process.Status == ScriptStatus.Stopped)
        {
            effect["почему"] = "снят";
            return ToolResult.Success(effect);
        }

        return ToolResult.Fail(
            process.Error ?? ToolError.ScriptError,
            process.Detail ?? "скрипт не доработал",
            retry: "other_target",
            effect: effect);
    }

    /// <summary>
    /// Справка по функциям, читаемая из самих схем инструментов.
    ///
    /// <para>
    /// Появилась не для удобства, а чтобы закрыть дыру режима. В классическом режиме описания и
    /// схемы инструментов уходят модели полем <c>tools</c> и являются единственным источником
    /// правды о том, что инструмент принимает. Режим скрипта их с провода снимает — и всё, чего
    /// не пересказал промпт, для модели перестаёт существовать. На боевом прогоне это стоило
    /// запуска реактора: агент десять ходов не мог вставить банку в контроллер, потому что не
    /// знал про аргумент <c>with_item</c>, а тот жил только в схеме.
    /// </para>
    /// <para>
    /// Пересказывать схемы в промпте было бы вторым источником правды, который разойдётся с
    /// первым на первой же правке инструмента. Здесь справка читается из реестра, поэтому
    /// расходиться нечему.
    /// </para>
    /// </summary>
    private static ToolResult Help(AgentSession session, JsonElement args)
    {
        StationAiAgentSystem.TryGetString(args, "tool", out var wanted);

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            if (!session.Registry.TryGet(wanted!, out var one))
            {
                return ToolResult.Fail(ToolError.UnknownTool, $"нет функции «{wanted}»",
                    retry: "other_target", alternatives: session.Registry.Nearest(wanted!));
            }

            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["функция"] = one.Name,
                ["описание"] = one.Description,
                ["аргументы"] = one.SchemaJson,
            });
        }

        var rows = new List<string>();

        foreach (var tool in session.Registry.Tools.OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            if (WireNames.Contains(tool.Name))
                continue;

            var name = ScriptRuntime.AliasOf(tool.Name) ?? tool.Name;
            rows.Add($"{name}{{{Signature(tool)}}} — {tool.Description}");
        }

        return ToolResult.Success(new Dictionary<string, object?> { ["функции"] = rows });
    }

    /// <summary>Имена аргументов из схемы; обязательные помечены звёздочкой.</summary>
    private static string Signature(AiTool tool)
    {
        try
        {
            using var schema = JsonDocument.Parse(tool.SchemaJson);

            if (!schema.RootElement.TryGetProperty("properties", out var properties))
                return "";

            var required = new HashSet<string>(StringComparer.Ordinal);

            if (schema.RootElement.TryGetProperty("required", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                {
                    var value = item.GetString();
                    if (value != null)
                        required.Add(value);
                }
            }

            return string.Join(", ", properties.EnumerateObject()
                .Select(p => required.Contains(p.Name) ? p.Name + "*" : p.Name));
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static ToolResult Output(ScriptProcessTable table, JsonElement args)
    {
        if (!TryGetPid(args, out var pid))
            return ToolResult.Fail(ToolError.BadArgs, "нужен номер процесса в поле pid");

        var process = table.Get(pid);
        if (process == null)
            return ToolResult.Fail(ToolError.NoProcess, $"процесса #{pid} нет", retry: "none");

        if (!process.IsRunning)
            return Report(process);

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["pid"] = process.Pid,
            ["статус"] = "идёт",
            ["секунд"] = Math.Round(process.Elapsed.TotalSeconds, 1),
            ["вызовов"] = process.Calls,
            ["новое"] = process.ReadNew(),
        });
    }

    private static ToolResult Stop(ScriptProcessTable table, JsonElement args)
    {
        if (!TryGetPid(args, out var pid))
            return ToolResult.Fail(ToolError.BadArgs, "нужен номер процесса в поле pid");

        var process = table.Get(pid);
        if (process == null)
            return ToolResult.Fail(ToolError.NoProcess, $"процесса #{pid} нет", retry: "none");

        if (!process.IsRunning)
        {
            return ToolResult.Success(new Dictionary<string, object?>
            {
                ["pid"] = pid,
                ["статус"] = process.StatusWord(),
                ["уже"] = "закончился сам",
            });
        }

        process.Stop();

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["pid"] = pid,
            ["статус"] = "снимаю",
            ["сделанное"] = "не отменяется",
        });
    }

    private static bool TryGetPid(JsonElement args, out int pid)
    {
        pid = 0;

        return args.ValueKind == JsonValueKind.Object
               && args.TryGetProperty("pid", out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt32(out pid);
    }
}
