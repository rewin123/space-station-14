using System;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Reachability check for the behavioural suite.
///
/// These benchmarks need a real model — nowadays a hosted one, which means an endpoint and a key.
/// On a machine with neither, the right outcome is <b>ignored</b>, not failed: a red suite that is
/// red for environmental reasons trains everyone to stop reading it.
///
/// Both come from the environment so the same suite can be pointed at a local llama-swap or at a
/// provider without editing anything:
/// <code>AI_ENDPOINT=… AI_MODEL=… AI_API_KEY=… Tools/aibench --live</code>
/// </summary>
public static class LiveLlmGate
{
    private static bool? _available;

    public static string Endpoint =>
        Environment.GetEnvironmentVariable("AI_ENDPOINT") is { Length: > 0 } fromEnv
            ? fromEnv
            : "https://api.deepseek.com/v1";

    /// <summary>Skip the current test unless a model endpoint is answering.</summary>
    public static void RequireOrIgnore()
    {
        _available ??= Probe().GetAwaiter().GetResult();

        if (_available != true)
        {
            Assert.Ignore($"живая модель недоступна на {Endpoint} — поведенческие бенчи пропущены. " +
                          "Для внешнего провайдера нужен AI_API_KEY");
        }
    }

    private static async Task<bool> Probe()
    {
        try
        {
            // Proxy-free for the same reason the agent's own client is: this box exports a global
            // HTTP_PROXY that swallows localhost and hangs the request.
            using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, Proxy = null })
            {
                Timeout = TimeSpan.FromSeconds(10),
            };

            // A hosted endpoint rejects an unauthenticated probe, so the key is part of the check:
            // "no key configured" and "provider is down" both mean the same thing here — the
            // behavioural suite cannot run.
            var key = Environment.GetEnvironmentVariable("AI_API_KEY");
            if (!string.IsNullOrWhiteSpace(key))
            {
                http.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            }

            var response = await http.GetAsync($"{Endpoint}/models");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
