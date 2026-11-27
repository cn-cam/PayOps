<script setup lang="ts">
import { useRouter } from 'vue-router'

defineProps<{ kind: '403' | '404' }>()
const router = useRouter()
</script>

<template>
  <div class="page">
    <el-card shadow="never">
      <el-result
        :icon="kind === '403' ? 'warning' : 'info'"
        :title="kind === '403' ? '无权限' : '页面不存在'"
        :sub-title="kind === '403' ? '当前账号没有访问该页面的权限' : '请检查访问地址是否正确'"
      >
        <template #extra>
          <el-button type="primary" @click="router.replace('/dashboard')">返回仪表盘</el-button>
        </template>
      </el-result>
    </el-card>
  </div>
</template>
