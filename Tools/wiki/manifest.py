#!/usr/bin/env python3
"""Карта: категория → источники во внутренней вики → префикс статей.

Один прогон агента pi — одна категория. Карта здесь, а не в скрипте запуска, потому что её же
читает линтер: он обязан знать, какому префиксу какие статьи принадлежат.

Проверка полноты обязательна: если в Guidebook появился файл, не попавший ни в одну категорию,
скрипт валится. Иначе новая страница вики просто не доедет до агента, и узнать об этом будет
неоткуда.
"""

import os
import sys

GUIDE = "Resources/ServerInfo/Guidebook"
PROTO = "Resources/Prototypes"

# Химия и кухня в guidebook — пустые обёртки: игра собирает эти страницы из прототипов на лету.
# Сырой YAML модели давать нельзя (294 КиБ отступов), поэтому Tools/wiki/digest.py сворачивает
# его в читаемые строки, и категория смотрит уже на них.
DIGEST = "wiki_skills/.source"

# (ключ, префикс статей, [пути-источники], человеческое название)
CATEGORIES = [
    ("виды", "виды-", [
        f"{GUIDE}/Mobs",
    ], "Виды: человек, вокс, диона, слизь, мотылёк, арахнид, дварф, рептилоид, вульпканин"),

    ("сб", "сб-", [
        f"{GUIDE}/Security",
    ], "Служба безопасности: арест, улики, судебная экспертиза, записи, разминирование"),

    ("мед", "мед-", [
        f"{GUIDE}/Medical",
    ], "Медицина: типы урона, лекарства, крио, клонирование, дефибриллятор"),

    ("наука", "наука-", [
        f"{GUIDE}/Science",
        f"{PROTO}/Research",
    ], "Наука: аномалии, артефакты, борги, роботехника, дерево технологий"),

    ("снабжение", "снабжение-", [
        f"{GUIDE}/Cargo",
    ], "Снабжение: заказы, баунти, утилизация, шахта, шаттл"),

    ("сервис", "сервис-", [
        f"{GUIDE}/Service",
        f"{DIGEST}/сервис-рецепты.txt",
    ], "Сервис: бар, кухня, ботаника, уборка, рецепты"),

    ("антаг", "антаг-", [
        f"{GUIDE}/Antagonist",
    ], "Антагонисты: предатель, ядерные, революция, ниндзя, маг, вор, зомби, ксеноборг"),

    ("силикон", "силикон-", [
        f"{GUIDE}/ServerRules/SiliconRules",
        f"{GUIDE}/ReferenceTables/Lawsets.xml",
        f"{PROTO}/silicon-laws.yml",
    ], "Силиконы: законы, лоусеты, приоритет, толкование, приказы"),

    ("закон", "закон-", [
        f"{GUIDE}/ServerRules/SpaceLaw",
    ], "Космический закон: статьи, наказания, права задержанного"),

    ("правила", "правила-", [
        f"{GUIDE}/ServerRules/CoreRules",
        f"{GUIDE}/ServerRules/RoleplayRules",
        f"{GUIDE}/ServerRules/MRPRules",
        f"{GUIDE}/ServerRules/DefaultRules.xml",
        f"{GUIDE}/ServerRules/RoleTypes.xml",
        f"{GUIDE}/ServerRules/BanTypes.xml",
        f"{GUIDE}/ServerRules/BanDurations.xml",
        f"{GUIDE}/ServerRules/WizDenCoreOnlyRules.xml",
        f"{GUIDE}/ServerRules/WizDenLRPRules.xml",
        f"{GUIDE}/ServerRules/WizDenMRPRules.xml",
    ], "Правила сервера: отыгрыш, эскалация, самоантаг, метагейм, роли"),

    ("питание", "питание-", [
        f"{GUIDE}/Engineering/Power.xml",
        f"{GUIDE}/Engineering/PowerStorage.xml",
        f"{GUIDE}/Engineering/VoltageNetworks.xml",
        f"{GUIDE}/Engineering/InspectingPower.xml",
        f"{GUIDE}/Engineering/Generators.xml",
        f"{GUIDE}/Engineering/PortableGenerator.xml",
        f"{GUIDE}/Engineering/SolarPanels.xml",
        f"{GUIDE}/Engineering/RTG.xml",
        f"{GUIDE}/Engineering/AME.xml",
        f"{GUIDE}/Engineering/TEG.xml",
        f"{GUIDE}/Engineering/Radiators.xml",
        f"{GUIDE}/Engineering/SingularityEngine.xml",
        f"{GUIDE}/Engineering/TeslaEngine.xml",
        f"{GUIDE}/Engineering/SingularityTeslaEngine.xml",
    ], "Питание: сеть напряжений, СМЕС, APC, двигатели, TEG, AME, сингулярность, Тесла"),

    ("атмосфера", "атмосфера-", [
        f"{GUIDE}/Engineering/Atmospherics.xml",
        f"{GUIDE}/Engineering/AtmosphericsSystems.xml",
        f"{GUIDE}/Engineering/AtmosphereInOut.xml",
        f"{GUIDE}/Engineering/Gasses.xml",
        f"{GUIDE}/Engineering/Pipes.xml",
        f"{GUIDE}/Engineering/PipeNetworks.xml",
        f"{GUIDE}/Engineering/Pumps.xml",
        f"{GUIDE}/Engineering/Valves.xml",
        f"{GUIDE}/Engineering/ManualValve.xml",
        f"{GUIDE}/Engineering/PneumaticValve.xml",
        f"{GUIDE}/Engineering/SignalValve.xml",
        f"{GUIDE}/Engineering/PassiveGate.xml",
        f"{GUIDE}/Engineering/PressureRegulator.xml",
        f"{GUIDE}/Engineering/AirVent.xml",
        f"{GUIDE}/Engineering/AirScrubber.xml",
        f"{GUIDE}/Engineering/AirInjector.xml",
        f"{GUIDE}/Engineering/PassiveVent.xml",
        f"{GUIDE}/Engineering/AirAlarms.xml",
        f"{GUIDE}/Engineering/MixingAndFiltering.xml",
        f"{GUIDE}/Engineering/Thermomachines.xml",
        f"{GUIDE}/Engineering/GasCanisters.xml",
        f"{GUIDE}/Engineering/GasCondensing.xml",
        f"{GUIDE}/Engineering/GasManipulation.xml",
        f"{GUIDE}/Engineering/GasMiningAndStorage.xml",
        f"{GUIDE}/Engineering/GasRecycling.xml",
        f"{GUIDE}/Engineering/PortableScrubber.xml",
        f"{GUIDE}/Engineering/AtmosphericUpsets.xml",
        f"{GUIDE}/Engineering/Fires.xml",
        f"{GUIDE}/Engineering/Spacing.xml",
        f"{GUIDE}/Engineering/DeltaPressure.xml",
        f"{GUIDE}/Engineering/AtmosTools.xml",
        f"{GUIDE}/Engineering/FireAndGasControl.xml",
        f"{GUIDE}/Engineering/Ramping.xml",
        f"{GUIDE}/Engineering/AtmosphericAlertsComputer.xml",
        f"{GUIDE}/Engineering/AtmosphericNetworkMonitor.xml",
    ], "Атмосфера: газы, трубы, насосы, вентили, скрубберы, тревоги, пожар, разгерметизация"),

    ("строй", "строй-", [
        f"{GUIDE}/Engineering/Engineering.xml",
        f"{GUIDE}/Engineering/Construction.xml",
        f"{GUIDE}/Engineering/ExpandingRepairingStation.xml",
        f"{GUIDE}/Engineering/WirePanels.xml",
        f"{GUIDE}/Engineering/Airlocks.xml",
        f"{GUIDE}/Engineering/AirlockSecurity.xml",
        f"{GUIDE}/Engineering/AccessConfigurator.xml",
        f"{GUIDE}/Engineering/Networking.xml",
        f"{GUIDE}/Engineering/DeviceMonitoringAndControl.xml",
        f"{GUIDE}/Engineering/Shuttlecraft.xml",
    ], "Строительство: инструменты, стены, шлюзы, провода, связывание устройств, RCD"),

    ("химия", "химия-", [
        f"{GUIDE}/Chemicals.xml",
        f"{GUIDE}/ChemicalTabs",
        f"{DIGEST}/химия-реакции.txt",
        f"{DIGEST}/химия-реагенты.txt",
    ], "Химия: реагенты, реакции, медикаменты, токсины, пиротехника, наркотики"),

    ("должности", "должности-", [
        f"{GUIDE}/Jobs.xml",
        f"{GUIDE}/Command.xml",
        f"{GUIDE}/Service/Service.xml",
        f"{GUIDE}/Medical/Medical.xml",
        f"{GUIDE}/Security/Security.xml",
        f"{GUIDE}/Science/Science.xml",
        f"{GUIDE}/Cargo/Cargo.xml",
        f"{GUIDE}/Engineering/Engineering.xml",
    ], "Должности: отделы, командование, доступы, кто за что отвечает"),

    ("общее", "", [
        f"{GUIDE}/NewPlayer",
        f"{GUIDE}/Glossary.xml",
        f"{GUIDE}/Survival.xml",
        f"{GUIDE}/SpaceStation14.xml",
        f"{GUIDE}/References.xml",
        f"{GUIDE}/Writing.xml",
    ], "Общее: словарь терминов, выживание, связь, управление, письмо — префиксы связь-/события-"),
]

