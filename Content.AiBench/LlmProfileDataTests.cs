using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// Profiles from <c>Resources/Prototypes/_AiAgent/llm_profiles.yml</c> parse and make sense.
///
/// <para>
/// Separate from <see cref="LlmRouterTests"/>, because this checks DATA, not logic, and the cost
/// is different: it needs a live server reading real prototypes. Without this a typo in the YAML
/// would only surface when the production server starts — and in the worst possible way, because
/// a profile that fails to load simply drops out of the chain while `ai.llm_chain` keeps looking
/// configured.
/// </para>
/// </summary>
[TestFixture]
[Category("AiContext")]
public sealed class LlmProfileDataTests
{
    [Test]
    public async Task ProfilesParseAndAreSane()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        // Bench profiles are filtered out, and that is not cosmetic.
        //
        // ConfigOverlayTests sets up profiles through the ai_data/config.d/ overlay, that is,
        // through the real IPrototypeManager.LoadString, and servers in this run are reused from a
        // pool. A profile set up there survives into this test — and it is set up DELIBERATELY
        // broken (compactHigh greater than ctxLimit: exactly this mismatch is what the endpoint
        // check must catch). This test checks the fork's own data from Resources/, not the
        // neighboring test's decorations; the bench- prefix is reserved for those.
        var profiles = await w.Read(() =>
            protoMan.EnumeratePrototypes<AiLlmProfilePrototype>()
                .Where(p => !p.ID.StartsWith("bench-", System.StringComparison.Ordinal))
                .ToList());

        Assert.That(profiles, Is.Not.Empty, "ни одного профиля модели не загрузилось");

        Assert.Multiple(() =>
        {
            foreach (var p in profiles)
            {
                Assert.That(p.Endpoint, Does.StartWith("http"), $"{p.ID}: эндпоинт не похож на URL");
                Assert.That(p.Endpoint, Does.EndWith("/v1"),
                    $"{p.ID}: клиент дописывает /chat/completions к базовому адресу, так что /v1 должен быть здесь");
                Assert.That(p.Model, Is.Not.Empty, $"{p.ID}: модель не названа");

                // A request to loopback that goes out through a remote exit simply hangs — and
                // hangs silently, all the way to the turn timeout.
                var loopback = p.Endpoint.Contains("127.0.0.1") || p.Endpoint.Contains("localhost");
                if (loopback)
                {
                    Assert.That(p.Proxy, Is.EqualTo(LlmProxyMode.None),
                        $"{p.ID}: профиль на loopback не должен ходить через прокси");
                }

                // Only llama-server knows how to answer /props. For everyone else it is a 404, and
                // without its own ctxLimit the compaction threshold silently falls back to the
                // printed ai.compact_high.
                if (p.CtxProbe == LlmCtxProbe.None)
                {
                    Assert.That(p.CtxLimit, Is.GreaterThan(0),
                        $"{p.ID}: не умеет /props и не знает своего окна — задай ctxLimit");
                }

                if (p.CompactHigh > 0 && p.CtxLimit > 0)
                {
                    Assert.That(p.CompactHigh, Is.LessThan(p.CtxLimit),
                        $"{p.ID}: порог компакции не может быть больше окна");
                }

                // Only DeepSeek understands the thinking object; for everyone else it arrives as
                // an unrecognized field. The dialect handles this on its own, but a profile that
                // declares a reasoning effort while its chosen dialect will never send it is a
                // setting that looks functional and does nothing.
                if (!string.IsNullOrWhiteSpace(p.ReasoningEffort))
                {
                    Assert.That(
                        LlmDialectRules.AllowsThinking(p.Dialect) || LlmDialectRules.AllowsReasoningEffort(p.Dialect),
                        Is.True,
                        $"{p.ID}: задан reasoningEffort, но диалект {p.Dialect} его не отправляет");
                }

                if (p.Quota == LlmQuotaKind.Metered)
                {
                    Assert.That(p.PriceInPer1M, Is.GreaterThan(0),
                        $"{p.ID}: платный профиль без цен — расход будет считаться нулём");
                }
            }
        });
    }

    /// <summary>
    /// A local model must be among the profiles: it is the chain's last line of defense.
    /// </summary>
    /// <remarks>
    /// A chain that has moved entirely onto the internet ends together with the internet — and
    /// ends mid-round. The llama-swap profile is the only one that keeps answering once the bridge
    /// goes down or connectivity drops, so its absence from the set is worth catching here, not in
    /// the field.
    /// </remarks>
    [Test]
    public async Task ThereIsALocalProfileToFallBackTo()
    {
        await using var w = await AiStation.Create();
        var protoMan = w.Pair.Server.ResolveDependency<IPrototypeManager>();

        var local = await w.Read(() => protoMan
            .EnumeratePrototypes<AiLlmProfilePrototype>()
            .Where(p => p.Dialect == LlmDialect.LlamaCpp && p.Quota == LlmQuotaKind.Free)
            .ToList());

        Assert.That(local, Is.Not.Empty, "в наборе нет ни одного локального профиля");
        Assert.That(local.Any(p => p.CtxProbe == LlmCtxProbe.Props), Is.True,
            "локальный профиль должен спрашивать n_ctx у llama-server, а не полагаться на печатное число");
    }
}
