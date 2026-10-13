<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref } from 'vue'
import type { Queue, Task } from './types'

const props = defineProps<{ script: Queue; tasks: Task[]; locked: boolean; blocked: boolean; running: boolean; presetSummary: string }>()
const emit = defineEmits<{ edit: []; run: []; duplicate: []; remove: []; dragstart: [event: DragEvent]; drop: [event: DragEvent]; move: [direction: -1 | 1] }>()

const moreOpen = ref(false)
const trigger = ref<HTMLButtonElement>()
const menu = ref<HTMLElement>()
const menuStyle = ref<Record<string, string>>({})

function placeMenu() {
  const rect = trigger.value?.getBoundingClientRect()
  if (rect) menuStyle.value = { top: `${Math.max(8, rect.bottom + 6)}px`, right: `${Math.max(8, window.innerWidth - rect.right)}px` }
}
function closeMenu(returnFocus = false) {
  if (!moreOpen.value) return
  moreOpen.value = false
  document.removeEventListener('pointerdown', onPointerDown, true)
  window.removeEventListener('resize', placeMenu)
  window.removeEventListener('scroll', placeMenu, true)
  if (returnFocus) nextTick(() => trigger.value?.focus())
}
function onPointerDown(event: PointerEvent) {
  const target = event.target as Node
  if (!trigger.value?.contains(target) && !menu.value?.contains(target)) closeMenu()
}
async function toggleMenu() {
  if (moreOpen.value) return closeMenu()
  moreOpen.value = true
  await nextTick()
  placeMenu()
  document.addEventListener('pointerdown', onPointerDown, true)
  window.addEventListener('resize', placeMenu)
  window.addEventListener('scroll', placeMenu, true)
}
function onMenuKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    event.preventDefault()
    closeMenu(true)
  }
}
function runAndClose(action: () => void) {
  action()
  closeMenu(true)
}
onBeforeUnmount(() => closeMenu())
</script>

<template>
  <article class="script-card" :class="{ running }" :draggable="!locked" @dragstart="emit('dragstart', $event)" @dragover.prevent @drop="emit('drop', $event)">
    <div class="script-summary">
      <button class="grab" type="button" title="拖拽调整脚本顺序" :disabled="locked" aria-label="拖拽调整脚本顺序">⠿</button>
      <span class="script-emblem" aria-hidden="true">{{ presetSummary === '通用程序' ? '通' : presetSummary.slice(0, 1) }}</span>
      <div class="script-title">
        <b>{{ script.name }}</b>
        <small>{{ tasks.length }} 个程序</small>
      </div>
      <span class="script-preset">{{ presetSummary }}</span>
      <span v-if="running" class="script-running">运行中</span>
      <span v-else-if="blocked" class="script-incomplete">待完成配置</span>
      <span v-else class="script-ready">可运行</span>
      <div class="card-actions">
        <button :disabled="locked" @click="emit('edit')">编辑脚本</button>
        <button class="run-button" :disabled="locked || blocked || !tasks.length" @click="emit('run')">运行</button>
        <button ref="trigger" class="more-trigger" :aria-expanded="moreOpen" aria-haspopup="menu" :disabled="locked" @click="toggleMenu" @keydown.esc="closeMenu(true)">更多</button>
      </div>
    </div>
    <Teleport to="body">
      <div v-if="moreOpen" ref="menu" class="action-menu" role="menu" :style="menuStyle" @keydown="onMenuKeydown">
        <button role="menuitem" :disabled="locked" @click="runAndClose(() => emit('move', -1))">上移</button>
        <button role="menuitem" :disabled="locked" @click="runAndClose(() => emit('move', 1))">下移</button>
        <button role="menuitem" :disabled="locked" @click="runAndClose(() => emit('duplicate'))">复制脚本</button>
        <button role="menuitem" class="textdanger" :disabled="locked" @click="runAndClose(() => emit('remove'))">删除脚本</button>
      </div>
    </Teleport>
  </article>
</template>
