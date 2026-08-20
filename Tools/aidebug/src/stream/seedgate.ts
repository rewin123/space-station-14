import type { AgentEventFrame } from '../api/types'

/**
 * Состояние одного «слайса» — агента или процессных хранилищ.
 *
 * - `absent` — снимка нет и не запрошен. Кадры отбрасываются.
 * - `seeding` — снимок в полёте. Кадры копятся в буфере, не применяются.
 * - `live` — кадр применяется тогда и только тогда, когда `frame.seq > seededAt`.
 */
export type SeedPhase = 'absent' | 'seeding' | 'live'

/** Сколько кадров держим, пока летит снимок. Столько же, сколько кольцо на сервере. */
export const PENDING_LIMIT = 2048

/**
 * Ворота досева: единственное по-настоящему хитрое место в клиенте.
 *
 * <b>Задача.</b> Лента одна на процесс, а снимки качаются поштучно и в произвольные моменты.
 * Значит, между «попросили снимок агента» и «снимок приехал» лента продолжает идти, и надо
 * решить, что делать с кадрами этого агента в промежутке.
 *
 * <b>Правило.</b> Снимок, приехавший на `seq = S`, ставит курсор ЭТОГО слайса в `S`; дальше
 * применяются только кадры с `frame.seq > S`. Общий курсор ленты при досеве не двигается никогда,
 * и кадры остальных слайсов идут своим чередом.
 *
 * <b>Почему `> S`, а не `>= S`.</b> Сервер читает `bus.Seq` ПЕРВЫМ (см. комментарий в
 * `AgentDebugState.CaptureGlobal`), поэтому гарантия односторонняя: всё с `seq <= S` в снимке
 * заведомо есть, а кое-что с `seq > S` могло попасть туда тоже. То есть правило может продублировать
 * кадр из окна съёмки — и это допустимо: все виды, кроме `message.appended`, идемпотентны, а он
 * ловится проверкой `body_epoch`/`index` и превращает дубль в пересев ОДНОГО агента.
 *
 * <b>Почему буфер, а не остановка петли.</b> Снимок может вернуться на `S`, меньшем текущего
 * курсора ленты `P`: пока он летел, лента ушла вперёд. Кадры из промежутка `(S, P]` к этому моменту
 * уже проехали, и слайс, не бывший `live`, их отбросил — то есть после посадки на `S` он не увидит
 * их НИКОГДА, и история застынет молча. Остановка петли на время снимка это лечит, но ценой
 * заморозки ленты всех четверых из-за одного медленного снимка. Буфер — единственный вариант,
 * который и не тормозит, и сходится.
 *
 * <b>Переполнение буфера не молчит.</b> Оно сбрасывает слайс обратно в запрос снимка, а не роняет
 * кадры: потерянная середина истории выглядит правдоподобно и потому опаснее видимой ошибки.
 */
export class SeedGate {
  private phase: SeedPhase = 'absent'
  private seededAt = 0
  private pending: AgentEventFrame[] = []
  private overflowed = false

  get state(): SeedPhase {
    return this.phase
  }

  /** Снимок этого слайса уже сажали. */
  get seeded(): boolean {
    return this.phase === 'live'
  }

  /** Пометить, что снимок запрошен. Кадры с этого момента копятся. */
  begin(): void {
    this.phase = 'seeding'
    this.pending = []
    this.overflowed = false
  }

  /** Вернуть слайс в исходное: снимка нет, буфер пуст, кадры снова отбрасываются. */
  reset(): void {
    this.phase = 'absent'
    this.seededAt = 0
    this.pending = []
    this.overflowed = false
  }

  /**
   * Предложить кадр.
   *
   * `apply` — применить прямо сейчас; `buffer` — отложен, вызывающему делать нечего;
   * `drop` — слайс не сеян, кадр не наш.
   */
  offer(frame: AgentEventFrame): 'apply' | 'buffer' | 'drop' {
    if (this.phase === 'absent')
      return 'drop'

    if (this.phase === 'seeding') {
      if (this.pending.length >= PENDING_LIMIT) {
        // Не молча: буфер очищается, а слайс остаётся в seeding — снимок будет перезапрошен.
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
   * Снимок приехал на `seq = S`.
   *
   * Возвращает кадры, которые надо доиграть по возрастанию `seq`. Пустой массив с
   * `overflowed = true` означает «буфер переполнялся, снимок надо взять заново».
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
