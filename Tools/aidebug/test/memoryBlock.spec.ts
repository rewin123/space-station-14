import { describe, expect, it } from 'vitest'
import { parseFrozen, pendingEntries, usedChars } from '../src/lib/memoryBlock'

/** Ровно то, что печатает MemoryStore.RenderBlock. */
function block(title: string, entries: string[]): string {
  const bar = '═'.repeat(46)
  return `${bar}\n${title}\n${bar}\n${entries.join('\n§\n')}`
}

describe('parseFrozen', () => {
  it('отделяет шапку от записей', () => {
    const text = block('ПАМЯТЬ (заметки) [5% — 230/4000 символов]', ['первая', 'вторая'])
    const parsed = parseFrozen(text)

    expect(parsed.header).toBe('ПАМЯТЬ (заметки) [5% — 230/4000 символов]')
    expect(parsed.entries).toEqual(['первая', 'вторая'])
  })

  it('пустой блок — не ошибка', () => {
    expect(parseFrozen('')).toEqual({ header: '', entries: [] })
    expect(parseFrozen('   ')).toEqual({ header: '', entries: [] })
  })

  it('запись с одинокой § не разрезается', () => {
    // Разделитель — именно "\n§\n", а не сама §: запись имеет полное право её содержать.
    const text = block('ПАМЯТЬ [0%]', ['параграф § 12 устава', 'вторая'])
    expect(parseFrozen(text).entries).toEqual(['параграф § 12 устава', 'вторая'])
  })

  it('многострочная запись остаётся целой', () => {
    const text = block('ПАМЯТЬ [0%]', ['строка один\nстрока два', 'вторая'])
    expect(parseFrozen(text).entries).toEqual(['строка один\nстрока два', 'вторая'])
  })

  it('текст без шапки отдаётся целиком, а не режется наугад', () => {
    const parsed = parseFrozen('просто запись\n§\nвторая')
    expect(parsed.header).toBe('')
    expect(parsed.entries).toEqual(['просто запись', 'вторая'])
  })
})

describe('pendingEntries', () => {
  it('находит записи, которых модель ещё не видит', () => {
    // Весь смысл экрана: правка легла на диск, но в системный промпт попадёт только со следующей
    // перестройкой префикса.
    const pending = pendingEntries(['старая', 'свежая'], ['старая'])
    expect([...pending]).toEqual(['свежая'])
  })

  it('после перестройки префикса ожидающих не остаётся', () => {
    expect(pendingEntries(['одна', 'две'], ['одна', 'две']).size).toBe(0)
  })
})

describe('usedChars', () => {
  it('считает той же меркой, что и сервер — с разделителями', () => {
    // "аб" + "\n§\n" + "вг" = 2 + 3 + 2
    expect(usedChars(['аб', 'вг'])).toBe(7)
    expect(usedChars([])).toBe(0)
  })
})
