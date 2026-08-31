using System.Collections.Generic;
using System.Linq;
using Content.Server.AiAgent.Skills;

namespace Content.Server.AiAgent.Bus;

/// <summary>
/// The state getter: one consistent picture of the agent, assembled once.
///
/// <para>
/// <b>It holds no two locks at once.</b> Each owner is asked in turn — conversation, memory,
/// skills — and each takes and releases its own lock, so the picture is assembled from adjacent
/// instants rather than one. That is a deliberate trade, and <see cref="CaptureGlobal"/> spells out why
/// the sequence number is read first to make the resulting skew safe. It also means the documented
/// <c>Conv → Memory → Skills → Bus</c> order costs nothing here: with one lock held at a time there
/// is no cycle to build.
/// </para>
/// <para>
/// Called from an HTTP thread, never the main thread. It touches no entity, only the three data
/// owners, all of which are thread-safe by construction — deliberately, because the main thread's
/// <c>_sessions</c> dictionary is not, and an HTTP thread that went looking for a session there
/// could land on a resize and spin forever inside a bucket chain.
/// </para>
/// </summary>
public static class AgentDebugState
{
    /// <summary>
    /// Процессный снимок: память, записи, заметки и ростер.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Разделение снимка на процессный и агентный — не косметика: системный промпт и переписка
    /// принадлежат одному агенту и весят мегабайты, а этот снимок мал. Слитый воедино ответ
    /// заставлял бы качать четыре истории, чтобы посмотреть на одну.
    /// </para>
    /// <para>
    /// <b>Отдаётся библиотека ЯДРА.</b> Раньше она была одна на процесс и слово «процессный» было
    /// правдой; теперь у каждого тела своя, и здесь показано тело ядра — то, ради которого этот
    /// отладчик и открывают. Библиотеки боргов лежат в <c>ai_data/agents/&lt;id&gt;/</c>; довести их
    /// до вкладки — отдельная работа, и до неё честнее показывать одну правдиво, чем четыре
    /// вперемешку.
    /// </para>
    /// </remarks>
    public static AgentStateSnapshot CaptureGlobal(
        AgentEventBus bus,
        AgentDirectory directory,
        Vfs.Vfs? vfs,
        int roundId)
    {
        // The sequence number is read FIRST, and that single line is what makes this safe.
        //
        // This capture is NOT atomic: each owner is asked separately, so a change can land between
        // two of the reads below. The only question is which way that failure points, and the order
        // of this one line decides it.
        //
        //   seq last  → the change is counted in seq but missing from the data, so the client
        //               never receives it and never learns it exists. A LOST UPDATE.
        //   seq first → the change is in the data and arrives again in the stream, so the client
        //               applies it twice. A DUPLICATE.
        //
        // A duplicate is harmless here because every event carries the whole new value rather than
        // a delta: memory.updated, skill.updated, skills.reloaded, history.replaced, prefix.replaced
        // and stats are all idempotent on replay. The single exception is message.appended, and the
        // client checks `index == messages.length` against it — a mismatch is a resync, not silence.
        //
        // Holding all four locks nested in the documented Conv → Memory → Skills → Bus order would
        // make it genuinely atomic, and CaptureUnderConcurrentPublishDoesNotDeadlock already guards
        // that order. It is not worth the deadlock surface for a debug endpoint when reordering one
        // line converts the failure into one the client already detects.
        var instance = bus.Instance;
        var seq = bus.Seq;

        var memory = vfs?.Memory;
        var notes = vfs?.Notes;

        var memoryDto = memory == null
            ? new AgentMemoryDto(System.Array.Empty<string>(), string.Empty, 0)
            : new AgentMemoryDto(memory.Entries(), memory.Snapshot(), memory.MemoryLimit);

        // Имя записи теперь путь внутри /skills («питание/смес»), а не плоское имя. Проводной
        // формат при этом прежний, поэтому клиент отладчика продолжает работать без правок.
        var skillDtos = (vfs?.Skills?.All ?? System.Array.Empty<Skill>())
            .Select(s => new AgentSkillDto(s.Name, s.When, s.Body))
            .ToList();

        // Порядок задаёт стор (по слагу, ординально), а не эта строка: тот же порядок уезжает в
        // notes.reloaded, и клиент, применяющий снимок и поток вперемешку, не переставляет список
        // под читателем.
        var noteDtos = (notes?.All ?? System.Array.Empty<PlayerNote>())
            .Select(n => new AgentPlayerNoteDto(n.Slug, n.Name, n.Entries))
            .ToList();

        return new AgentStateSnapshot(
            instance, seq, roundId, directory.Roster(), memoryDto, skillDtos, noteDtos,
            notes?.NoteLimit ?? 0);
    }

