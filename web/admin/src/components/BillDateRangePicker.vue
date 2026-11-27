<script setup lang="ts">
import dayjs from 'dayjs'

/** 账单日范围（yyyy-MM-dd，闭区间），用于对账相关筛选 */
export type BillDateRange = [string, string] | null

defineProps<{ modelValue: BillDateRange }>()
const emit = defineEmits<{
  'update:modelValue': [value: BillDateRange]
  change: [value: BillDateRange]
}>()

const day = (offset: number) => dayjs().add(offset, 'day').startOf('day').toDate()

const shortcuts = [
  { text: '昨天', value: () => [day(-1), day(-1)] },
  { text: '近 7 天', value: () => [day(-7), day(-1)] },
  { text: '近 30 天', value: () => [day(-30), day(-1)] },
  { text: '本月', value: () => [dayjs().startOf('month').toDate(), day(0)] },
]

function onChange(value: unknown) {
  const next: BillDateRange =
    Array.isArray(value) && value.length === 2 && value[0] && value[1] ? [String(value[0]), String(value[1])] : null
  emit('update:modelValue', next)
  emit('change', next)
}
</script>

<template>
  <el-date-picker
    :model-value="modelValue"
    type="daterange"
    value-format="YYYY-MM-DD"
    range-separator="至"
    start-placeholder="开始账单日"
    end-placeholder="结束账单日"
    :shortcuts="shortcuts"
    style="width: 260px"
    @update:model-value="onChange"
  />
</template>
