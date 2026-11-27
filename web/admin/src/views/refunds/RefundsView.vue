<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { refundsApi } from '@/api/refunds'
import type { RefundItem, RefundListQuery, RefundStatus } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import AppSelect from '@/components/AppSelect.vue'
import EnumTag from '@/components/EnumTag.vue'
import DetailDrawers from '@/components/DetailDrawers.vue'
import TablePager from '@/components/TablePager.vue'
import TimeRangePicker from '@/components/TimeRangePicker.vue'
import { REFUND_SOURCE, REFUND_STATUS, enumLabel } from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { formatTime, rangeToQuery, type DateRange } from '@/utils/time'

const route = useRoute()
const auth = useAuthStore()
const meta = useMetaStore()

interface Query {
  refundNo: string
  orderNo: string
  mchRefundNo: string
  appId: number | undefined
  status: RefundStatus | undefined
  timeRange: DateRange
}

function initialStatus(): RefundStatus | undefined {
  const v = route.query.status
  return v === '0' || v === '1' || v === '2' ? (Number(v) as RefundStatus) : undefined
}

let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<RefundItem, Query, RefundListQuery>(
    refundsApi.list,
    () => {
      const first = firstQuery
      firstQuery = false
      return {
        refundNo: '',
        orderNo: first && typeof route.query.orderNo === 'string' ? route.query.orderNo : '',
        mchRefundNo: '',
        appId: undefined,
        status: first ? initialStatus() : undefined,
        timeRange: null,
      }
    },
    (q) => ({
      refundNo: q.refundNo,
      orderNo: q.orderNo,
      mchRefundNo: q.mchRefundNo,
      appId: q.appId,
      status: q.status,
      ...rangeToQuery(q.timeRange),
    }),
  )

onMounted(() => meta.loadProviders())

const syncingNo = ref('')

async function sync(row: RefundItem) {
  syncingNo.value = row.refundNo
  try {
    const res = await refundsApi.sync(row.refundNo)
    const text = res.message || (res.changed ? '退款状态已更新' : '退款状态无变化')
    if (res.changed) {
      ElMessage.success(`${text}（当前：${enumLabel(REFUND_STATUS, res.status)}）`)
      load()
    } else {
      ElMessage.info(text)
    }
  } finally {
    syncingNo.value = ''
  }
}

const drawers = ref<InstanceType<typeof DetailDrawers>>()

function openOrder(orderNo: string) {
  drawers.value?.openOrder(orderNo)
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="退款单号">
          <el-input v-model="query.refundNo" placeholder="平台退款单号" clearable style="width: 210px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="订单号">
          <el-input v-model="query.orderNo" placeholder="平台订单号" clearable style="width: 210px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="商户退款单号">
          <el-input
            v-model="query.mchRefundNo"
            placeholder="业务方退款单号"
            clearable
            style="width: 210px"
            @keyup.enter="search"
          />
        </el-form-item>
        <el-form-item label="应用">
          <AppSelect v-model="query.appId" @change="search" />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 130px" @change="search">
            <el-option v-for="s in REFUND_STATUS" :key="s.value" :value="s.value" :label="s.label" />
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
          退款列表
          <span class="toolbar__sub">共 {{ total }} 条；发起退款请到订单详情中操作</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="refundNo">
        <el-table-column label="退款单号 / 商户退款单号" min-width="200">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="mono">{{ row.refundNo }}</span>
              <span class="mono text-secondary ellipsis" :title="row.mchRefundNo">{{ row.mchRefundNo || '-' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="订单号 / 上游退款单号" min-width="205">
          <template #default="{ row }">
            <div class="cell-stack">
              <el-link type="primary" underline="never" class="mono" @click="openOrder(row.orderNo)">
                {{ row.orderNo }}
              </el-link>
              <span class="mono text-secondary ellipsis" :title="row.upstreamRefundNo">
                {{ row.upstreamRefundNo || '-' }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="应用 / 支付类型" min-width="120">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="mono">{{ row.appCode }}</span>
              <span class="text-secondary text-small">{{ meta.providerName(row.provider) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="退款金额" width="110" align="right">
          <template #default="{ row }">
            <span class="money">{{ formatMoney(row.amount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="状态 / 来源" width="104" align="center">
          <template #default="{ row }">
            <div class="cell-stack">
              <EnumTag :options="REFUND_STATUS" :value="row.status" />
              <span class="text-secondary text-small">{{ enumLabel(REFUND_SOURCE, row.source) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="原因 / 失败信息" min-width="150" show-overflow-tooltip>
          <template #default="{ row }">
            <span>{{ row.reason || '-' }}</span>
            <span v-if="row.errorMsg" class="text-danger">（{{ row.errorMsg }}）</span>
          </template>
        </el-table-column>
        <el-table-column label="创建 / 成功时间" width="165">
          <template #default="{ row }">
            <div class="cell-stack">
              <span>{{ formatTime(row.createdAt) }}</span>
              <span class="text-secondary">{{ formatTime(row.successAt) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column v-if="auth.canWrite" label="操作" width="80" fixed="right">
          <template #default="{ row }">
            <el-tooltip content="向上游查询退款结果并同步" placement="top" :disabled="row.status === 1">
              <el-button
                link
                type="primary"
                :disabled="row.status === 1"
                :loading="syncingNo === row.refundNo"
                @click="sync(row as RefundItem)"
              >
                同步
              </el-button>
            </el-tooltip>
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

    <DetailDrawers ref="drawers" @changed="load" />
  </div>
</template>
