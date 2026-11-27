<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { Refresh, Search } from '@element-plus/icons-vue'
import { subscriptionsApi } from '@/api/subscriptions'
import type { SubscriptionItem, SubscriptionListQuery, SubscriptionStatus } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useMetaStore } from '@/stores/meta'
import AppSelect from '@/components/AppSelect.vue'
import DetailDrawers from '@/components/DetailDrawers.vue'
import EnumTag from '@/components/EnumTag.vue'
import ProviderSelect from '@/components/ProviderSelect.vue'
import TablePager from '@/components/TablePager.vue'
import TimeRangePicker from '@/components/TimeRangePicker.vue'
import { AUTO_RENEW, SUBSCRIPTION_MODE, SUBSCRIPTION_STATUS } from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { expiryInfo } from '@/utils/subscription'
import { formatTime, rangeToQuery, type DateRange } from '@/utils/time'

const route = useRoute()
const meta = useMetaStore()

interface Query {
  subNo: string
  appId: number | undefined
  provider: string | undefined
  status: SubscriptionStatus | undefined
  autoRenew: boolean | undefined
  userId: string
  productId: string
  timeRange: DateRange
}

function queryString(key: string): string {
  const v = route.query[key]
  return typeof v === 'string' ? v : ''
}

function initialStatus(): SubscriptionStatus | undefined {
  const v = queryString('status')
  return v === '1' || v === '2' || v === '3' || v === '4' ? (Number(v) as SubscriptionStatus) : undefined
}

function initialAutoRenew(): boolean | undefined {
  const v = queryString('autoRenew')
  return v === 'true' ? true : v === 'false' ? false : undefined
}

let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<SubscriptionItem, Query, SubscriptionListQuery>(
    subscriptionsApi.list,
    () => {
      // 仅首次使用地址栏带入的条件（来自仪表盘等页面的跳转）
      const first = firstQuery
      firstQuery = false
      return {
        subNo: first ? queryString('subNo') : '',
        appId: undefined,
        provider: first ? queryString('provider') || undefined : undefined,
        status: first ? initialStatus() : undefined,
        autoRenew: first ? initialAutoRenew() : undefined,
        userId: first ? queryString('userId') : '',
        productId: '',
        timeRange: null,
      }
    },
    (q) => ({
      subNo: q.subNo,
      appId: q.appId,
      provider: q.provider,
      status: q.status,
      autoRenew: q.autoRenew,
      userId: q.userId,
      productId: q.productId,
      ...rangeToQuery(q.timeRange),
    }),
  )

onMounted(() => meta.loadProviders())

const drawers = ref<InstanceType<typeof DetailDrawers>>()

function openDetail(subNo: string) {
  drawers.value?.openSubscription(subNo)
}
</script>

