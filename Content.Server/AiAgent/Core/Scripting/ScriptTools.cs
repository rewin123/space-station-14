using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Content.Server.AiAgent.Tools;

namespace Content.Server.AiAgent.Core.Scripting;

/// <summary>The mode's knobs, taken from cvars once at setup.</summary>
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
/// The three tools the model sees in script mode — and nothing else.
///
/// <para>
/// This is exactly the "either/or." In classic mode there's no <c>script</c>; in script mode there's
/// no <c>look</c>, <c>use</c>, or <c>goto</c> as separate calls — they exist only as Lua functions.
/// Mixing them would give the model two ways to do the same thing and force it to choose between
/// them on every turn.
/// </para>
/// <para>
/// There's exactly one exception — <c>noop</c>. It's not an action in the world but a way to close a
/// turn; without it, ending a turn would only be possible through prose, and prose goes to the crew
/// as speech.
/// </para>
/// </summary>
public sealed class ScriptTools
{
    /// <summary>What goes on the wire when the mode is enabled.</summary>
    public static readonly IReadOnlySet<string> WireNames =
        new HashSet<string>(StringComparer.Ordinal) { "script", "bp_get_output", "bp_stop", "noop" };

    private readonly ScriptOptions _options;

    public ScriptTools(ScriptOptions options)
    {
        _options = options;
    }

