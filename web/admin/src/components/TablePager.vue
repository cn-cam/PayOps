<script setup lang="ts">
defineProps<{
  total: number
  page: number
  pageSize: number
}>()

const emit = defineEmits<{
  'page-change': [page: number]
  'size-change': [size: number]
}>()
</script>

<template>
  <div class="table-pager">
    <el-pagination
      background
      layout="total, sizes, prev, pager, next, jumper"
      :total="total"
      :current-page="page"
      :page-size="pageSize"
      :page-sizes="[10, 20, 50, 100, 200]"
      @current-change="(p: number) => emit('page-change', p)"
      @size-change="(s: number) => emit('size-change', s)"
    />
  </div>
</template>

<style scoped>
.table-pager {
  display: flex;
  justify-content: flex-end;
  margin-top: 16px;
}
</style>
