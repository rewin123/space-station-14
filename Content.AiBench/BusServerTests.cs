using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Content.Server.AiAgent.Bus;
using Content.Server.AiAgent.Skills;
using Content.Server.AiAgent.Vfs;
using NUnit.Framework;
using Robust.Shared.Log;

namespace Content.AiBench;

/// <summary>
/// The one test that binds a real socket.
///
/// Everything else about the API is covered against <see cref="AgentDebugRouter"/> directly, which
/// is why this is a single case and not a suite: the bench pool reuses server instances inside one
/// process, so a leaked listener does not fail the test that leaked it — it fails whichever test
/// binds next, naming the wrong culprit. Port 0 (kernel-assigned) plus a disposal in a
/// <c>finally</c> keeps that from being possible here.
///
/// What it actually proves is the plumbing the router cannot: that a request survives the round
/// trip through <c>System.Net.HttpListener</c> with its status code, its UTF-8 body and its
/// Authorization header intact.
/// </summary>
[TestFixture]
[Category("AiBusSocket")]
[Explicit("Биндит настоящий порт — вне обычного прогона, чтобы пул серверов не унаследовал слушателя")]
public sealed class BusServerTests
{
    // ASCII: the token travels in an Authorization header, and headers are ASCII-only.
    private const string Token = "socket-test-token";

    /// <summary>Та же таблица монтирований, что у живого агента, но без справочника.</summary>
    private static Vfs NewVfs(string dir) => new VfsBuilder(Sawmill)
        .AddFolder(Path.Combine(dir, "skills"), "skills", VfsAccess.Write, "что ты понял сам")
        .AddNotes(dir, "players", VfsAccess.Write, "заметки о людях", () => "[раунд 1 · 01.01]")
        .AddMemory(dir, "memory.md", VfsAccess.Write, "факты о станции")
        .Build();

    private static ISawmill Sawmill => new LogManager().GetSawmill("bus-server-test");

    [Test]
    public async Task StateRoundTripsOverARealSocket()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aibench-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var bus = new AgentEventBus(256);

        var vfs = NewVfs(dir);
        vfs.AttachSink(bus.ForProcess());

        var memory = vfs.Memory!;
        memory.Add("Иван Петров — инженер");

        var router = new AgentDebugRouter(
            bus, Token,
            // Пустая витрина: тела никто не занял. Проверяется транспорт, а не агенты.
            new AgentDirectory(),
            () => vfs,
            () => 7,
            (_, _, c) => memory.Add(c),
            (n, w, b, _, _) =>
            {
                var r = vfs.Skills!.Write(n, n, w ?? "", b ?? "");
                return new SkillResult(r.Ok, r.Message, r.Hints);
            });

        // A free port, found by binding one and letting go. HttpListener has no port-0 mode, so
        // this is the only way to avoid a fixed number that a leftover process could be sitting on.
        var server = AgentDebugServer.TryStart($"127.0.0.1:{FreePort()}", Token, router, Sawmill);

        try
        {
            Assert.That(server, Is.Not.Null, "сервер не поднялся на 127.0.0.1:0");

            var baseUrl = server!.Prefix.TrimEnd('/');

            // NO_PROXY: this box exports a global HTTP_PROXY that otherwise swallows localhost.
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            var state = await http.GetAsync($"{baseUrl}/state");
            var json = await state.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            Assert.Multiple(() =>
            {
                Assert.That((int)state.StatusCode, Is.EqualTo(200));
                Assert.That(doc.RootElement.GetProperty("instance").GetString(), Is.EqualTo(bus.Instance));
                Assert.That(doc.RootElement.GetProperty("memory").GetProperty("memory_live")[0].GetString(),
                    Is.EqualTo("Иван Петров — инженер"),
                    "кириллица не пережила круг через сокет");
                Assert.That(doc.RootElement.GetProperty("session").ValueKind, Is.EqualTo(JsonValueKind.Null),
                    "агента нет — поле обязано быть null, а не отсутствовать");
            });

            // Auth is enforced by the router, but the header has to survive the listener first.
            using var anonymous = new HttpClient(new HttpClientHandler { UseProxy = false });
            var refused = await anonymous.GetAsync($"{baseUrl}/state");

            Assert.That((int)refused.StatusCode, Is.EqualTo(401));

            // A command with a body, to prove the request stream is read correctly.
            var command = await http.PostAsync($"{baseUrl}/command", new StringContent(
                "{\"type\":\"memory.change\",\"action\":\"add\",\"content\":\"Мария Сидорова — врач\"}",
                Encoding.UTF8, "application/json"));

            using var commandBody = JsonDocument.Parse(await command.Content.ReadAsStringAsync());

            Assert.Multiple(() =>
            {
                Assert.That((int)command.StatusCode, Is.EqualTo(200));
                Assert.That(commandBody.RootElement.GetProperty("visible_to_model").GetString(),
                    Is.EqualTo("next_compaction"));
                Assert.That(memory.Entries(), Does.Contain("Мария Сидорова — врач"));
            });
        }
        finally
        {
            server?.Dispose();

            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Ask the kernel for a free port, then give it straight back.</summary>
    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Test]
    public void EnabledWithNoTokenRefusesToBind()
    {
        // Serving /state openly hands any player the agent's whole conversation, its memory and its
        // soul, and /command lets them speak in its voice. There is no charitable reading of an
        // empty token, so this is a refusal rather than a warning.
        var bus = new AgentEventBus(16);
        var dir = Path.Combine(Path.GetTempPath(), "aibench-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            var vfs = NewVfs(dir);

            var router = new AgentDebugRouter(
                bus, "", new AgentDirectory(), () => vfs, () => 7,
                (_, _, c) => vfs.Memory!.Add(c),
                (n, w, b, _, _) =>
                {
                    var r = vfs.Skills!.Write(n, n, w ?? "", b ?? "");
                    return new SkillResult(r.Ok, r.Message, r.Hints);
                });

            var server = AgentDebugServer.TryStart($"127.0.0.1:{FreePort()}", "", router, Sawmill);

            Assert.That(server, Is.Null, "сервер поднялся с пустым токеном");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
