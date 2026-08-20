<script setup lang="ts">
import { useAgent } from '../stores/agent'

/**
 * Показывать нечего — и причин этому четыре разных, а не одна.
 *
 * Между раундами тела никем не заняты; выбранный агент может быть ещё не загружен (истории всех
 * мозгов сразу не качаются намеренно); снимок может быть в полёте; агент мог уйти. Все четыре —
 * штатные состояния, и различать их надо: «не загружен» лечится кликом, «ушёл» — нет.
 *
 * Память и скиллы при любом из них продолжают работать: они принадлежат процессу, а не агенту.
 */
const agent = useAgent()
</script>

<template>
  <div class="none">
    <p v-if="agent.status === 'idle'">Не подключено.</p>
    <p v-else-if="agent.loading">Снимок агента в пути…</p>
    <p v-else-if="agent.selected && agent.sliceState(agent.selected) === 'ended'">
      Агент «{{ agent.selected }}» ушёл. Разговор остался, новые кадры к нему не применяются.
    </p>
    <p v-else-if="agent.selected && agent.sliceState(agent.selected) === 'absent'">
      Агент «{{ agent.selected }}» не загружен — выбери его в шапке.
    </p>
    <template v-else>
      <p>Тела никем не заняты.</p>
      <p class="dim">
        Между раундами это нормально. Память и скиллы на своих вкладках работают: они принадлежат
        процессу, а не агенту, и переживают раунд.
      </p>
    </template>
  </div>
</template>

<style scoped>
.none {
  color: var(--text);
  padding: 20px 0;
}

.dim {
  color: var(--dim);
  max-width: 60ch;
}
</style>
