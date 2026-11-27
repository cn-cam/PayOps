<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { Refresh, Search } from '@element-plus/icons-vue'
import { passagesApi } from '@/api/passages'
import { upstreamNotifiesApi } from '@/api/notify'
import type { PassageBrief, UpstreamNotifyItem, UpstreamNotifyListQuery } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useMetaStore } from '@/stores/meta'
import ContentDialog, { type ContentSection } from '@/components/ContentDialog.vue'
import EnumTag from '@/components/EnumTag.vue'
import OrderDetailDrawer from '@/components/OrderDetailDrawer.vue'
import ProviderSelect from '@/components/ProviderSelect.vue'
import TablePager from '@/components/TablePager.vue'
import TimeRangePicker from '@/components/TimeRangePicker.vue'
import { VERIFIED } from '@/utils/enums'
import { formatTime, rangeToQuery, type DateRange } from '@/utils/time'

const meta = useMetaStore()

interface Query {
  provider: string | undefined
  orderNo: string
  passageId: number | undefined
  verified: boolean | undefined
  timeRange: DateRange
}

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<UpstreamNotifyItem, Query, UpstreamNotifyListQuery>(
    upstreamNotifiesApi.list,
    () => ({ provider: undefined, orderNo: '', passageId: undefined, verified: undefined, timeRange: null }),
    (q) => ({
      provider: q.provider,
      orderNo: q.orderNo,
      passageId: q.passageId,
      verified: q.verified,
      ...rangeToQuery(q.timeRange),
    }),
  )

// ---------- 通道下拉（同时用于把 passageId 显示为名称） ----------
const allPassages = ref<PassageBrief[]>([])
const passageOptions = computed(() =>
  query.provider ? allPassages.value.filter((p) => p.provider === query.provider) : allPassages.value,
)
const passageNames = computed(() => new Map(allPassages.value.map((p) => [p.id, p.name])))

onMounted(async () => {
  meta.loadProviders()
  allPassages.value = (await passagesApi.all()) ?? []
})

function onProviderChange() {
  query.passageId = undefined
  search()
}

function onReset() {
  reset()
}

// ---------- 查看报文 ----------
const contentVisible = ref(false)
const contentTitle = ref('')
const contentSections = ref<ContentSection[]>([])

function view(row: UpstreamNotifyItem) {
  contentTitle.value = `上游回调 #${row.id}`
  contentSections.value = [
    { label: '处理结果', content: row.result, extra: row.verified ? '验签通过' : '验签失败' },
    {
      label: '请求报文',
      content: row.requestBody,
      extra: `${meta.providerName(row.provider)} · ${row.clientIp || '-'} · ${formatTime(row.createdAt)}`,
    },
  ]
  contentVisible.value = true
}

const detailVisible = ref(false)
const detailOrderNo = ref('')

function openOrder(orderNo: string) {
  detailOrderNo.value = orderNo
  detailVisible.value = true
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="支付类型">
          <ProviderSelect v-model="query.provider" @change="onProviderChange" />
        </el-form-item>
        <el-form-item label="通道">
          <el-select
            v-model="query.passageId"
            placeholder="全部通道"
            clearable
            filterable
            style="width: 220px"
            @change="search"
          >
            <el-option v-for="p in passageOptions" :key="p.id" :value="p.id" :label="p.name" />
          </el-select>
        </el-form-item>
        <el-form-item label="订单号">
          <el-input v-model="query.orderNo" placeholder="平台订单号" clearable style="width: 220px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="验签">
          <el-select v-model="query.verified" placeholder="全部" clearable style="width: 130px" @change="search">
            <el-option v-for="s in VERIFIED" :key="String(s.value)" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="接收时间">
          <TimeRangePicker v-model="query.timeRange" />
        </el-form-item>
        <el-form-item>
          <el-button type="primary" :icon="Search" @click="search">查询</el-button>
          <el-button :icon="Refresh" @click="onReset">重置</el-button>
        </el-form-item>
      </el-form>
    </el-card>

    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          上游回调日志
          <span class="toolbar__sub">支付平台 / 应用商店推送过来的原始回调，用于排查验签失败、漏单等问题</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id">
        <el-table-column label="ID" prop="id" width="80" />
        <el-table-column label="通道 / 支付类型" min-width="170">
          <template #default="{ row }">
            <div class="cell-stack">
              <span v-if="row.passageId" class="ellipsis">
                {{ passageNames.get(row.passageId) ?? `#${row.passageId}` }}
              </span>
              <span v-else class="text-secondary">未匹配通道</span>
              <span class="text-secondary text-small">{{ meta.providerName(row.provider) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="回调类型" prop="kind" width="90" show-overflow-tooltip />
        <el-table-column label="订单号" min-width="200">
          <template #default="{ row }">
            <el-link v-if="row.orderNo" type="primary" underline="never" class="mono" @click="openOrder(row.orderNo)">
              {{ row.orderNo }}
            </el-link>
            <span v-else class="text-secondary">-</span>
          </template>
        </el-table-column>
        <el-table-column label="验签" width="100" align="center">
          <template #default="{ row }">
            <EnumTag :options="VERIFIED" :value="row.verified" />
          </template>
        </el-table-column>
        <el-table-column label="处理结果" prop="result" min-width="170" show-overflow-tooltip />
        <el-table-column label="接收时间 / 来源 IP" width="165">
          <template #default="{ row }">
            <div class="cell-stack">
              <span>{{ formatTime(row.createdAt) }}</span>
              <span class="mono text-secondary">{{ row.clientIp || '-' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="view(row as UpstreamNotifyItem)">查看报文</el-button>
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
    <OrderDetailDrawer v-model="detailVisible" :order-no="detailOrderNo" />
  </div>
</template>
