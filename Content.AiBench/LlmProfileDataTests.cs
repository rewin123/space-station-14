using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent.Llm;
using NUnit.Framework;
using Robust.Shared.Prototypes;

namespace Content.AiBench;

/// <summary>
/// Профили из <c>Resources/Prototypes/_AiAgent/llm_profiles.yml</c> разбираются и осмысленны.
///
/// <para>
/// Отдельно от <see cref="LlmRouterTests"/>, потому что здесь проверяются ДАННЫЕ, а не логика, и
/// цена другая: нужен поднятый сервер, читающий настоящие прототипы. Без этого опечатка в YAML
/// вылезла бы только при старте боевого сервера — и в худшем виде, потому что незагрузившийся
/// профиль просто выпадает из цепочки, а `ai.llm_chain` продолжает выглядеть настроенным.
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

        // Профили стенда отсеиваются, и это не косметика.
        //
        // ConfigOverlayTests заводит профили через накладку ai_data/config.d/, то есть настоящим
        // IPrototypeManager.LoadString, а серверы в этом прогоне переиспользуются пулом. Профиль,
        // заведённый там, доживает до этого теста — и заведён он СПЕЦИАЛЬНО сломанным
        // (compactHigh больше ctxLimit: именно это расхождение проверка эндпоинта обязана поймать).
        // Здесь проверяются данные форка из Resources/, а не декорации соседнего теста; префикс
        // bench- зарезервирован за ними.
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

                // Запрос на loopback, ушедший в удалённый выход, просто зависает — и зависает
                // молча, до самого таймаута хода.
                var loopback = p.Endpoint.Contains("127.0.0.1") || p.Endpoint.Contains("localhost");
                if (loopback)
                {
                    Assert.That(p.Proxy, Is.EqualTo(LlmProxyMode.None),
                        $"{p.ID}: профиль на loopback не должен ходить через прокси");
                }

                // Спрашивать /props умеет только llama-server. У остальных это 404, и без своего
                // ctxLimit порог компакции молча садится на печатный ai.compact_high.
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

                // Объект thinking понимает только DeepSeek; всем остальным он приходит незнакомым
                // полем. Диалект решает это сам, но профиль, где заявлено усилие и выбран диалект,
                // который его не пошлёт, — это настройка, выглядящая рабочей и не делающая ничего.
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
    /// Локальная модель обязана быть среди профилей: она последний рубеж цепочки.
    /// </summary>
    /// <remarks>
    /// Цепочка, целиком уехавшая в интернет, кончается вместе с интернетом — и кончается посреди
    /// раунда. Профиль на llama-swap единственный, кто продолжит отвечать, когда упадёт мост или
    /// пропадёт связь, поэтому его отсутствие в наборе стоит поймать здесь, а не в бою.
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
