using System;
using System.Net.Http;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// Reachability check for the behavioural suite.
///
/// These benchmarks need a real model, which means a GPU and a running llama-swap. On a machine
/// without either, the right outcome is <b>ignored</b>, not failed: a red suite that is red for
/// environmental reasons trains everyone to stop reading it.
/// </summary>
public static class LiveLlmGate
{
    private static bool? _available;

    public const string Endpoint = "http://127.0.0.1:9292/v1";

    /// <summary>Skip the current test unless a model endpoint is answering.</summary>
    public static void RequireOrIgnore()
    {
        _available ??= Probe().GetAwaiter().GetResult();

        if (_available != true)
            Assert.Ignore($"живая модель недоступна на {Endpoint} — поведенческие бенчи пропущены");
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

            var response = await http.GetAsync($"{Endpoint}/models");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