# Файлы вики, которые сознательно никому не отданы, и почему.
SKIPPED = {
    f"{GUIDE}/ServerRules/README.txt": "служебный файл сборки правил, не текст для игрока",
    f"{GUIDE}/ReferenceTables/Drinks.xml": "пустая обёртка вокруг сгенерированной таблицы напитков",
}


def category(key):
    for c in CATEGORIES:
        if c[0] == key:
            return c
    raise SystemExit(f"нет категории '{key}'. Есть: {', '.join(c[0] for c in CATEGORIES)}")


def files_of(cat):
    """Развернуть пути категории в список конкретных файлов."""
    out = []
    for p in cat[2]:
        if os.path.isdir(p):
            for root, _, fs in os.walk(p):
                out += [os.path.join(root, f) for f in sorted(fs)]
        elif os.path.isfile(p):
            out.append(p)
        else:
            print(f"ВНИМАНИЕ: нет пути {p}", file=sys.stderr)
    return sorted(set(out))


def check_coverage():
    """Каждый файл Guidebook обязан быть в какой-то категории или в SKIPPED."""
    assigned = set()
    for c in CATEGORIES:
        assigned |= {f for f in files_of(c) if f.startswith(GUIDE)}

    everything = set()
    for root, _, fs in os.walk(GUIDE):
        everything |= {os.path.join(root, f) for f in fs}

    orphans = sorted(everything - assigned - set(SKIPPED))
    if orphans:
        print("СИРОТЫ — файлы вики, не попавшие ни в одну категорию:", file=sys.stderr)
        for o in orphans:
            print("  " + o, file=sys.stderr)
        return False
    return True


if __name__ == "__main__":
    ok = check_coverage()
    print(f"{len(CATEGORIES)} категорий")
    for c in CATEGORIES:
        fs = files_of(c)
        chars = sum(os.path.getsize(f) for f in fs)
        print(f"  {c[0]:12} {len(fs):4} файлов  {chars // 1024:5} КиБ  {c[3][:60]}")
    sys.exit(0 if ok else 1)
