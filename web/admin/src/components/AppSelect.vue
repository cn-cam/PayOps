<script setup lang="ts">
import { onMounted } from 'vue'
import { useMetaStore } from '@/stores/meta'

withDefaults(
  defineProps<{
    modelValue: number | undefined
    placeholder?: string
    clearable?: boolean
    disabled?: boolean
    width?: string
  }>(),
  { placeholder: '全部应用', clearable: true, disabled: false, width: '200px' },
)

const emit = defineEmits<{
  'update:modelValue': [value: number | undefined]
  change: [value: number | undefined]
}>()

const meta = useMetaStore()

onMounted(() => meta.loadApps())

function onChange(value: number | string | undefined | null) {
  const v = value === '' || value === null || value === undefined ? undefined : Number(value)
  emit('update:modelValue', v)
  emit('change', v)
}
</script>

<template>
  <el-select
    :model-value="modelValue"
    :placeholder="placeholder"
    :clearable="clearable"
    :disabled="disabled"
    filterable
    :style="{ width }"
    @update:model-value="onChange"
  >
    <el-option
      v-for="app in meta.apps"
      :key="app.id"
      :value="app.id"
      :label="`${app.name}（${app.appCode}）`"
    >
      <span>{{ app.name }}</span>
      <span class="option-sub">{{ app.appCode }}</span>
      <el-tag v-if="app.status !== 1" size="small" type="info" class="option-tag">停用</el-tag>
    </el-option>
  </el-select>
</template>

<style scoped>
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.option-tag {
  margin-left: 8px;
}
</style>
