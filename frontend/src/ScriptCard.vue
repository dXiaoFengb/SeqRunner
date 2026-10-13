<script setup lang="ts">
import type { Queue, Task } from './types'

defineProps<{ script: Queue; tasks: Task[]; expanded: boolean; locked: boolean; blocked: boolean; running: boolean; presetSummary: string }>()
const emit = defineEmits<{ edit: []; run: []; duplicate: []; remove: []; toggle: []; dragstart: [event: DragEvent]; drop: [event: DragEvent]; move: [direction: -1 | 1] }>()
</script>

<template>
  <article class="script-card" :class="{ expanded, running }" :draggable="!locked" @dragstart="emit('dragstart', $event)" @dragover.prevent @drop="emit('drop', $event)">
    <div class="script-summary">
      <span class="grab" aria-hidden="true">⠿</span>
      <div class="script-title"><b>{{ script.name }}</b><small>{{ tasks.length }} 个程序 · {{ presetSummary }}</small></div>
      <span v-if="running" class="script-running">运行中</span><span v-else-if="blocked" class="script-incomplete">待完成配置</span><span v-else class="script-ready">可运行</span>
      <div class="card-actions"><button :disabled="locked" @click="emit('edit')">编辑脚本</button><button class="run-button" :disabled="locked || blocked || !tasks.length" @click="emit('run')">运行</button><details class="more"><summary>更多</summary><button :disabled="locked" @click="emit('duplicate')">复制脚本</button><button class="textdanger" :disabled="locked" @click="emit('remove')">删除脚本</button></details><button class="chevron" :aria-label="expanded ? '收起脚本' : '展开脚本'" @click="emit('toggle')">{{ expanded ? '⌃' : '⌄' }}</button></div>
    </div>
    <div class="script-order" aria-label="脚本顺序"><button :disabled="locked" @click="emit('move', -1)">上移</button><button :disabled="locked" @click="emit('move', 1)">下移</button><small>此顺序仅用于“运行全部脚本”。</small></div>
    <div v-if="expanded" class="script-editor"><slot /></div>
  </article>
</template>
