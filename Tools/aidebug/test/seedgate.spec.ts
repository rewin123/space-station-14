import { describe, expect, it } from 'vitest'
import { PENDING_LIMIT, SeedGate } from '../src/stream/seedgate'
import { frame } from './fixtures'

/**
 * Ворота досева — единственное по-настоящему хитрое место в клиенте, и потому единственное,
 * которое тестируется отдельно от всего остального.
 *
 * Проверяется правило: снимок на `seq = S` ставит курсор слайса в `S`, дальше применяются кадры
 * с `seq > S`, а всё, что накопилось, пока снимок летел, доигрывается по возрастанию.
 */

function f(seq: number) {
  return frame(seq, 'stats', {})
}

describe('SeedGate', () => {
  it('до запроса снимка кадры отбрасываются', () => {
    const gate = new SeedGate()
    expect(gate.offer(f(1))).toBe('drop')
  })

  it('снимок отстал от курсора: накопленное доигрывается', () => {
    // Единственный кейс, ради которого буфер и существует.
    //
    // Пока летел снимок, лента ушла с 10 до 14. Кадры 11..14 к этому моменту уже проехали мимо, и
    // слайс, не бывший live, отбросил бы их навсегда — история застыла бы молча, без единого
    // признака поломки.
    const gate = new SeedGate()
    gate.begin()

    for (const seq of [11, 12, 13, 14])
      expect(gate.offer(f(seq))).toBe('buffer')

    const { replay, overflowed } = gate.land(10)

    expect(overflowed).toBe(false)
    expect(replay.map((x) => x.seq)).toEqual([11, 12, 13, 14])
  })

  it('снимок обогнал курсор: кадры до S отбрасываются', () => {
    // Обратная сторона того же правила. Всё с seq <= S в снимке заведомо есть — доигрывать это
    // значило бы задваивать.
    const gate = new SeedGate()
    gate.begin()

    for (const seq of [11, 12, 13])
      gate.offer(f(seq))

    const { replay } = gate.land(12)

    expect(replay.map((x) => x.seq)).toEqual([13])
  })

  it('S равен курсору: ничего не потеряно и ничего не задвоено', () => {
    const gate = new SeedGate()
    gate.begin()
    gate.offer(f(10))
    gate.offer(f(11))

    const { replay } = gate.land(10)

    expect(replay.map((x) => x.seq)).toEqual([11])
  })

  it('после посадки применяется только то, что строго больше S', () => {
    const gate = new SeedGate()
    gate.begin()
    gate.land(10)

    expect(gate.offer(f(10))).toBe('drop')
    expect(gate.offer(f(11))).toBe('apply')
  })

  it('доигранное вперемешку возвращается по возрастанию seq', () => {
    // Порядок в ленте гарантирован, но буфер тестируется как самостоятельная вещь: применять
    // кадры не по порядку значит получить разъезд индексов там, где его не было.
    const gate = new SeedGate()
    gate.begin()

    for (const seq of [14, 11, 13, 12])
      gate.offer(f(seq))

    const { replay } = gate.land(10)

    expect(replay.map((x) => x.seq)).toEqual([11, 12, 13, 14])
  })

  it('переполнение буфера не теряет молча', () => {
    // Потерянная середина истории выглядит правдоподобно и потому опаснее видимой ошибки: слайс
    // обязан сказать «снимок надо взять заново», а не тихо продолжить с дырой.
    const gate = new SeedGate()
    gate.begin()

    for (let seq = 1; seq <= PENDING_LIMIT + 1; seq++)
      gate.offer(f(seq))

    const { replay, overflowed } = gate.land(0)

    expect(overflowed).toBe(true)
    expect(replay).toEqual([])
  })

  it('сброс возвращает слайс в исходное', () => {
    const gate = new SeedGate()
    gate.begin()
    gate.land(10)
    expect(gate.seeded).toBe(true)

    gate.reset()

    expect(gate.seeded).toBe(false)
    expect(gate.state).toBe('absent')
    expect(gate.offer(f(11))).toBe('drop')
  })
})
