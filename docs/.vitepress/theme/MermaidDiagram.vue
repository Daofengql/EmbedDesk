<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useData } from 'vitepress'

const props = defineProps<{ source: string }>()
const { isDark } = useData()
const drawing = ref('')
const error = ref(false)
let generation = 0
let disposed = false

async function render() {
  const current = ++generation
  error.value = false
  try {
    const { default: mermaid } = await import('mermaid')
    if (disposed || current !== generation) return
    mermaid.initialize({
      startOnLoad: false,
      securityLevel: 'strict',
      theme: isDark.value ? 'dark' : 'default',
      fontFamily: 'system-ui, "Microsoft YaHei", sans-serif',
      flowchart: { useMaxWidth: false }
    })
    const { svg } = await mermaid.render(`diagram-${crypto.randomUUID()}`, props.source)
    if (!disposed && current === generation) drawing.value = svg
  } catch {
    if (!disposed && current === generation) error.value = true
  }
}

onMounted(() => {
  render()
  watch([isDark, () => props.source], render)
})
onBeforeUnmount(() => { disposed = true; generation++ })
</script>

<template>
  <figure class="mermaid-diagram" aria-label="结构流程图">
    <div v-if="drawing && !error" v-html="drawing" />
    <pre v-else>{{ source }}</pre>
    <figcaption v-if="error">图表暂时无法显示，以上为流程定义。</figcaption>
    <figcaption v-else-if="drawing">宽图可横向滚动查看。</figcaption>
  </figure>
</template>
