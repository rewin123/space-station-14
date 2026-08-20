using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// A small HTTP server on its own port, serving <see cref="AgentDebugRouter"/> and nothing else.
///
/// <para>
/// <b>Why not the engine's status host.</b> <c>StatusHost</c> holds one of only
/// <c>status.max_connections</c> slots — five by default — for the <em>entire duration</em> of a
/// request, and shares them with <c>/status</c>, <c>/info</c> and <c>/admin/*</c>. A long-poll
/// would sit on a slot for twenty-five seconds at a time; two open debugger tabs would take 40% of
/// the server's HTTP capacity, and starving <c>/status</c> gets the server dropped from the hub.
/// A second listener on its own port has none of those couplings. The engine does the same thing
/// for its metrics endpoint — see <c>Robust.Server/DataMetrics/MetricsManager.MetricsServer.cs</c>.
/// </para>
/// <para>
/// <b>Why the BCL listener.</b> The engine uses <c>SpaceWizards.HttpListener</c>, but that package
/// is declared <c>PrivateAssets="compile"</c> in <c>Robust.Server.csproj</c>: the runtime asset
/// flows to us, the compile asset does not, and adding a <c>PackageReference</c> of our own would
/// be an edit to an upstream project file. <c>System.Net.HttpListener</c> is in the shared
/// framework and needs no reference at all. Do not "fix" this to match the engine.
/// </para>
/// </summary>
public sealed class AgentDebugServer : IDisposable
{
    /// <summary>
    /// Concurrent requests. Not for the engine's sake — we own this port — but so a client stuck in
    /// a reconnect loop cannot open sockets without bound.
    /// </summary>
    private const int MaxConcurrent = 16;

    private readonly HttpListener _listener = new();
    private readonly AgentDebugRouter _router;
    private readonly ISawmill _sawmill;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);

    public string Prefix { get; }

    private AgentDebugServer(string prefix, AgentDebugRouter router, ISawmill sawmill)
    {
        Prefix = prefix;
        _router = router;
        _sawmill = sawmill;
        _listener.Prefixes.Add(prefix);
    }

    /// <summary>
    /// Bind and start serving, or return null and say why.
    ///
    /// Never throws: a port already taken — the usual cause being a previous server in the same
    /// process that has not let go yet — must degrade to "no debug endpoint", not abort round start.
    /// </summary>
    public static AgentDebugServer? TryStart(string bind, string token, AgentDebugRouter router, ISawmill sawmill)
    {
        // Enabled with no token is refused outright rather than served openly. /state hands out the
        // entire conversation, the agent's memory and its soul, and /command can speak to the crew
        // in its voice. There is no reading of "the operator meant to publish that anonymously".
        if (string.IsNullOrEmpty(token))
        {
            sawmill.Error("шина отладки включена, но ai.debug_token пуст — сервер не поднят. " +
                          "Задай токен: /state отдаёт весь разговор и память, /command говорит от лица ИИ.");
            return null;
        }

        // A non-ASCII token cannot be sent at all: HTTP header values are ASCII, and a client that
        // tries throws before the request leaves. Without this the symptom is an endpoint that
        // answers 401 to a token the operator can see is correct.
        foreach (var c in token)
        {
            if (c > 127)
            {
                sawmill.Error("ai.debug_token содержит не-ASCII символы — такой токен нельзя передать " +
                              "в HTTP-заголовке. Сервер не поднят.");
                return null;
            }
        }

        // A concrete port, always: HttpListener has no "let the kernel choose" mode the way a
        // TcpListener does, so a caller wanting an ephemeral port has to pick one itself first.
        var prefix = $"http://{bind}/";
        var server = new AgentDebugServer(prefix, router, sawmill);

        try
        {
            server._listener.Start();
        }
        catch (Exception e)
        {
            sawmill.Error($"отладочный сервер не поднялся на {prefix}: {e.GetType().Name}: {e.Message}");
            server.Dispose();
            return null;
        }

        // Background: the listener must never be the reason the process refuses to exit.
        var thread = new Thread(() => server.ListenAsync().GetAwaiter().GetResult())
        {
            IsBackground = true,
            Name = "ai-debug-http",
        };
        thread.Start();

        sawmill.Info($"отладочный сервер слушает {prefix}");
        return server;
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (Exception e)
            {
                _sawmill.Warning($"отладочный сервер: приём соединения не удался: {e.Message}");
                continue;
            }

            await _slots.WaitAsync(_stop.Token).ConfigureAwait(false);

            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleAsync(context).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _sawmill.Warning($"отладочный запрос упал: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    _slots.Release();
                }
            });
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in request.QueryString.AllKeys)
        {
            if (key != null)
                query[key] = request.QueryString[key] ?? "";
        }

        var body = "";
        if (request.HasEntityBody)
        {
            using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
            body = await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        var response = await _router.RouteAsync(
            request.HttpMethod,
            request.Url?.AbsolutePath ?? "/",
            query,
            body,
            request.Headers["Authorization"],
            _stop.Token).ConfigureAwait(false);

        var bytes = Encoding.UTF8.GetBytes(response.Json);

        context.Response.StatusCode = response.Status;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;

        // The debugger is served from somewhere else entirely — a static page, a dev server, a
        // file:// URL. Without this every fetch fails in the browser for a reason that never
        // appears in the server log.
        // Кэшировать здесь нельзя НИЧЕГО, и это не перестраховка.
        //
        // /events — длинный опрос: один и тот же URL (instance+since) запрашивается повторно, пока
        // курсор не сдвинулся. Ответ без единой директивы кэширования браузер и промежуточные
        // прокси вправе переиспользовать по эвристике — и тогда петля получает старый ответ, курсор
        // не двигается, URL не меняется, и страница живёт вечно на первом ответе. Снаружи это
        // выглядит ровно как «события не обновляются, пока не нажмёшь F5»: перезагрузка берёт новый
        // снимок с новым seq, то есть новый URL, и на одну итерацию всё оживает.
        //
        // Поймать это пробами через Node невозможно: у его fetch нет HTTP-кэша вовсе, и та же самая
        // петля против того же сервера отрабатывает безупречно. Отсюда правило: заголовок ставится
        // независимо от того, воспроизвелось ли — цена одна строка, а отсутствие директивы у
        // повторяющегося GET это просто ошибка.
        context.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        context.Response.Headers["Pragma"] = "no-cache";

        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
        context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";

        // The one CORS header here that costs real money if it is missing.
        //
        // Absent, a browser caches a preflight for five seconds. The long-poll on /events runs for
        // twenty-five, so every single poll would pay for an extra round trip AND burn a second of
        // the six connections a browser allows per origin — halving how many tabs work before a
        // POST starts queueing behind parked polls. A day is the usual ceiling; Chrome clamps to
        // two hours on its own.
        context.Response.Headers["Access-Control-Max-Age"] = "86400";

        // Credentials are deliberately NOT allowed: the token is a header, not a cookie, and
        // Allow-Credentials is incompatible with the `*` origin above. A client that ever sets
        // credentials:'include' would fail every request with a confusing wildcard error.

        await context.Response.OutputStream.WriteAsync(bytes, _stop.Token).ConfigureAwait(false);
        context.Response.Close();
    }

    public void Dispose()
    {
        _stop.Cancel();

        try
        {
            if (_listener.IsListening)
                _listener.Stop();

            _listener.Close();
        }
        catch (Exception)
        {
            // Shutting down. Nothing here is worth a log line, let alone an exception.
        }

        _stop.Dispose();
        _slots.Dispose();
    }
}
