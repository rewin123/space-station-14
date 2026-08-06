<script setup lang="ts">
import { computed } from 'vue'

// Одно из двух: либо уже разобранное значение, либо сырая строка с провода. Второе — не то же
// самое, что первое: сырую строку нельзя нормализовать, иначе битый аргумент станет невидимым.
const props = defineProps<{ value?: unknown; raw?: string }>()

/**
 * Печатает JSON с отступами, а при неудаче отдаёт исходную строку как есть.
 *
 * Откат обязателен. Аргументы вызова инструмента приезжают сырой строкой ровно так, как их выдала
 * модель, и сервер намеренно их не нормализует: отладчик, показывающий причёсанный JSON, спрятал
 * бы именно тот битый аргумент, ради которого его и открыли.
 */
const text = computed(() => {
  if (props.raw !== undefined) {
    try {
      return JSON.stringify(JSON.parse(props.raw), null, 2)
    } catch {
      return props.raw
    }
  }

  try {
    return JSON.stringify(props.value, null, 2)
  } catch {
    return String(props.value)
  }
})

const malformed = computed(() => {
  if (props.raw === undefined) return false
  try {
    JSON.parse(props.raw)
    return false
  } catch {
    return true
  }
})
</script>

<template>
  <pre class="json" :class="{ malformed }">{{ text }}</pre>
</template>

<style scoped>
.json {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  color: var(--dim);
}

/* Не распарсилось — это находка, а не помеха: подсвечиваем, а не прячем. */
.malformed {
  color: var(--bad);
}
</style>
