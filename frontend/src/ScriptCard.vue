<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref } from 'vue'
import type { Queue, Task } from './types'

const props = defineProps<{ script: Queue; tasks: Task[]; expanded: boolean; locked: boolean; blocked: boolean; running: boolean; presetSummary: string }>()
const emit = defineEmits<{ edit: []; run: []; duplicate: []; remove: []; toggle: []; dragstart: [event: DragEvent]; drop: [event: DragEvent]; move: [direction: -1 | 1] }>()
const moreOpen = ref(false)
const trigger = ref<HTMLButtonElement>()
const menu = ref<HTMLElement>()
const menuStyle = ref<Record<string, string>>({})

function placeMenu() { const rect = trigger.value?.getBoundingClientRect(); if (rect) menuStyle.value = { top: `${Math.max(8, rect.bottom + 6)}px`, right: `${Math.max(8, window.innerWidth - rect.right)}px` } }
function closeMenu(returnFocus = false) { if (!moreOpen.value) return; moreOpen.value = false; document.removeEventListener('pointerdown', onPointerDown, true); window.removeEventListener('resize', placeMenu); window.removeEventListener('scroll', placeMenu, true); if (returnFocus) nextTick(() => trigger.value?.focus()) }
function onPointerDown(event: PointerEvent) { const target = event.target as Node; if (!trigger.value?.contains(target) && !menu.value?.contains(target)) closeMenu() }
async function toggleMenu() { if (moreOpen.value) return closeMenu(); moreOpen.value = true; await nextTick(); placeMenu(); document.addEventListener('pointerdown', onPointerDown, true); window.addEventListener('resize', placeMenu); window.addEventListener('scroll', placeMenu, true) }
function onMenuKeydown(event: KeyboardEvent) { if (event.key === 'Escape') { event.preventDefault(); closeMenu(true) } }
onBeforeUnmount(() => closeMenu())
</script>

<template>
  <article class="script-card" :class="{ expanded, running }" :draggable="!locked" @dragstart="emit('dragstart', $event)" @dragover.prevent @drop="emit('drop', $event)">
    <div class="script-summary">
      <span class="grab" aria-hidden="true">⠿</span>
      <div class="script-title"><b>{{ script.name }}</b><small>{{ tasks.length }} 个程序 · {{ presetSummary }}</small></div>
      <span v-if="running" class="script-running">运行中</span><span v-else-if="blocked" class="script-incomplete">待完成配置</span><span v-else class="script-ready">可运行</span>
      <div class="card-actions"><button :disabled="locked" @click="emit('edit')">编辑脚本</button><button class="run-button" :disabled="locked || blocked || !tasks.length" @click="emit('run')">运行</button><button ref="trigger" class="more-trigger" :aria-expanded="moreOpen" aria-haspopup="menu" :disabled="locked" @click="toggleMenu" @keydown.esc="closeMenu(true)">更多</button><button class="chevron" :aria-label="expanded ? '收起程序摘要' : '展开程序摘要'" @click="emit('toggle')">{{ expanded ? '⌃' : '⌄' }}</button></div>
    </div>
    <div class="script-order" aria-label="脚本顺序"><button :disabled="locked" @click="emit('move', -1)">上移</button><button :disabled="locked" @click="emit('move', 1)">下移</button><small>此顺序仅用于“运行全部脚本”。</small></div>
    <div v-if="expanded" class="script-preview"><p v-if="!tasks.length">尚未添加程序。点击“编辑脚本”完成配置。</p><ol v-else><li v-for="task in tasks" :key="task.id"><b>{{ task.name }}</b><span>{{ task.preset_id || '通用程序' }}</span></li></ol></div>
    <Teleport to="body"><div v-if="moreOpen" ref="menu" class="action-menu" role="menu" :style="menuStyle" @keydown="onMenuKeydown"><button role="menuitem" :disabled="locked" @click="emit('duplicate'); closeMenu(true)">复制脚本</button><button role="menuitem" class="textdanger" :disabled="locked" @click="emit('remove'); closeMenu(true)">删除脚本</button></div></Teleport>
  </article>
</template>