    /// <summary>
    /// Снимок одного агента. Тот же порядок «сначала seq», по той же причине.
    /// </summary>
    public static AgentSessionSnapshot CaptureAgent(AgentEventBus bus, AgentHandle? handle)
    {
        var instance = bus.Instance;
        var seq = bus.Seq;

        return new AgentSessionSnapshot(instance, seq, handle?.Capture());
    }

    public static AgentSessionDto CaptureSession(AgentSession session, string sessionId, int roundId)
    {
        var conv = session.Conv;
        var body = conv.Snapshot();

        var messages = new AgentMessageDto[body.Count];
        for (var i = 0; i < body.Count; i++)
            messages[i] = AgentMessageDto.From(i, body[i]);

        return new AgentSessionDto(
            sessionId,
            (int)session.Brain,
            roundId,
            conv.PrefixHash,
            conv.SystemPrompt,
            conv.ToolsJson,
            conv.BodyEpoch,
            messages,
            Files(session.Body.Vfs),
            Stats(session),
            LastTurn(session));
    }

    /// <summary>
    /// Дерево файлов агента на два уровня: корень и содержимое каждой папки верхнего уровня.
    /// </summary>
    /// <remarks>
    /// Глубина ограничена намеренно. Полное дерево справочника — это 226 строк, то есть ровно тот
    /// список, ради избавления от которого всё и переделывалось; отдавать его в каждом снимке
    /// значило бы вернуть болезнь с другой стороны. Кто хочет глубже — раскрывает папку отдельным
    /// запросом.
    /// </remarks>
    private static IReadOnlyList<AgentFileDto> Files(Vfs.Vfs vfs)
    {
        var files = new List<AgentFileDto>();

        foreach (var mount in vfs.Mounts)
        {
            var access = mount.Writable ? "rw-" : "r--";
            files.Add(new AgentFileDto("/" + mount.Point, !mount.IsFile, mount.Description, 0, access));

            if (mount.IsFile)
                continue;

            foreach (var entry in mount.List(Vfs.VfsPath.Root, out var error))
            {
                if (error.Length > 0)
                    break;

                files.Add(new AgentFileDto(
                    $"/{mount.Point}/{entry.Name}", entry.IsDir, entry.Desc, entry.Size, access));
            }
        }

        return files;
    }

    /// <summary>
    /// Строка ростера. Дороже всего здесь <c>BodyCount</c>, и он берёт замок, но не копирует тело.
    /// </summary>
    public static AgentRosterEntryDto Roster(AgentSession session, string id, string name, int roundId, long startedSeq)
    {
        var conv = session.Conv;
        var stats = Stats(session);

        return new AgentRosterEntryDto(
            id,
            name,
            (int)session.Brain,
            roundId,
            startedSeq,
            // Живость проставляет владелец хендла: считать её здесь значило бы трогать мир с
            // чужого потока. См. AgentHandle.Alive.
            true,
            stats.Mode,
            stats.Turns,
            conv.BodyCount,
            conv.BodyEpoch,
            stats.LastPromptTokens,
            stats.ContextLimit,
            stats.QueueDepth,
            session.Inbox.HasPending,
            stats.LastError);
    }

    /// <summary>
    /// The whole statistics record.
    ///
    /// Sampled rather than diffed per counter — see <see cref="AgentEventBus"/>. The same builder
    /// serves the snapshot and the periodic <see cref="AgentEventKind.Stats"/> event, so the two
    /// can never disagree about what a field means.
    /// </summary>
    public static AgentStatsDto Stats(AgentSession session)
    {
        var conv = session.Conv;

        return new AgentStatsDto(
            session.Turns,
            conv.TurnCount,
            session.UntooledReplies,
            session.State.IdleTurns,
            session.ConsecutiveFailures,
            session.State.BrokenPromises,
            session.State.Compactions,
            conv.LastPromptTokens,
            conv.CharsPerToken,
            conv.BodyChars(),
            session.ContextLimit,
            session.Cache.LastRatio,
            session.Cache.MeanRatio,
            session.Cache.Alarms,
            session.Queue.Count,
            session.Mode.ToString(),
            session.LastError,
            conv.VolatileTail);
    }

    private static AgentTurnDto? LastTurn(AgentSession session)
    {
        var turn = session.LastTurn;
        if (turn == null)
            return null;

        return new AgentTurnDto(
            turn.Index,
            turn.Phase.ToString(),
            turn.Step,
            turn.ToolCalls,
            turn.Spoke,
            turn.Nudged,
            turn.Promised,
            turn.Exit.ToString(),
            turn.Delivery.ToString(),
            turn.LastCacheRatio,
            turn.Perception.RadioChannel,
            turn.Perception.Addressed,
            turn.Perception.Forced,
            turn.Perception.Text);
    }
}
