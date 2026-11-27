<script setup lang="ts">
import JsonViewer from './JsonViewer.vue'

export interface ContentSection {
  label: string
  content: unknown
  /** 附加说明，显示在标题右侧 */
  extra?: string
}

defineProps<{
  modelValue: boolean
  title: string
  sections: ContentSection[]
}>()

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="title"
    width="760px"
    top="8vh"
    append-to-body
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  >
    <div v-for="(s, i) in sections" :key="i" class="content-section">
      <div class="content-section__head">
        <span class="content-section__label">{{ s.label }}</span>
        <span v-if="s.extra" class="content-section__extra">{{ s.extra }}</span>
      </div>
      <JsonViewer :value="s.content" max-height="42vh" />
    </div>
    <template #footer>
      <el-button type="primary" @click="emit('update:modelValue', false)">关闭</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.content-section + .content-section {
  margin-top: 16px;
}
.content-section__head {
  display: flex;
  align-items: baseline;
  gap: 10px;
  margin-bottom: 6px;
}
.content-section__label {
  font-weight: 600;
}
.content-section__extra {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
</style>
