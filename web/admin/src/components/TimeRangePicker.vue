<script setup lang="ts">
import { rangeShortcuts, type DateRange } from '@/utils/time'

defineProps<{ modelValue: DateRange }>()
const emit = defineEmits<{ 'update:modelValue': [value: DateRange] }>()

const defaultTime: [Date, Date] = [new Date(2000, 0, 1, 0, 0, 0), new Date(2000, 0, 1, 23, 59, 59)]

function onChange(value: unknown) {
  if (Array.isArray(value) && value.length === 2 && value[0] && value[1]) {
    emit('update:modelValue', [new Date(value[0]), new Date(value[1])])
  } else {
    emit('update:modelValue', null)
  }
}
</script>

<template>
  <el-date-picker
    :model-value="modelValue"
    type="datetimerange"
    range-separator="至"
    start-placeholder="开始时间"
    end-placeholder="结束时间"
    format="YYYY-MM-DD HH:mm:ss"
    :default-time="defaultTime"
    :shortcuts="rangeShortcuts"
    style="width: 360px"
    @update:model-value="onChange"
  />
</template>
