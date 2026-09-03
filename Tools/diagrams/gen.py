#!/usr/bin/env python3
"""Generate the SVG diagrams for Content.Server/AiAgent/README.md.

    python3 Tools/diagrams/gen.py            # writes Content.Server/AiAgent/diagrams/*.svg

Every diagram is hand-placed: coordinates are chosen for readability, not computed. When the
architecture changes, edit the function for that diagram and re-run. The kit is in svg.py.
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(__file__))
from svg import Svg, ACCENTS, MUTED  # noqa: E402

OUT = os.path.join(os.path.dirname(__file__), "..", "..", "Content.Server", "AiAgent", "diagrams")


# --------------------------------------------------------------------------- 1. overview
def overview():
    s = Svg(960, 770)

    # ---- game world
    s.lane(20, 20, 670, 100, "GAME WORLD", "grey", "RobustToolbox + Content.Server, unmodified")
    ecs = s.node(35, 45, 180, 56, "Entities & events", "doors · APCs · radio · mobs")
    vis = s.node(230, 45, 180, 56, "StationAiVisionSystem", "the camera view")
    nav = s.node(425, 45, 160, 56, "NavMapComponent", "the borg's route map")

    # ---- seam
    s.lane(20, 135, 670, 140, "THE SEAM", "blue")
    sas = s.node(35, 160, 290, 56, "StationAiAgentSystem",
                 "lifecycle · perception · tools · vision", accent="blue")
    borg = s.node(340, 160, 180, 56, "AiBorgSystem", "legs · eyes · hands · route", accent="blue")
    modes = s.node(535, 160, 140, 56, "Mode systems", "rogue · power · end", accent="blue")
    wit = s.node(35, 225, 215, 40, "Witness", "game events → OBSERVED")
    gate = s.node(262, 225, 278, 40, "DeviceGate",
                  "whitelist → wire → power → visible → access")
    con = s.node(552, 225, 123, 40, "Server console", "aiagent · aiborg")

    s.arrow(ecs.bottom(), sas.top(-55), label="game events", label_at=0.5, label_dx=8,
            label_anchor="start")
    s.arrow(vis.bottom(), (vis.cx, 160), label="tiles", label_dx=8, label_anchor="start")
    s.arrow(nav.bottom(), (nav.cx, 160), label="bitmap", label_dx=8, label_anchor="start")
    s.arrow(modes.top(), (modes.cx, 120), label="spawns · grants", label_dx=8,
            label_anchor="start", label_at=0.5)
    s.arrow(sas.right(), borg.left(), head=False)  # both build bodies; drawn as a tie
    s.arrow(sas.bottom(-100), wit.top(), head=False)

    # ---- world bus (narrow lane so the observation path visibly bypasses it)
    s.lane(20, 290, 480, 80, "WORLD BUS", "warm", "Threading/")
    bus = s.node(35, 320, 450, 40, "WorldBus",
                 "urgent + normal lanes · per-frame budget · generation check", accent="warm")

    s.arrow(bus.top(), (bus.cx, 275), label="`Pump()` inside `Update`, one slice per frame budget",
            label_at=0.5, label_dx=12, label_anchor="start")

    # ---- agent core
    s.lane(20, 385, 670, 200, "AGENT CORE", "green")
    tools = s.node(35, 410, 150, 70, "Tools", "AiToolRegistry", "ToolDispatcher", accent="green")
    turn = s.node(200, 410, 150, 70, "TurnRunner", "request → classify", "dispatch → settle",
                  accent="green")
    sess = s.node(365, 410, 150, 70, "AgentSession", "wake · turn · persist", "backoff · degraded",
                  accent="green")
    obs = s.node(525, 410, 150, 70, "ObservationQueue", "+ TimerStore", "caps per kind",
                 accent="green")
    body = s.node(35, 495, 150, 70, "AgentBody", "the body seam:", "eye · speak · tools")
    conv = s.node(200, 495, 150, 70, "ConversationState", "zones 0 · 1 · 2",
                  "frozen prefix hash")
    comp = s.node(365, 495, 150, 70, "Compactor", "8-step ritual", "Curator: self-review")
    vfs = s.node(530, 495, 145, 70, "Vfs · Lua scripts", "`/wiki_ru /skills`",
                 "`/players /memory.md`")

    s.arrow(tools.top(40), bus.bottom(-75), label="tool handlers marshal jobs", label_dx=12,
            label_anchor="start")
    s.arrow((obs.cx, 275), obs.top(), dashed=True, label="Observation",
            label_at=0.25, label_dx=10, label_anchor="start")
    s.text(obs.cx + 10, 350, "pushed from the", size=11)
    s.text(obs.cx + 10, 365, "main thread,", size=11)
    s.text(obs.cx + 10, 380, "no bus between", size=11)
    s.arrow(obs.left(), sess.right(), label="wakes", label_at=0.5, label_dy=-40)
    s.arrow(sess.left(), turn.right())
    s.arrow(turn.left(), tools.right())
    s.arrow(turn.bottom(), conv.top(), label="messages", label_dx=8, label_anchor="start",
            label_dy=4)
    s.arrow(sess.bottom(), comp.top(), label="compacts", label_dx=8, label_anchor="start",
            label_dy=4)
    s.arrow(comp.right(), vfs.left())
    s.arrow(tools.bottom(), body.top(), head=False, dashed=True)
    s.arrow(comp.left(), conv.right())

    # ---- llm layer
    s.lane(20, 600, 670, 75, "LLM LAYER", "purple", "Llm/")
    router = s.node(35, 625, 260, 40, "RoutingLlmClient", "chain · sticky · fallback · quota",
                    accent="purple")
    client = s.node(310, 625, 190, 40, "LlamaClient", "OpenAI-compatible · dialects")
    quota = s.node(515, 625, 160, 40, "LlmQuotaState", "`ai_data/llm_state.json`")
    s.arrow(conv.bottom(), router.top(75), label="`ChatAsync(messages, tools)`", label_at=0.5,
            label_dx=12, label_anchor="start")
    s.arrow(router.right(), client.left())
    s.arrow(client.right(), quota.left(), head=False, dashed=True)

    prov = s.node(35, 700, 640, 44, "Model providers",
                  "DeepSeek · OpenRouter · llama.cpp · vLLM · Grok bridge", accent="purple",
                  fill="#faf7ff")
    s.arrow(client.bottom(), (client.cx, 700), label="HTTP", label_dx=8, label_anchor="start")

    # ---- observability (right column)
    s.lane(710, 20, 230, 195, "OBSERVABILITY", "teal")
    evb = s.node(725, 45, 200, 40, "AgentEventBus", "ring buffer · (instance, seq)")
    dbg = s.node(725, 100, 200, 40, "AgentDebugServer", "`/state /session /events`")
    ui = s.node(725, 155, 200, 40, "Tools/aidebug", "Vue debugger")
    s.arrow(evb.bottom(), dbg.top())
    s.arrow(dbg.bottom(), ui.top())
    s.arrow((690, 405), (706, 405), (706, 65), evb.left(), dashed=True,
            label="events", label_at=0.45, label_dx=8, label_anchor="start")

    # ---- data (right column)
    s.lane(710, 385, 230, 200, "DATA", "warm", "ai_data/, git-ignored")
    data = s.node(725, 410, 200, 150, "ai_data/",
                  "SOUL.md · CURATOR.md", "wiki_ru/ shared, read-only",
                  "agents/<id>/: skills, people,", "memory, sessions, logs",
                  "config.d/*.yml overlay", "*.key · llm_state.json")
    s.arrow(vfs.right(), (700, vfs.cy), (700, data.cy), data.left(), tail=True)

    # ---- legend
    s.text(710, 610, "solid arrow — call or action", size=11)
    s.text(710, 626, "dashed arrow — observation or event", size=11)
    s.text(710, 642, "coloured bar — a layer's key class", size=11)

    return s.save(os.path.join(OUT, "overview.svg"))


# --------------------------------------------------------------------------- 2. lifecycle
def lifecycle():
    s = Svg(960, 360)
    cols = {"GT": 130, "SAS": 420, "S": 660}
    s.node(cols["GT"] - 70, 20, 140, 36, "GameTicker")
    s.node(cols["SAS"] - 100, 20, 200, 36, "StationAiAgentSystem")
    s.node(cols["S"] - 70, 20, 140, 36, "AgentSession")
    for x in cols.values():
        s.parts.append(
            f'<line x1="{x}" y1="56" x2="{x}" y2="345" stroke="#d0d7de" stroke-width="1.4" '
            f'stroke-dasharray="4 4"/>'
        )

    def msg(y, a, b, label):
        xa, xb = cols[a], cols[b]
        s.arrow((xa, y), (xb, y), label=label, label_at=0.5, label_dy=-6)

    def self_note(y, col, label, color=MUTED):
        x = cols[col]
        s.parts.append(f'<circle cx="{x}" cy="{y}" r="4" fill="{ACCENTS["blue"]}"/>')
        s.text(x + 12, y + 4, label, size=11.5, color=color)

    msg(85, "GT", "SAS", "StationPostInitEvent → close the StationAi job slot")
    msg(120, "GT", "SAS", "`RunLevel = InRound`")
    self_note(150, "SAS", "TryClaimAnyCore() — a station core, not CentComm's; retries for 30 s")
    self_note(175, "SAS", "spawn StationAiBrain · add LlmStationAiComponent · apply the mode's laws")
    msg(210, "SAS", "S", "`StartSession(BuildStationBody(brain))` — `ai.max_agents` checked here")
    self_note(240, "S", "freeze zone 0 · restore the snapshot")
    self_note(262, "S", "(only if prefix hash and round match)")
    self_note(284, "S", "loop starts on the thread pool")
    msg(312, "GT", "SAS", "RoundRestartCleanupEvent")
    msg(338, "SAS", "S", "Release() — cancel, never wait; disposed later in Update")
    return s.save(os.path.join(OUT, "lifecycle.svg"))


# --------------------------------------------------------------------------- 3. loop
def loop():
    s = Svg(960, 600)
    cx, w = 330, 360
    x = cx - w / 2
    a = s.node(x, 25, w, 56, "wait on Woken", "any observation wakes it · ceiling 5 s, idle 25 s")
    b = s.node(x, 105, w, 56, "force a turn?", "idle ≥ 6 · operator inbox · budget exhausted",
               accent="blue")
    c = s.node(x, 185, w, 72, "`BuildObservation(force)`", "on the main thread through the bus:",
               "drain queue · SELF line · law change · body hook")
    d = s.node(x, 281, w, 40, "`AppendUser`(observation + operator message)")
    e = s.node(x, 345, w, 56, "`TurnRunner.RunAsync`", "up to `ai.max_tool_calls_per_turn` steps")
    f = s.node(x, 425, w, 56, "compact?", "prompt ≥ `compactHigh` and no open tool call",
               accent="blue")
    g = s.node(x, 505, w, 40, "reset failures · `SaveSnapshot()`")

    for p, q in [(a, b), (b, c), (c, d), (d, e), (e, f)]:
        s.arrow(p.bottom(), q.top())
    s.arrow(f.bottom(), g.top(), label="no", label_dx=8, label_anchor="start", label_dy=4)
    # loop back on the left
    s.arrow(g.left(), (120, g.cy), (120, a.cy), a.left(), label="next turn", label_at=0.5,
            label_dx=-6, label_anchor="end")

    # idle branch
    idle = s.node(650, 205, 170, 40, "idleStreak++", "null · paused · disabled")
    s.arrow(c.right(), idle.left(), label="nothing to say", label_at=0.5, label_dy=-6)
    s.arrow(idle.right(), (845, idle.cy), (845, a.cy), a.right())

    # compaction branch
    comp = s.node(590, 433, 240, 40, "Compactor ritual", "mode = Review for the duration")
    s.arrow(f.right(), comp.left(), label="yes", label_at=0.4, label_dy=-6)
    s.arrow(comp.bottom(), (comp.cx, g.cy), g.right())

    # failure branch
    fail = s.node(590, 305, 350, 72, "exception", "ConsecutiveFailures++ · backoff 1 s · 2ⁿ, max 30 s",
                  "≥ 10 → degraded: retry every 60 s, never dies", accent="red")
    s.arrow(e.right(), (560, e.cy), (560, fail.cy), fail.left(), dashed=True)
    s.arrow(fail.right(), (945, fail.cy), (945, 12), (a.cx, 12), a.top(), dashed=True)

    s.note(530, 545, 420, [
        "Only the session's own cancellation exits the loop. A provider timeout is a",
        "TaskCanceledException, which inherits OperationCanceledException: catch it",
        "with when (ct.IsCancellationRequested) or the agent dies silently.",
    ], size=11)
    return s.save(os.path.join(OUT, "loop.svg"))


# --------------------------------------------------------------------------- 4. turn
def turn():
    s = Svg(960, 370)
    req = s.node(30, 130, 130, 48, "Request", "ChatAsync", accent="blue")
    cls = s.node(200, 130, 150, 48, "Classify", "tokens · cache metrics")
    disp = s.node(390, 40, 150, 56, "Dispatch", "every call through", "ToolDispatcher")
    steer = s.node(570, 40, 170, 56, "Steer", "queue drained into one", "NEW_EVENTS message")
    prose = s.node(390, 200, 150, 56, "Prose", "no tool calls")
    nudge = s.node(390, 290, 150, 48, "Nudge", "promised, did nothing")
    settle = s.node(570, 196, 185, 64, "Settle", "nothing owed → deliver text",
                    "or suppress a repeat")
    close = s.node(785, 130, 150, 48, "Close", "exit reason · delivery", accent="blue")

    s.arrow(req.right(), cls.left())
    s.arrow(cls.right(), (370, cls.cy), head=False)
    s.arrow((370, cls.cy), (370, disp.cy), disp.left(), label="tool calls",
            label_at=0.5, label_dx=-6, label_anchor="end")
    s.arrow((370, cls.cy), (370, prose.cy), prose.left(), label="text only",
            label_at=0.5, label_dx=-6, label_anchor="end")
    s.arrow(disp.right(), steer.left())
    s.arrow(steer.top(), (steer.cx, 18), (req.cx, 18), req.top(), label="next step",
            label_at=0.5, label_dy=-5)
    s.arrow(steer.right(), (770, steer.cy), (770, close.cy - 8), close.left(-8),
            label="noop or step budget", label_at=0.5, label_dx=6, label_anchor="start", label_dy=4)
    s.arrow(prose.right(), settle.left())
    s.arrow(prose.bottom(), nudge.top(), label="addressed but silent", label_dx=8,
            label_anchor="start", label_dy=4)
    s.arrow(nudge.bottom(), (nudge.cx, 355), (req.cx, 355), req.bottom(), label="once per turn",
            label_at=0.5, label_dy=-5)
    s.arrow(settle.right(), (770, settle.cy), (770, close.cy + 8), close.left(8))
    return s.save(os.path.join(OUT, "turn.svg"))


# --------------------------------------------------------------------------- 5. world bus
def worldbus():
    s = Svg(960, 360)
    s.lane(20, 20, 200, 320, "AGENT THREAD", "green")
    s.lane(240, 20, 280, 320, "WORLDBUS QUEUES", "warm")
    s.lane(540, 20, 400, 320, "MAIN THREAD", "blue", "Update() inside TickUpdate")

    h = s.node(35, 120, 170, 64, "tool handler", "`WorldBus.RunAsync`", "(job, priority)",
               accent="green")
    urg = s.node(255, 60, 250, 68, "urgent", "say · radio · announce · move_camera",
                 "device_* · timers · drain")
    nor = s.node(255, 145, 250, 52, "normal", "look · inspect · map · crew_status")
    res = s.node(255, 215, 250, 52, "resume", "unfinished chunked jobs come back")
    pump = s.node(555, 45, 370, 44, "Pump()", "deadline = now + ai.frame_budget_ms (3 ms)",
                  accent="blue")
    order = [
        "1  unfinished urgent",
        "2  unfinished normal",
        "3  aged normal (promotion)",
        "4  urgent",
        "5  normal",
    ]
    y = 100
    for line in order:
        s.node(555, y, 370, 26, line)
        y += 30
    sl = s.node(555, 262, 370, 64, "run one slice", "generation re-checked → StaleGenerationException",
                "Step(budget) — one slice always runs", accent="blue")

    s.arrow(h.right(-10), urg.left(10))
    s.arrow(h.right(10), nor.left())
    s.arrow(urg.right(), (530, urg.cy), (530, pump.cy), pump.left())
    s.arrow(nor.right(), (530, nor.cy), head=False)
    s.arrow(pump.bottom(), (pump.cx, 100))
    s.arrow((pump.cx, 250), sl.top(), head=True)
    s.arrow(sl.left(-20), (535, sl.cy - 20), (535, res.cy), res.right(), dashed=True)
    s.arrow(sl.bottom(), (sl.cx, 345), (h.cx, 345), h.bottom(),
            label="result delivered; continuations run asynchronously, never on the game thread",
            label_at=0.5, label_dy=-5)
    return s.save(os.path.join(OUT, "worldbus.svg"))


# --------------------------------------------------------------------------- 6. zones
def zones():
    s = Svg(960, 300)
    s.lane(20, 20, 920, 110, "ZONE 0", "blue", "frozen between compactions · hashed as PrefixHash")
    s.node(35, 48, 590, 68, "system prompt",
           "perception format · SELF fields · speech rules · error codes · glossary",
           "+ script-mode text · `SOUL.md` · memory snapshot · VFS root block")
    s.node(640, 48, 285, 68, "tool schemas", "sorted by name, canonical JSON,",
           "parsed once — never reflected")
    s.lane(20, 145, 920, 66, "ZONE 1", "green", "append-only body")
    s.text(35, 192, "user · assistant · tool messages — cuts only at a user message with no open tool call; "
                    "dangling calls get a synthetic turn_budget result", size=12)
    s.lane(20, 226, 920, 58, "ZONE 2", "warm", "volatile tail")
    s.text(35, 269, "at most one user message, always last, consumed by the turn that sends it", size=12)
    return s.save(os.path.join(OUT, "zones.svg"))


# --------------------------------------------------------------------------- 7. compaction
def compaction():
    s = Svg(960, 250)
    steps = [
        ("1  feasibility", "body has more than one message", True),
        ("2  announce", "announced in game first", False),
        ("3  curator", "self-review on a chain copy", False),
        ("4  summary", "asked with the same tools", False),
        ("5  fold", "summary + last N journal lines", True),
        ("6  report", "curator verdict into the body", False),
        ("7  prefix", "reload VFS, rebuild zone 0", True),
        ("8  commit", "counters, log line", True),
    ]
    nodes = []
    for i, (t, sub, fatal) in enumerate(steps):
        row, col = divmod(i, 4)
        x = 30 + col * 228
        y = 30 + row * 100
        nodes.append(s.node(x, y, 218, 60, t, sub, accent="red" if fatal else None))
    for i in range(3):
        s.arrow(nodes[i].right(), nodes[i + 1].left())
        s.arrow(nodes[4 + i].right(), nodes[5 + i].left())
    n4, n5 = nodes[3], nodes[4]
    s.arrow(n4.bottom(), (n4.cx, 110), (n5.cx, 110), n5.top())
    s.note(30, 215, 900, [
        "Red bar: a fatal step — failure aborts the ritual and zone 0 is rolled back byte for byte. "
        "Other steps are logged and skipped. Runs at a turn boundary only.",
    ])
    return s.save(os.path.join(OUT, "compaction.svg"))


# --------------------------------------------------------------------------- 8. script mode
def script():
    s = Svg(960, 320)
    model = s.node(20, 40, 150, 56, "model", "`script{code}`", accent="purple")
    lint = s.node(195, 40, 175, 56, "ScriptLint", "unknown function name →", "script_syntax, nothing ran")
    host = s.node(395, 32, 225, 72, "LuaHost (MoonSharp)", "HardSandbox + pcall + metatables",
                  "no io / os / require / load", accent="purple")
    rt = s.node(640, 40, 160, 56, "ScriptRuntime", "tools become functions", "goto_wait → go")
    disp = s.node(815, 40, 125, 56, "ToolDispatcher", "same gate,", "same errors")
    proc = s.node(395, 175, 225, 64, "ScriptProcess", "own thread · output cursor",
                  "call cap · wall-clock cap")
    obsn = s.node(640, 175, 180, 48, "Observation", "СКРИПТ #N wakes the loop")
    bus = s.node(830, 175, 110, 40, "WorldBus")

    s.arrow(model.right(), lint.left())
    s.arrow(lint.right(), host.left())
    s.arrow(host.right(), rt.left())
    s.arrow(rt.right(), disp.left())
    s.arrow(disp.bottom(), (disp.cx, bus.y))
    s.arrow(host.bottom(), proc.top(), label="runs as a coroutine, 20k-instruction slices",
            label_dx=10, label_anchor="start")
    s.arrow(proc.right(-8), obsn.left(-8), label="finished in the background", label_at=0.5, label_dy=52)
    s.arrow(proc.left(), (95, proc.cy), model.bottom(),
            label="done within ai.script_foreground_ms → result inline", label_at=0.5, label_dy=-6)
    s.note(20, 275, 900, [
        "A tool refusal is a Lua exception, so straight-line code reads top to bottom and tolerance is ordinary `pcall`.",
        "`help{tool='use'}` reads the registry directly: schemas are off the wire in this mode.",
    ])
    return s.save(os.path.join(OUT, "script.svg"))


# --------------------------------------------------------------------------- 9. providers
def providers():
    s = Svg(960, 330)
    head = s.node(40, 130, 170, 48, "Head", "first profile in ai.llm_chain")
    sticky = s.node(280, 122, 190, 64, "Sticky", "the last profile that worked;",
                    "every switch is a full prefill", accent="purple")
    nxt = s.node(580, 30, 170, 56, "Next profile", "timeout · 5xx · network", "→ short cooldown")
    quota = s.node(580, 122, 170, 56, "Quota sleep", "429 → until Retry-After", "or quota cooldown")
    dead = s.node(580, 214, 170, 56, "Dead", "401 / 403 / invalid_grant", "→ `aiagent llm revive`")
    inc = s.node(780, 122, 160, 56, "Incompatible", "400 / 404 / 422", "→ ERROR with body")

    s.arrow(head.right(), sticky.left(), label="success", label_dy=-6)
    s.arrow(sticky.top(60), (sticky.cx + 60, nxt.cy), nxt.left(), label="retryable",
            label_at=0.85, label_dy=-6)
    s.arrow(sticky.right(10), quota.left(10))
    s.arrow(sticky.right(20), (565, sticky.cy + 20), (565, dead.cy), dead.left())
    s.arrow(sticky.top(), (sticky.cx, 15), (inc.cx, 15), inc.top(), label="strict API rejects a field",
            label_at=0.5, label_dy=-4)
    s.arrow(nxt.bottom(), (nxt.cx, 108), (sticky.cx + 85, 108), sticky.top(85),
            label="next becomes sticky", label_at=0.5, label_dy=-6)
    s.arrow(sticky.bottom(), (sticky.cx, 250), (head.cx, 250), head.bottom(),
            label="every `ai.llm_recheck_seconds`: walk from the top", label_at=0.5, label_dy=-5)
    # self loop
    s.arrow(sticky.top(-55), (sticky.cx - 55, 100), (sticky.cx - 20, 100), sticky.top(-20),
            label="success: stay", label_at=0.5, label_dy=-4)
    s.note(40, 285, 900, [
        "Not a reason to switch: a reply truncated by `max_tokens` or malformed JSON in the arguments — the next provider reproduces both.",
        "One client per session, so one agent's fallback does not drag another off its provider. Quota state is shared and persisted.",
    ])
    return s.save(os.path.join(OUT, "providers.svg"))


# --------------------------------------------------------------------------- 10. borg
def borg():
    s = Svg(960, 300)
    g = s.node(20, 60, 150, 56, "goto target", "handle · room · x,y", accent="blue")
    pf = s.node(200, 52, 200, 72, "BorgPathfinder", "A* over NavMapComponent",
                "4-neighbour · no broadphase")
    legs = s.node(425, 60, 115, 56, "cut into legs")
    walk = s.node(565, 52, 225, 72, "Walk.cs, each tick", "direction → InputMoverComponent",
                  ".CurTickSprintMovement", accent="blue")
    door = s.node(810, 60, 140, 56, "door ahead?", "pressed, repeatedly", accent="warm")
    blk = s.node(200, 175, 340, 56, "still shut → tile marked impassable",
                 "replan · budget 10, reset on progress")
    arr = s.node(600, 175, 170, 48, "ARRIVED / NOPATH", "as an observation")

    s.arrow(g.right(), pf.left())
    s.arrow(pf.right(), legs.left())
    s.arrow(legs.right(), walk.left())
    s.arrow(walk.right(), door.left())
    s.arrow(door.top(), (door.cx, 28), (walk.cx, 28), walk.top(), label="opened, keep walking",
            label_at=0.5, label_dy=-5)
    s.arrow(door.bottom(), (door.cx, 252), (blk.cx, 252), blk.bottom(),
            label="not opened after retries", label_at=0.5, label_dy=-6)
    s.arrow(blk.top(pf.cx - blk.cx), pf.bottom(), label="new route", label_dx=8, label_anchor="start")
    s.arrow(walk.bottom(arr.cx - walk.cx), arr.top(), label="at the goal / no route", label_dx=8,
            label_anchor="start")
    s.note(20, 285, 900, [
        "Physics, collisions, speed and bumping doors open stay upstream: the fork only decides the direction for this tick.",
    ])
    return s.save(os.path.join(OUT, "borg.svg"))


if __name__ == "__main__":
    os.makedirs(OUT, exist_ok=True)
    problems = []
    for fn in (overview, lifecycle, loop, turn, worldbus, zones, compaction, script, providers, borg):
        problems += fn() or []
    for p in problems:
        print("PROBLEM", p)
    print(f"{len(problems)} problems")
    sys.exit(1 if problems else 0)
