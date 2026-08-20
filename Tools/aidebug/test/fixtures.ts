import type {
  AgentEventFrame,
  AgentSession,
  AgentStateSnapshot,
  AgentStats,
} from '../src/api/types'

/** Общие заготовки для тестов клиента: один источник формы данных на все три файла. */

export function stats(turn: number, extra: Partial<AgentStats> = {}): AgentStats {
  return {
    turns: turn,
    conv_turns: turn,
    untooled_replies: 0,
    idle_turns: 0,
    consecutive_failures: 0,
    broken_promises: 0,
    compactions: 0,
    last_prompt_tokens: 9000,
    chars_per_token: 3,
    body_chars: 400,
    context_limit: 0,
    cache_last_ratio: 0.98,
    cache_mean_ratio: 0.98,
    cache_alarms: 0,
    queue_depth: 0,
    mode: 'Core',
    last_error: null,
    volatile_tail: null,
    ...extra,
  }
}

export function session(id = 'core', overrides: Partial<AgentSession> = {}): AgentSession {
  return {
    id,
    brain: 50611,
    round: 22,
    prefix_hash: 'HASH1',
    system_prompt: 'ПРОМПТ ' + id,
    tools_json: '[]',
    body_epoch: 0,
    messages: [{ index: 0, role: 'user', content: 'наблюдение', tool_calls: null, tool_call_id: null }],
    stats: stats(1),
    last_turn: null,
    ...overrides,
  }
}

export function roster(...ids: string[]) {
  return ids.map((id) => ({
    id,
    name: id,
    brain: 1,
    round: 22,
    started_seq: 0,
    alive: true,
    mode: 'Core',
    turns: 1,
    messages: 1,
    body_epoch: 0,
    last_prompt_tokens: 0,
    context_limit: 0,
    queue_depth: 0,
    pending_input: false,
    last_error: null,
  }))
}

export function snapshot(overrides: Partial<AgentStateSnapshot> = {}): AgentStateSnapshot {
  return {
    instance: 'proc-1',
    seq: 10,
    round: 22,
    agents: roster('core'),
    memory: {
      memory_live: ['запись'],
      memory_frozen: 'заморожено',
      memory_limit: 4000,
    },
    skills: [{ name: 'alpha', when: 'когда', body: 'тело' }],
    notes: [{ slug: 'autumn-treeby', name: 'Autumn Treeby', entries: ['[раунд 7] ей нельзя кофе'] }],
    note_limit: 2000,
    ...overrides,
  }
}

export function frame(
  seq: number,
  type: AgentEventFrame['type'],
  payload: unknown,
  agent = 'core',
): AgentEventFrame {
  return { seq, type, session: agent, payload }
}
