<script setup lang="ts">
import { ref } from 'vue'
import { Refresh, Search } from '@element-plus/icons-vue'
import { auditLogsApi } from '@/api/users'
import type { AuditLogItem, AuditLogQuery } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import ContentDialog, { type ContentSection } from '@/components/ContentDialog.vue'
import TablePager from '@/components/TablePager.vue'
import TimeRangePicker from '@/components/TimeRangePicker.vue'
import { formatTime, rangeToQuery, type DateRange } from '@/utils/time'

interface Query {
  username: string
  action: string
  timeRange: DateRange
}

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<AuditLogItem, Query, AuditLogQuery>(
    auditLogsApi.list,
    () => ({ username: '', action: '', timeRange: null }),
    (q) => ({ username: q.username, action: q.action, ...rangeToQuery(q.timeRange) }),
  )

const contentVisible = ref(false)
const contentTitle = ref('')
const contentSections = ref<ContentSection[]>([])

function view(row: AuditLogItem) {
  contentTitle.value = `操作审计 #${row.id}`
  contentSections.value = [
    {
      label: '操作',
      content: `${row.username} · ${row.action}${row.target ? ` · ${row.target}` : ''}`,
      extra: `${row.ip || '-'} · ${formatTime(row.createdAt)}`,
    },
    { label: '详情', content: row.detail },
  ]
  contentVisible.value = true
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="用户名">
          <el-input v-model="query.username" placeholder="操作人" clearable style="width: 180px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="操作">
          <el-input v-model="query.action" placeholder="操作类型" clearable style="width: 200px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="操作时间">
          <TimeRangePicker v-model="query.timeRange" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Search" @click="search">查询</el-button>
          <el-button :icon="Refresh" @click="reset">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          操作审计
          <span class="toolbar__sub">后台所有写操作的留痕，共 {{ total }} 条</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id">
        <el-table-column label="时间" width="170">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作人" min-width="120">
          <template #default="{ row }">
            <span class="mono">{{ row.username }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" min-width="170" show-overflow-tooltip>
          <template #default="{ row }">
            <el-tag effect="plain">{{ row.action }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="对象" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="mono">{{ row.target || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="详情" min-width="200">
          <template #default="{ row }">
            <div class="mono text-secondary ellipsis" :title="row.detail">{{ row.detail || '-' }}</div>
          </template>
        </el-table-column>
        <el-table-column label="IP" width="140">
          <template #default="{ row }">
            <span class="mono">{{ row.ip || '-' }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="80" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" :disabled="!row.detail" @click="view(row as AuditLogItem)">详情</el-button>
          </template>
        </el-table-column>
      </el-table>

      <TablePager
        :total="total"
        :page="page"
        :page-size="pageSize"
        @page-change="onPageChange"
        @size-change="onSizeChange"
      />
    </el-card>

    <ContentDialog v-model="contentVisible" :title="contentTitle" :sections="contentSections" />
  </div>
</template>
