using System.Linq;
using System.Threading.Tasks;
using Content.Server.AiAgent;
using Content.Server.AiAgent.Locale;
using NUnit.Framework;

namespace Content.AiBench;

/// <summary>
/// The agent's prompt language is a mode, not a translation of the game. Default stays Russian so
/// the rest of the suite keeps asserting on the strings it already knows.
/// </summary>
[TestFixture]
public sealed class LanguageTests
{
    [Test]
    public void Parse_AcceptsEnglishAliases()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentLangUtil.Parse("ru"), Is.EqualTo(AgentLang.Ru));
            Assert.That(AgentLangUtil.Parse(""), Is.EqualTo(AgentLang.Ru));
            Assert.That(AgentLangUtil.Parse("en"), Is.EqualTo(AgentLang.En));
            Assert.That(AgentLangUtil.Parse("EN-US"), Is.EqualTo(AgentLang.En));
            Assert.That(AgentLangUtil.Parse("english"), Is.EqualTo(AgentLang.En));
        });
    }

    [Test]
    public void Locale_SwitchesKeysAndDirections()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AgentLocale.Ru.Objects, Is.EqualTo("объекты"));
            Assert.That(AgentLocale.En.Objects, Is.EqualTo("objects"));
            Assert.That(AgentLocale.Ru.Outcome, Is.EqualTo("итог"));
            Assert.That(AgentLocale.En.Outcome, Is.EqualTo("outcome"));
            Assert.That(AgentLocale.Ru.Dir(Robust.Shared.Maths.Direction.North), Is.EqualTo("север"));
            Assert.That(AgentLocale.En.Dir(Robust.Shared.Maths.Direction.North), Is.EqualTo("north"));
            Assert.That(AgentLocale.En.OperatorPrefix, Does.Contain("OUT-OF-GAME"));
            Assert.That(AgentPrompts.Station, Does.Contain("Answer in English"));
            Assert.That(AgentPrompts.Station, Does.Not.Contain("Отвечай по-русски"));
        });
    }

    [Test]
    [Category("AiTools")]
    public async Task EnglishPrompt_TellsTheModelToSpeakEnglish()
    {
        await using var w = await AiWorld.CreateEnglish();
        var prompt = await w.Read(() => w.System.BuildSystemPromptForTest());
        var look = await w.Read(() =>
            w.System.Sessions.Values.First().Registry.Tools.First(t => t.Name == "look").Description);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("You are the Station AI"));
            Assert.That(prompt, Does.Contain("Answer in English"));
            Assert.That(prompt, Does.Contain("OUT-OF-GAME SERVER OPERATOR MESSAGE"));
            Assert.That(prompt, Does.Not.Contain("Отвечай по-русски"));
            Assert.That(prompt, Does.Contain("bad_args"));
            Assert.That(prompt, Does.Contain("stale_handle"));
            Assert.That(look, Does.Contain("Look at the station through cameras"));
            Assert.That(look, Does.Not.Contain("Осмотреть станцию"));
        });
    }
}