    /// <summary>Switch the session into script mode: set up the process table and swap the wire.</summary>
    public void Install(AgentSession session, AiToolRegistry registry)
    {
        var table = new ScriptProcessTable { Locale = session.Locale };
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

        var L = session.Locale;

        registry.Register(new AiTool
        {
            Name = "script",
            Description = L.T(
                "Выполнить программу на Lua. Все твои действия — функции этого языка: look, use, " +
                "pickup, drop, go, examine, say, radio, memory и остальные. Отказ инструмента " +
                "бросает ошибку и останавливает скрипт на этой строке; переживать отказ — pcall. " +
                "Скрипт длиннее секунды уходит в фон и присылает итог наблюдением.",
                "Run a Lua program. All your actions are functions of this language: look, use, " +
                "pickup, drop, go, examine, say, radio, memory and the rest. A tool refusal " +
                "throws and stops the script on that line; catch a refusal with pcall. A script " +
                "longer than a second goes to the background and sends the outcome as an observation."),
            SchemaJson =
                """
                {"type":"object","required":["code"],"additionalProperties":false,"properties":{
                "code":{"type":"string","description":"код на Lua; последний return станет полем ответ"}}}
                """,

            // There's NO GameAction here — and that's not an oversight. In script mode, only four
            // names go on the wire (see WireNames), and everything else, including write_file and
            // edit_file, lives as Lua functions. Marking `script` itself as a game action closed off
            // the ENTIRE toolset during review at once: the curator got review_mode on the very first
            // call and answered "I didn't change any files" — exactly what happened on 2026-09-01,
            // review #1, 0 records.
            //
            // Game calls, meanwhile, stay gated: the gate sits INSIDE, on every call from Lua (the
            // lambda with AgentMode.Review just above), and it only starts working now that the
            // outer door has stopped slamming shut before it.
            Handler = (args, ct) => RunAsync(session, runtime, table, args, ct),
        });

        registry.Register(new AiTool
        {
            Name = "bp_get_output",
            Description = L.T(
                "Что напечатал фоновый скрипт с прошлого раза. Отдаёт только новые строки, " +
                "а не весь вывод заново.",
                "What the background script printed since last time. Returns only new lines, " +
                "not the whole output again."),
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
            Description = L.T(
                "Снять фоновый скрипт. Уже сделанное не отменяется.",
                "Stop a background script. What already happened is not undone."),
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
            Description = L.T(
                "Справка по функциям: что принимает инструмент.",
                "Function help: what a tool accepts."),
            SchemaJson =
                """
                {"type":"object","additionalProperties":false,"properties":{
                "tool":{"type":"string"}}}
                """,
            Wire = false,
            Handler = (args, ct) => Task.FromResult(Help(session, args)),
        });

        // As the last line: the wire must be fully assembled before this.
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

        // Linter before the run. Otherwise a typo in a function name would surface midway through the
        // work, once the robot had already crossed half the station and picked something up — and
        // would cost a rollback that doesn't exist.
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
                [session.Locale.Status] = session.Locale.ScriptRunning,
                [session.Locale.HowToKnow] = session.Locale.T(
                    "придёт наблюдение СКРИПТ, когда закончит; можно спросить bp_get_output",
                    "a SCRIPT observation will arrive when it finishes; you can also ask bp_get_output"),
            });
        }

        return Report(process, session.Locale);
    }

    /// <summary>The result of a finished process — the same for both ways of finding out about it.</summary>
    public static ToolResult Report(ScriptProcess process, Locale.AgentLocale? loc = null)
    {
        loc ??= Locale.AgentLocale.Ru;
        var effect = new Dictionary<string, object?>
        {
            ["pid"] = process.Pid,
            [loc.Status] = process.StatusWord(loc),
            [loc.Seconds] = Math.Round(process.Elapsed.TotalSeconds, 1),
            [loc.Calls] = process.Calls,
            [loc.Output] = process.ReadNew(),
        };

        if (process.Answer != null)
            effect[loc.Answer] = process.Answer;

        if (process.Status == ScriptStatus.Done)
            return ToolResult.Success(effect);

        if (process.Status == ScriptStatus.Stopped)
        {
            effect[loc.Why] = loc.ScriptStopped;
            return ToolResult.Success(effect);
        }

        return ToolResult.Fail(
            process.Error ?? ToolError.ScriptError,
            process.Detail ?? loc.T("скрипт не доработал", "the script did not finish"),
            retry: "other_target",
            effect: effect);
    }

    /// <summary>
    /// Help on the functions, read straight from the tool schemas themselves.
    ///
    /// <para>
    /// This didn't appear for convenience, but to close a hole in the mode. In classic mode, tool
    /// descriptions and schemas go to the model in the <c>tools</c> field and are the single source
    /// of truth for what a tool accepts. Script mode takes them off the wire — and anything the
    /// prompt didn't retell stops existing for the model. On a live run this cost a reactor startup:
    /// the agent couldn't insert the can into the controller for ten turns, because it didn't know
    /// about the <c>with_item</c> argument, which only lived in the schema.
    /// </para>
    /// <para>
    /// Retelling the schemas in the prompt would be a second source of truth that would diverge from
    /// the first at the very first tool edit. Here the help is read from the registry, so there's
    /// nothing to diverge.
    /// </para>
    /// </summary>
    private static ToolResult Help(AgentSession session, JsonElement args)
    {
        StationAiAgentSystem.TryGetString(args, "tool", out var wanted);

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            if (!session.Registry.TryGet(wanted!, out var one))
            {
                return ToolResult.Fail(ToolError.UnknownTool,
                    session.Locale.T($"нет функции «{wanted}»", $"no function «{wanted}»"),
                    retry: "other_target", alternatives: session.Registry.Nearest(wanted!));
            }

            return ToolResult.Success(new Dictionary<string, object?>
            {
                [session.Locale.Function] = one.Name,
                [session.Locale.Description] = one.Description,
                [session.Locale.Arguments] = one.SchemaJson,
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

        return ToolResult.Success(new Dictionary<string, object?> { [session.Locale.Functions] = rows });
    }

    /// <summary>Argument names from the schema; required ones are marked with an asterisk.</summary>
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
            return Report(process, table.Locale);

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["pid"] = process.Pid,
            [table.Locale.Status] = table.Locale.ScriptRunning,
            [table.Locale.Seconds] = Math.Round(process.Elapsed.TotalSeconds, 1),
            [table.Locale.Calls] = process.Calls,
            [table.Locale.NewLines] = process.ReadNew(),
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
                [table.Locale.Status] = process.StatusWord(table.Locale),
                [table.Locale.Already] = table.Locale.ScriptFinishedOnItsOwn,
            });
        }

        process.Stop();

        return ToolResult.Success(new Dictionary<string, object?>
        {
            ["pid"] = pid,
            [table.Locale.Status] = table.Locale.ScriptStopping,
            [table.Locale.DoneKey] = table.Locale.ScriptDoneStays,
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
