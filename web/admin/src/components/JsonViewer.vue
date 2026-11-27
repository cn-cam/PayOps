<script setup lang="ts">
import { computed } from 'vue'
import { CopyDocument } from '@element-plus/icons-vue'
import { copyText, prettyJson } from '@/utils/common'

const props = withDefaults(
  defineProps<{
    value: unknown
    maxHeight?: string
    emptyText?: string
  }>(),
  { maxHeight: '360px', emptyText: '（空）' },
)

const text = computed(() => prettyJson(props.value))
</script>

<template>
  <div class="json-viewer">
    <el-button
      v-if="text"
      class="json-viewer__copy"
      size="small"
      text
      :icon="CopyDocument"
      @click="copyText(text)"
    >
      复制
    </el-button>
    <pre v-if="text" class="json-viewer__pre" :style="{ maxHeight }">{{ text }}</pre>
    <div v-else class="json-viewer__empty">{{ emptyText }}</div>
  </div>
</template>

<style scoped>
.json-viewer {
  position: relative;
  width: 100%;
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 6px;
  background: #f8fafc;
}
.json-viewer__copy {
  position: absolute;
  top: 6px;
  right: 10px;
}
.json-viewer__pre {
  margin: 0;
  padding: 12px 14px;
  overflow: auto;
  font-family: var(--mono-font);
  font-size: 12.5px;
  line-height: 1.6;
  color: #1f2937;
  white-space: pre-wrap;
  word-break: break-all;
}
.json-viewer__empty {
  padding: 14px;
  color: var(--el-text-color-placeholder);
  font-size: 13px;
}
</style>
