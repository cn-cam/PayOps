<script setup lang="ts">
import { ref } from 'vue'
import { useRoute } from 'vue-router'
import { Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { notifyTasksApi } from '@/api/notify'
import type { NotifyStatus, NotifyTaskItem, NotifyTaskListQuery, NotifyType } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import AppSelect from '@/components/AppSelect.vue'
import ContentDialog, { type ContentSection } from '@/components/ContentDialog.vue'
import EnumTag from '@/components/EnumTag.vue'
import TablePager from '@/components/TablePager.vue'
import TimeRangePicker from '@/components/TimeRangePicker.vue'
import { confirmAction } from '@/utils/common'
import { NOTIFY_STATUS, NOTIFY_TYPE } from '@/utils/enums'
import { formatTime, rangeToQuery, type DateRange } from '@/utils/time'

const route = useRoute()
const auth = useAuthStore()

interface Query {
  bizNo: string
  appId: number | undefined
  status: NotifyStatus | undefined
  type: NotifyType | undefined
  timeRange: DateRange
}

function initialStatus(): NotifyStatus | undefined {
  const v = route.query.status
  return v === '0' || v === '1' || v === '2' ? (Number(v) as NotifyStatus) : undefined
}

let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<NotifyTaskItem, Query, NotifyTaskListQuery>(
    notifyTasksApi.list,
    () => {
      const first = firstQuery
      firstQuery = false
      return {
        bizNo: first && typeof route.query.bizNo === 'string' ? route.query.bizNo : '',
        appId: undefined,
        status: first ? initialStatus() : undefined,
        type: undefined,
        timeRange: null,
      }
    },
    (q) => ({
      bizNo: q.bizNo,
      appId: q.appId,
      status: q.status,
      type: q.type,
      ...rangeToQuery(q.timeRange),
    }),
  )

// ---------- 查看 ----------
const contentVisible = ref(false)
const contentTitle = ref('')
const contentSections = ref<ContentSection[]>([])

function view(row: NotifyTaskItem) {
  contentTitle.value = `业务通知 #${row.id}（${row.bizNo}）`
  contentSections.value = [
    { label: '通知地址', content: row.url },
    { label: '通知内容（payload）', content: row.payload },
    {
      label: '最近一次响应',
      content: row.lastResponse,
      extra: row.lastHttpStatus ? `HTTP ${row.lastHttpStatus} · ${formatTime(row.lastTime)}` : '尚未发送',
    },
  ]
  contentVisible.value = true
}

// ---------- 重发 ----------
const retryingId = ref<number | null>(null)

async function retry(row: NotifyTaskItem) {
  if (!(await confirmAction(`将立即向业务方重新发送「${row.bizNo}」的通知，并把任务重置为待通知。确定吗？`, '重发通知', 'info'))) {
    return
  }
  retryingId.value = row.id
  try {
    await notifyTasksApi.retry(row.id)
    ElMessage.success('已重新发起通知')
    load()
  } finally {
    retryingId.value = null
  }
}

function httpStatusClass(status: number | null): string {
  if (!status) return 'text-secondary'
  return status >= 200 && status < 300 ? 'text-success' : 'text-danger'
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="业务单号">
          <el-input
            v-model="query.bizNo"
            placeholder="订单号 / 退款单号 / 订阅号"
            clearable
            style="width: 220px"
            @keyup.enter="search"
          />
        </el-form-item>
        <el-form-item label="应用">
          <AppSelect v-model="query.appId" @change="search" />
        </el-form-item>
        <el-form-item label="类型">
          <el-select v-model="query.type" placeholder="全部" clearable style="width: 110px" @change="search">
            <el-option v-for="s in NOTIFY_TYPE" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 130px" @change="search">
            <el-option v-for="s in NOTIFY_STATUS" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="创建时间">
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
          业务通知任务
          <span class="toolbar__sub">支付 / 退款 / 订阅状态变更通知业务服务端的记录；失败会按退避策略自动重试，重试耗尽后需人工重发</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id">
        <el-table-column label="ID" prop="id" width="80" />
        <el-table-column label="业务单号 / 应用" min-width="200">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="mono">{{ row.bizNo }}</span>
              <span class="mono text-secondary">{{ row.appCode }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="类型" width="70" align="center">
          <template #default="{ row }">
            <EnumTag :options="NOTIFY_TYPE" :value="row.type" effect="plain" />
          </template>
        </el-table-column>
        <el-table-column label="通知地址" min-width="170" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="mono">{{ row.url }}</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="92" align="center">
          <template #default="{ row }">
            <EnumTag :options="NOTIFY_STATUS" :value="row.status" />
          </template>
        </el-table-column>
        <el-table-column label="已试 / 上限" width="90" align="center">
          <template #default="{ row }">{{ row.attempts }} / {{ row.maxAttempts }}</template>
        </el-table-column>
        <el-table-column label="最近发送 / 下次重试" width="165">
          <template #default="{ row }">
            <div class="cell-stack">
              <span>
                <span :class="httpStatusClass(row.lastHttpStatus)">
                  {{ row.lastHttpStatus ? `HTTP ${row.lastHttpStatus}` : '尚未发送' }}
                </span>
              </span>
              <span v-if="row.lastTime" class="text-secondary">{{ formatTime(row.lastTime) }}</span>
              <span v-if="row.status === 0 && row.nextTime" class="text-secondary text-small">
                下次 {{ formatTime(row.nextTime) }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="165">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" :width="auth.canWrite ? 110 : 70" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="view(row as NotifyTaskItem)">查看</el-button>
            <el-button
              v-if="auth.canWrite"
              link
              type="primary"
              :loading="retryingId === row.id"
              @click="retry(row as NotifyTaskItem)"
            >
              重发
            </el-button>
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