<template>
  <div class="page">
    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          订阅是怎么运转的
          <span class="toolbar__sub">签约代扣 = 本系统按周期发起扣款；商店订阅 = 商店扣款</span>
        </div>
      </div>
      <div class="flow-steps">
        <div class="flow-steps__step">
          <span class="flow-steps__no">1</span>
          <div>
            <div class="flow-steps__title">首购 / 签约</div>
            <div class="flow-steps__desc">商店订阅由首购订单创建；签约代扣由签约单建立协议，之后按周期自动扣款</div>
          </div>
        </div>
        <div class="flow-steps__step">
          <span class="flow-steps__no">2</span>
          <div>
            <div class="flow-steps__title">每期续费</div>
            <div class="flow-steps__desc">商店订阅由商店扣款并回调；签约代扣由本系统到期发起扣款，成功后生成续费单并顺延周期</div>
          </div>
        </div>
        <div class="flow-steps__step">
          <span class="flow-steps__no">3</span>
          <div>
            <div class="flow-steps__title">退订</div>
            <div class="flow-steps__desc">退订只是关闭自动续订，当前周期结束前订阅仍然生效，到期后才失效</div>
          </div>
        </div>
        <div class="flow-steps__step">
          <span class="flow-steps__no">4</span>
          <div>
            <div class="flow-steps__title">到期 / 撤销</div>
            <div class="flow-steps__desc">不再续费则到期变为「已过期」；发生退款或撤回则立即变为「已撤销」</div>
          </div>
        </div>
      </div>
    </el-card>

    <el-card shadow="never" class="filter-card">
      <el-form inline label-width="70px" @submit.prevent="search">
        <el-form-item label="订阅号">
          <el-input v-model="query.subNo" placeholder="平台订阅号" clearable style="width: 220px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="用户 ID">
          <el-input v-model="query.userId" placeholder="userId" clearable style="width: 220px" @keyup.enter="search" />
        </el-form-item>
        <el-form-item label="商品 ID">
          <el-input
            v-model="query.productId"
            placeholder="productId"
            clearable
            style="width: 220px"
            @keyup.enter="search"
          />
        </el-form-item>
        <el-form-item label="应用">
          <AppSelect v-model="query.appId" width="220px" @change="search" />
        </el-form-item>
        <el-form-item label="支付类型">
          <ProviderSelect v-model="query.provider" width="220px" @change="search" />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 220px" @change="search">
            <el-option v-for="s in SUBSCRIPTION_STATUS" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="续订">
          <el-select v-model="query.autoRenew" placeholder="全部" clearable style="width: 220px" @change="search">
            <el-option v-for="s in AUTO_RENEW" :key="String(s.value)" :value="s.value" :label="s.label" />
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
          订阅列表
          <span class="toolbar__sub">共 {{ total }} 条；同步、退订等操作在订阅详情里</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="subNo">
        <el-table-column label="订阅号 / 应用 · 渠道" min-width="200">
          <template #default="{ row }">
            <div class="cell-stack">
              <el-link type="primary" underline="never" class="mono" @click="openDetail(row.subNo)">
                {{ row.subNo }}
              </el-link>
              <span class="mono text-secondary">{{ row.appCode }} · {{ row.channelCode }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="通道" min-width="150">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="ellipsis" :title="row.passageName">{{ row.passageName || '-' }}</span>
              <span class="text-secondary text-small">{{ meta.providerName(row.provider) }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="模式" width="120" align="center">
          <template #default="{ row }">
            <EnumTag :options="SUBSCRIPTION_MODE" :value="row.mode" size="small" effect="plain" />
          </template>
        </el-table-column>
        <el-table-column label="商品 / 用户" min-width="140">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="mono ellipsis" :title="row.productId">{{ row.productId || '-' }}</span>
              <span class="mono text-secondary ellipsis" :title="row.userId">{{ row.userId || '-' }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态 / 续订" width="100" align="center">
          <template #default="{ row }">
            <div class="cell-stack cell-stack--tight">
              <EnumTag :options="SUBSCRIPTION_STATUS" :value="row.status" />
              <EnumTag :options="AUTO_RENEW" :value="row.autoRenew" size="small" effect="plain" />
            </div>
          </template>
        </el-table-column>
        <el-table-column label="最近一期金额" width="124" align="right">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="money">{{ formatMoney(row.amount) }}</span>
              <span class="text-secondary text-small">已续 {{ row.renewCount ?? 0 }} 期</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="当前周期" width="186">
          <template #default="{ row }">
            <div class="cell-stack">
              <span class="text-secondary">{{ formatTime(row.periodStart) }} 起</span>
              <span :class="`expiry--${expiryInfo(row as SubscriptionItem).level}`">
                {{ formatTime(row.expiresAt) }} 止
              </span>
              <span
                v-if="['soon', 'expired'].includes(expiryInfo(row as SubscriptionItem).level)"
                class="text-small"
                :class="`expiry--${expiryInfo(row as SubscriptionItem).level}`"
              >
                {{ expiryInfo(row as SubscriptionItem).text }}
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="下次扣款" width="150">
          <template #default="{ row }">
            <div v-if="row.mode === 1" class="cell-stack">
              <span v-if="row.status === 4" class="text-warning">已暂停</span>
              <span v-else-if="!row.autoRenew" class="text-secondary">已退订</span>
              <span v-else>{{ formatTime(row.nextChargeAt) }}</span>
              <span v-if="row.chargeFailCount > 0" class="text-danger text-small">
                连续失败 {{ row.chargeFailCount }} 次
              </span>
            </div>
            <span v-else class="text-secondary">—</span>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="165">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="70" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openDetail(row.subNo)">详情</el-button>
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
