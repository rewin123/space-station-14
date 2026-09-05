<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useAgent } from './stores/agent'
import { useSettings } from './stores/settings'
import BusView from './views/BusView.vue'
import ConversationView from './views/ConversationView.vue'
import MemoryView from './views/MemoryView.vue'
import NotesView from './views/NotesView.vue'
import SkillsView from './views/SkillsView.vue'
import PromptView from './views/PromptView.vue'
import StatsView from './views/StatsView.vue'
import SendBox from './components/SendBox.vue'

const settings = useSettings()
const agent = useAgent()

const TABS = [
  { id: 'conversation', title: 'Разговор' },
  { id: 'memory', title: 'Память' },
  { id: 'skills', title: 'Записи' },
  { id: 'notes', title: 'Люди' },
  { id: 'prompt', title: 'Промпт' },
  { id: 'stats', title: 'Статистика' },
  { id: 'bus', title: 'Шина' },
] as const

type TabId = (typeof TABS)[number]['id']
const tab = ref<TabId>('conversation')

// Connect right away: behind a reverse proxy the token is supplied by the proxy itself, and
// with a wrong token the loop stops terminally and says so — staying silent would be worse.
onMounted(() => agent.connect())

const SLICE_TEXT: Record<string, string> = {
  absent: 'не загружен',
  seeding: 'снимок…',
  live: 'на связи',
  ended: 'ушёл',
}

function chipTitle(row: { id: string; alive: boolean; turns: number; last_error: string | null }): string {
  const parts = [
    `${row.id}: ${SLICE_TEXT[agent.sliceState(row.id)]}`,
    row.alive ? 'тело живо' : 'ТЕЛО МЕРТВО',
    `ходов ${row.turns}`,
  ]

  if (row.last_error)
    parts.push(`ошибка: ${row.last_error}`)

  return parts.join(' · ')
}

const STATUS_TEXT: Record<string, string> = {
  idle: 'не подключено',
  connecting: 'снимок…',
  live: 'на связи',
  resyncing: 'пересинхронизация',
  retrying: 'повтор',
  unauthorized: 'неверный токен',
  broken: 'ошибка клиента',
}
</script>

<template>
  <div class="app">
    <header>
      <strong>Отладчик ИИ станции</strong>

      <input v-model="settings.baseUrl" size="22" spellcheck="false" />
      <input v-model="settings.token" type="password" placeholder="ai.debug_token" size="18" />

      <button @click="agent.connect()">Подключиться</button>
      <button :disabled="agent.status === 'idle'" @click="agent.disconnect()">Отключиться</button>

      <span class="status" :class="agent.status">
        {{ STATUS_TEXT[agent.status] ?? agent.status }}
        <template v-if="agent.statusDetail">— {{ agent.statusDetail }}</template>
      </span>

      <span class="cursor mono">
        <template v-if="agent.globals.round">раунд {{ agent.globals.round }} · </template>
        seq {{ agent.state.seq }} · {{ agent.state.instance || '—' }}
      </span>
    </header>

    <!--
      Brain switcher.

      The order is set by the SERVER (the core first, then alphabetically) — the roster is
      deliberately not re-sorted here: two independent sortings would drift apart, and the
      default tab would keep jumping.

      Frames for already-opened agents keep applying in the background, so switching back to
      them is instant. Unopened ones accumulate nothing: four histories of a hundred thousand
      tokens each in one tab would be tens of megabytes of strings.
    -->
    <div v-if="agent.roster.length" class="agents">
      <button
        v-for="row in agent.roster"
        :key="row.id"
        class="chip"
        :class="{ on: row.id === agent.selected, dead: !row.alive, bad: !!row.last_error }"
        :title="chipTitle(row)"
        @click="agent.select(row.id)"
      >
        <span class="dot" :class="agent.sliceState(row.id)" />
        {{ row.name }}
        <span class="id mono">{{ row.id }}</span>
        <span v-if="row.pending_input" class="mark">✉</span>
        <span v-if="row.last_error" class="mark">!</span>
      </button>

      <button
        v-if="agent.selected && agent.sliceState(agent.selected) !== 'absent'"
        class="chip unload"
        title="Убрать историю этого агента из памяти вкладки"
        @click="agent.unload(agent.selected)"
      >
        выгрузить
      </button>
    </div>

    <nav>
      <button
        v-for="t in TABS"
        :key="t.id"
        class="tab"
        :class="{ active: tab === t.id }"
        @click="tab = t.id"
      >
        {{ t.title }}
      </button>
    </nav>

    <main>
      <p v-if="agent.status === 'unauthorized'" class="hint bad">
        Сервер отверг токен. Он лежит в <code>ai_data/debug.token</code>, на сервере — в
        <code>ai.debug_token</code>. Повторять бессмысленно, петля остановлена.
      </p>
      <p v-else-if="agent.status === 'idle'" class="hint">
        Введите токен и нажмите «Подключиться».
      </p>

      <ConversationView v-show="tab === 'conversation'" />
      <MemoryView v-show="tab === 'memory'" />
      <SkillsView v-show="tab === 'skills'" />
      <NotesView v-show="tab === 'notes'" />
      <PromptView v-show="tab === 'prompt'" />
      <StatsView v-show="tab === 'stats'" />
      <BusView v-show="tab === 'bus'" />
    </main>

    <SendBox />
  </div>
</template>

<style scoped>
.app {
  display: flex;
  flex-direction: column;
  height: 100vh;
}

header {
  display: flex;
  gap: 10px;
  align-items: center;
  flex-wrap: wrap;
  padding: 10px 14px;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
}

.status {
  color: var(--dim);
}

.status.live {
  color: var(--assistant);
}

.status.unauthorized,
.status.broken {
  color: var(--bad);
}

.status.retrying,
.status.resyncing {
  color: var(--tool);
}

.cursor {
  margin-left: auto;
  color: var(--dim);
}

nav {
  display: flex;
  gap: 2px;
  padding: 0 14px;
  background: var(--panel);
  border-bottom: 1px solid var(--line);
}

.tab {
  border: none;
  border-bottom: 2px solid transparent;
  border-radius: 0;
  background: none;
  color: var(--dim);
  padding: 7px 14px;
}

.tab.active {
  color: var(--text);
  border-bottom-color: var(--user);
}

main {
  flex: 1;
  overflow: auto;
  padding: 14px;
}

.hint {
  color: var(--dim);
}

.hint.bad {
  color: var(--bad);
}

.agents {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 6px 10px;
  border-bottom: 1px solid var(--line);
}

.agents .chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 999px;
  background: transparent;
  color: inherit;
  cursor: pointer;
  font-size: 13px;
}

.agents .chip.on {
  border-color: var(--accent, #6ab);
  background: rgba(106, 187, 255, 0.12);
}

.agents .chip.dead { opacity: 0.55; }
.agents .chip.bad { border-color: #c55; }

.agents .chip .id { opacity: 0.6; font-size: 11px; }
.agents .chip .mark { color: #c55; }

.agents .chip .dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #666;
}

.agents .chip .dot.live { background: #6c6; }
.agents .chip .dot.seeding { background: #cc6; }
.agents .chip .dot.ended { background: #c66; }
.agents .chip.unload { opacity: 0.6; }
</style>
