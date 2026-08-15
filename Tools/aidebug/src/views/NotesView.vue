<script setup lang="ts">
import { computed, ref } from 'vue'
import { useAgent } from '../stores/agent'

const agent = useAgent()

const selected = ref<string | null>(null)

const current = computed(() => agent.state.notes.find((n) => n.slug === selected.value) ?? null)

/** Так же, как считает стор: записи, склеенные разделителем. Иначе цифра разойдётся с той, что видит агент. */
const DELIMITER = '\n§\n'

function used(entries: string[]): number {
  return entries.join(DELIMITER).length
}
</script>

<template>
  <div class="notes">
    <aside>
      <p v-if="!agent.state.notes.length" class="dim">
        Заметок нет. Агент заводит их сам — по файлу на человека, и они переживают смены.
      </p>
      <button
        v-for="note in agent.state.notes"
        :key="note.slug"
        class="item"
        :class="{ active: note.slug === selected }"
        @click="selected = note.slug"
      >
        <span class="name">{{ note.name }}</span>
        <span class="meta mono">{{ note.slug }} · {{ note.entries.length }}</span>
      </button>
    </aside>

    <section v-if="current">
      <header>
        <h2>{{ current.name }}</h2>
        <span class="meta mono">
          {{ current.slug }}.md · {{ used(current.entries) }} / {{ agent.state.noteLimit }}
        </span>
      </header>

      <ol class="entries">
        <li v-for="(entry, i) in current.entries" :key="i">{{ entry }}</li>
      </ol>

      <p class="hint">
        Только чтение. В зону 0 заметки не вклеиваются вовсе, поэтому колонки «живое против
        замороженного», как у памяти, здесь нет и быть не может: агент узнаёт о заметке строкой
        <code>NOTE</code>, когда знакомый впервые за смену заговорил, и читает её инструментом
        <code>read_player_related_memory</code>. Штамп раунда в начале записи ставит хранилище, а не
        модель, — чтобы прошлая смена не выдавалась за сегодняшнюю.
      </p>
    </section>

    <section v-else class="dim pick">Выберите человека слева.</section>
  </div>
</template>

<style scoped>
.notes {
  display: grid;
  grid-template-columns: 260px 1fr;
  gap: 20px;
  align-items: start;
}

@media (max-width: 900px) {
  .notes {
    grid-template-columns: 1fr;
  }
}

aside {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.item {
  display: flex;
  flex-direction: column;
  gap: 2px;
  text-align: left;
  background: none;
  border: 1px solid transparent;
}

.item.active {
  background: var(--panel);
  border-color: var(--line);
}

.name {
  color: var(--text);
}

.meta {
  color: var(--dim);
  font-size: 11.5px;
}

header {
  display: flex;
  align-items: baseline;
  gap: 12px;
  margin-bottom: 10px;
}

h2 {
  margin: 0;
  font-size: 15px;
}

.entries {
  margin: 0;
  padding-left: 22px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-width: 90ch;
}

.entries li {
  line-height: 1.45;
}

.hint {
  color: var(--dim);
  font-size: 12px;
  max-width: 70ch;
  margin: 18px 0 0;
}

.dim {
  color: var(--dim);
}

.pick {
  padding-top: 20px;
}
</style>
