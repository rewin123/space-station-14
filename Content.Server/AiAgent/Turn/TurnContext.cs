using Content.Server.AiAgent.Perception;

namespace Content.Server.AiAgent.Turn;

/// <summary>The named nodes of one turn. Exactly one is current at any moment.</summary>
public enum TurnPhase : byte
{
    Open, Step, Request, Classify, Dispatch, Prose, Nudge, Settle, Recover, Close, Done,
}

/// <summary>Why the step loop stopped. Orthogonal to <see cref="TurnDelivery"/>.</summary>
public enum TurnExit : byte
{
    /// <summary>The model answered without calling anything, so the turn is over.</summary>
    ModelStopped,

    /// <summary>Ran out of steps while still calling tools.</summary>
    BudgetExhausted,

    /// <summary>Shutdown, carding or death arrived mid-turn.</summary>
    Cancelled,

    /// <summary>The model client threw.</summary>
    Failed,
}

/// <summary>How the crew was served. Orthogonal to <see cref="TurnExit"/>.</summary>
public enum TurnDelivery : byte
{
    /// <summary>Nobody was owed a reply.</summary>
    NothingOwed,

    /// <summary>Answered properly, through say/radio/announce.</summary>
    SpokeByTool,

    /// <summary>Prose after the nudge; we put it on the air ourselves.</summary>
    Delivered,

    /// <summary>Same, but delivery was switched off, dry-run, or the AI had left play.</summary>
    DeliveryDeclined,

    /// <summary>Prose was owed but identical to something just said.</summary>
    SuppressedRepeat,

    /// <summary>The turn did not finish.</summary>
    Abandoned,
}

/// <summary>
/// The state of one turn, as a value with named transitions.
///
/// It replaces eight loose variables spread across three scopes — <c>addressed</c>, <c>spoke</c>,
/// <c>nudged</c>, <c>undelivered</c>, <c>step</c>, and three session fields — and, more usefully,
/// it gives the turn's possible endings names. There were at least six of them and not one had one,
/// so "what can a turn do" could only be answered by simulating the method in your head. Now the
/// pairs of <see cref="Exit"/> and <see cref="Delivery"/> enumerate it.
///
/// <see cref="UnheardProse"/> carries exactly one meaning. Its predecessor carried three at once —
/// "text that may need delivering", "flag: we owe a delivery", and "null: this step used tools" —
/// which is why the delivery logic could only be read by tracing every assignment.
/// </summary>
public sealed class TurnContext
{
    public TurnContext(int index, TurnPerception perception, int maxSteps)
    {
        Index = index;
        Perception = perception;
        MaxSteps = maxSteps;
    }

    public int Index { get; }
    public TurnPerception Perception { get; }
    public int MaxSteps { get; }

    public TurnPhase Phase { get; private set; } = TurnPhase.Open;
    public int Step { get; private set; }
    public int ToolCalls { get; private set; }

    /// <summary>A say/radio/announce landed, so trailing prose is tidying up, not an unspoken reply.</summary>
    public bool Spoke { get; private set; }

    public bool Nudged { get; private set; }

    /// <summary>The last thing it told the crew it was about to do, until it does it.</summary>
    public string? Promised { get; private set; }

    /// <summary>Whether it has already been reminded about an unkept promise this turn.</summary>
    public bool NudgedPromise { get; private set; }

    /// <summary>Said it would act on the station, then finished the turn without acting.</summary>
    public bool HasUnkeptPromise => Promised != null;

    public void MarkPromised(string? spoken)
    {
        if (SpokenIntent.PromisesAction(spoken))
            Promised = spoken;
    }

    /// <summary>A game action landed, so whatever was promised is no longer outstanding.</summary>
    public void MarkActed() => Promised = null;

    public void MarkPromiseNudged() => NudgedPromise = true;

    /// <summary>Prose the crew has not heard and this turn owes them, or null.</summary>
    public string? UnheardProse { get; private set; }

    public TurnExit Exit { get; private set; } = TurnExit.ModelStopped;
    public TurnDelivery Delivery { get; private set; } = TurnDelivery.NothingOwed;
    public double LastCacheRatio { get; private set; }

    public void Enter(TurnPhase phase) => Phase = phase;

    /// <summary>Advance to the next step, or report that the budget is gone.</summary>
    public bool TryAdvanceStep()
    {
        if (Step + 1 >= MaxSteps)
            return false;

        Step++;
        return true;
    }

    public void RecordResponse(double cacheRatio, int toolCalls)
    {
        LastCacheRatio = cacheRatio;
        ToolCalls += toolCalls;
    }

    public void MarkSpoke() => Spoke = true;

    public void HoldProse(string? prose) => UnheardProse = prose;

    public void MarkNudged(string prose)
    {
        Nudged = true;
        UnheardProse = prose;
    }

    public void Finish(TurnExit exit, TurnDelivery delivery)
    {
        Exit = exit;
        Delivery = delivery;
    }
}
