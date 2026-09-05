<script setup lang="ts">
import { computed } from 'vue'

// One of two things: either an already-parsed value, or a raw string off the wire. The second
// isn't the same as the first: the raw string can't be normalized, or a malformed argument would
// become invisible.
const props = defineProps<{ value?: unknown; raw?: string }>()

/**
 * Prints JSON with indentation, and on failure returns the original string as-is.
 *
 * The fallback is mandatory. Tool call arguments arrive as a raw string exactly as the model
 * produced it, and the server deliberately doesn't normalize them: a debugger that shows
 * prettified JSON would hide exactly the malformed argument it was opened to find.
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

/* Failing to parse is a finding, not a nuisance: highlight it, don't hide it. */
.malformed {
  color: var(--bad);
}
</style>
