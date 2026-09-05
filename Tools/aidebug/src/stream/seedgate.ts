import type { AgentEventFrame } from '../api/types'

/**
 * State of one "slice" — an agent or the process-level stores.
 *
 * - `absent` — no snapshot, and none requested. Frames are dropped.
 * - `seeding` — a snapshot is in flight. Frames accumulate in a buffer, not applied.
 * - `live` — a frame is applied if and only if `frame.seq > seededAt`.
 */
export type SeedPhase = 'absent' | 'seeding' | 'live'

/** How many frames we keep while a snapshot is in flight. The same as the server's ring. */
export const PENDING_LIMIT = 2048

/**
 * The seed gate: the one genuinely tricky spot in the client.
 *
 * <b>The problem.</b> There's one stream per process, but snapshots are downloaded one at a
 * time and at arbitrary moments. So between "an agent's snapshot was requested" and "the
 * snapshot arrived" the stream keeps moving, and we must decide what to do with that agent's
 * frames in the meantime.
 *
 * <b>The rule.</b> A snapshot that arrives at `seq = S` sets THIS slice's cursor to `S`; from
 * then on only frames with `frame.seq > S` are applied. The stream's shared cursor never moves
 * during a seed, and other slices' frames proceed as usual.
 *
 * <b>Why `> S` and not `>= S`.</b> The server reads `bus.Seq` FIRST (see the comment in
 * `AgentDebugState.CaptureGlobal`), so the guarantee is one-sided: everything with `seq <= S` is
 * guaranteed to be in the snapshot, while something with `seq > S` might have made it in too.
 * In other words, the rule can duplicate a frame from the capture window — and that's
 * acceptable: every kind except `message.appended` is idempotent, and that one is caught by the
 * `body_epoch`/`index` check, turning the duplicate into a reseed of ONE agent.
 *
 * <b>Why a buffer and not stopping the loop.</b> A snapshot can come back at an `S` smaller than
 * the stream's current cursor `P`: while it was in flight, the stream moved ahead. The frames in
 * the `(S, P]` range have already gone by at that point, and a slice that wasn't `live` dropped
 * them — meaning after landing at `S` it will NEVER see them, and the history freezes silently.
 * Stopping the loop for the duration of the snapshot fixes this, but at the cost of freezing the
 * stream for all four agents because of one slow snapshot. A buffer is the only option that
 * neither stalls nor fails to converge.
 *
 * <b>Buffer overflow isn't silent.</b> It resets the slice back to requesting a snapshot rather
 * than dropping frames: a lost middle section of history looks plausible and is therefore more
 * dangerous than a visible error.
 */
export class SeedGate {
  private phase: SeedPhase = 'absent'
  private seededAt = 0
  private pending: AgentEventFrame[] = []
  private overflowed = false

  get state(): SeedPhase {
    return this.phase
  }

  /** Whether this slice's snapshot has already been landed. */
  get seeded(): boolean {
    return this.phase === 'live'
  }

  /** Marks that a snapshot has been requested. Frames accumulate from this point on. */
  begin(): void {
    this.phase = 'seeding'
    this.pending = []
    this.overflowed = false
  }

  /** Returns the slice to its initial state: no snapshot, empty buffer, frames dropped again. */
  reset(): void {
    this.phase = 'absent'
    this.seededAt = 0
    this.pending = []
    this.overflowed = false
  }

  /**
   * Offers a frame.
   *
   * `apply` — apply it right now; `buffer` — deferred, nothing for the caller to do;
   * `drop` — the slice isn't seeded, this frame isn't ours.
   */
  offer(frame: AgentEventFrame): 'apply' | 'buffer' | 'drop' {
    if (this.phase === 'absent')
      return 'drop'

    if (this.phase === 'seeding') {
      if (this.pending.length >= PENDING_LIMIT) {
        // Not silently: the buffer is cleared, but the slice stays in seeding — the snapshot
        // will be re-requested.
        this.overflowed = true
        this.pending = []
        return 'buffer'
      }

      this.pending.push(frame)
      return 'buffer'
    }

    return frame.seq > this.seededAt ? 'apply' : 'drop'
  }

  /**
   * The snapshot arrived at `seq = S`.
   *
   * Returns the frames that need to be replayed, in ascending `seq` order. An empty array with
   * `overflowed = true` means "the buffer overflowed, the snapshot needs to be fetched again".
   */
  land(seq: number): { replay: AgentEventFrame[]; overflowed: boolean } {
    const overflowed = this.overflowed

    this.phase = 'live'
    this.seededAt = seq

    const replay = overflowed
      ? []
      : this.pending.filter((f) => f.seq > seq).sort((a, b) => a.seq - b.seq)

    this.pending = []
    this.overflowed = false

    return { replay, overflowed }
  }
}
