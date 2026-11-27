<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { CircleCheckFilled, Promotion, Refresh } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { bindingsApi } from '@/api/bindings'
import { channelsApi } from '@/api/channels'
import { ordersApi } from '@/api/orders'
import type { BindingItem, ChannelBrief, OrderStatus, OrderType, TestOrderResult } from '@/api/types'
import { useMetaStore } from '@/stores/meta'
import AppSelect from '@/components/AppSelect.vue'
import EnumTag from '@/components/EnumTag.vue'
import JsonViewer from '@/components/JsonViewer.vue'
import MoneyInput from '@/components/MoneyInput.vue'
import DetailDrawers from '@/components/DetailDrawers.vue'
import { validateForm } from '@/utils/common'
import { ORDER_STATUS, ORDER_TYPE } from '@/utils/enums'
import { formatMoney } from '@/utils/money'
import { formatTime } from '@/utils/time'

const router = useRouter()
const meta = useMetaStore()

const formRef = ref<FormInstance>()
const submitting = ref(false)
const form = reactive({
  appId: undefined as number | undefined,
  channelCode: '',
  payMethod: '',
  amount: 100,
  subject: '测试商品',
  productId: '',
  openId: '',
  userId: '',
  subscribe: false,
  signAgreement: false,
  periodDays: 30,
})

const rules: FormRules = {
  appId: [{ required: true, message: '请选择应用', trigger: 'change' }],
  channelCode: [{ required: true, message: '请选择渠道', trigger: 'change' }],
  payMethod: [{ required: true, message: '请选择支付方式', trigger: 'change' }],
  amount: [
    {
      validator: (_rule, _value, callback) => {
        if (form.amount <= 0) callback(new Error('请输入大于 0 的金额'))
        else callback()
      },
      trigger: 'change',
    },
  ],
  subject: [{ required: true, message: '请输入商品标题', trigger: 'blur' }],
  periodDays: [
    {
      validator: (_rule, _value, callback) => {
        if (!Number.isInteger(form.periodDays) || form.periodDays < 1 || form.periodDays > 365) {
          callback(new Error('扣款周期为 1~365 天'))
        } else callback()
      },
      trigger: 'change',
    },
  ],
}

// ---------- 应用 → 渠道 → 支付方式 联动 ----------
const channels = ref<ChannelBrief[]>([])
const channelsLoading = ref(false)
const channelBindings = ref<BindingItem[]>([])

const selectedChannel = computed(() => channels.value.find((c) => c.code === form.channelCode))

async function onAppChange() {
  form.channelCode = ''
  channels.value = []
  channelBindings.value = []
  const appId = form.appId
  if (appId === undefined) return
  channelsLoading.value = true
  try {
    const list = await channelsApi.all(appId)
    if (form.appId === appId) channels.value = list ?? []
  } finally {
    channelsLoading.value = false
  }
}

async function onChannelChange() {
  channelBindings.value = []
  const channel = selectedChannel.value
  if (!channel) return
  const list = await bindingsApi.list(channel.id)
  if (selectedChannel.value?.id === channel.id) channelBindings.value = list ?? []
}

/** 已绑定到所选渠道的支付类型 → 启用中的通道数 */
const boundProviders = computed(() => {
  const map = new Map<string, number>()
  for (const b of channelBindings.value) {
    if (b.status === 1 && b.passageStatus === 1) map.set(b.provider, (map.get(b.provider) ?? 0) + 1)
  }
  return map
})

/** 全部支付方式（来自 provider 元数据），所选渠道已绑定的支付类型排在前面 */
const methodGroups = computed(() => {
  const groups = meta.providers
    .filter((p) => (p.methods ?? []).length > 0)
    .map((p) => ({
      provider: p.code,
      label: boundProviders.value.has(p.code)
        ? `${p.name}（该渠道已绑定 ${boundProviders.value.get(p.code)} 条通道）`
        : p.name,
      bound: boundProviders.value.has(p.code),
      methods: p.methods,
    }))
  return groups.sort((a, b) => Number(b.bound) - Number(a.bound))
})

const methodProvider = computed(() => meta.methodMap.get(form.payMethod)?.provider)
const methodNotBound = computed(
  () => !!form.payMethod && !!selectedChannel.value && !!methodProvider.value && !boundProviders.value.has(methodProvider.value),
)

/** 只有所选支付方式对应的支付类型支持自动续费时，才能下订阅单 */
const subscriptionSupported = computed(
  () => !!meta.getProvider(methodProvider.value)?.capabilities?.subscription,
)

/** 只有所选支付方式对应的支付类型支持签约代扣时，才展示签约开关 */
const deductSupported = computed(() => !!meta.getProvider(methodProvider.value)?.capabilities?.deduct)

watch(subscriptionSupported, (supported) => {
  if (!supported) form.subscribe = false
})

watch(deductSupported, (supported) => {
  if (!supported) form.signAgreement = false
})

// 商店订阅由商店扣款，签约代扣由本系统扣款，二者互斥
watch(
  () => form.subscribe,
  (on) => {
    if (on) form.signAgreement = false
  },
)
watch(
  () => form.signAgreement,
  (on) => {
    if (on) form.subscribe = false
  },
)

onMounted(() => meta.loadProviders())

// ---------- 下单 ----------
interface HistoryEntry {
  orderNo: string
  time: Date
  payMethod: string
  amount: number
  provider: string
  passageName: string
  status: OrderStatus
  orderType: OrderType
  /** 订阅首购单支付成功后才会有 */
  subNo: string | null
}

const result = ref<TestOrderResult | null>(null)
const currentStatus = ref<OrderStatus>(0)
const currentPaidAt = ref<string | null>(null)
const currentSubNo = ref<string | null>(null)
const history = ref<HistoryEntry[]>([])

async function submit() {
  if (!(await validateForm(formRef.value))) return
  submitting.value = true
  try {
    const res = await ordersApi.test({
      appId: form.appId as number,
      channelCode: form.channelCode,
      payMethod: form.payMethod,
      amount: form.amount,
      subject: form.subject.trim(),
      productId: form.productId.trim(),
      openId: form.openId.trim(),
      userId: form.userId.trim(),
      subscribe: subscriptionSupported.value && form.subscribe,
      signAgreement: deductSupported.value && form.signAgreement,
      periodDays: deductSupported.value && form.signAgreement ? form.periodDays : 0,
    })
    result.value = res
    currentStatus.value = res.status
    currentPaidAt.value = null
    currentSubNo.value = null
    history.value.unshift({
      orderNo: res.orderNo,
      time: new Date(),
      payMethod: res.payMethod,
      amount: res.amount,
      provider: res.provider,
      passageName: res.passageName,
      status: res.status,
      orderType: res.orderType ?? 0,
      subNo: null,
    })
    ElMessage.success(`下单成功，已路由到「${res.passageName}」`)
  } finally {
    submitting.value = false
  }
}

function setStatus(orderNo: string, status: OrderStatus, subNo: string | null) {
  const entry = history.value.find((h) => h.orderNo === orderNo)
  if (entry) {
    entry.status = status
    entry.subNo = subNo
  }
  if (result.value?.orderNo === orderNo) {
    currentStatus.value = status
    currentSubNo.value = subNo
  }
}

/** 订阅首购（1）和签约单（3）支付 / 签约成功后都会建立订阅 */
function isSubOrder(type: OrderType | undefined | null): boolean {
  return type === 1 || type === 3
}

const refreshing = ref(false)

async function refreshStatus(orderNo: string, silent = false) {
  refreshing.value = true
  try {
    const detail = await ordersApi.get(orderNo)
    setStatus(orderNo, detail.status, detail.subNo ?? null)
    if (result.value?.orderNo === orderNo) currentPaidAt.value = detail.paidAt
    if (!silent) ElMessage.success('订单状态已刷新')
    return detail
  } finally {
    refreshing.value = false
  }
}

const mockPaying = ref(false)

async function mockPay() {
  if (!result.value) return
  const orderNo = result.value.orderNo
  mockPaying.value = true
  try {
    await ordersApi.mockPay(orderNo)
    const detail = await refreshStatus(orderNo, true)
    if (detail.orderType === 3 && detail.subNo) {
      ElMessage.success(`已模拟签约成功，代扣订阅 ${detail.subNo} 已创建`)
    } else if (detail.orderType === 3) {
      ElMessage.success('已模拟签约成功；订阅号尚未返回，稍后点「刷新状态」查看')
    } else if (detail.orderType === 1 && detail.subNo) {
      ElMessage.success(`已模拟支付成功，订阅 ${detail.subNo} 已创建`)
    } else if (detail.orderType === 1) {
      ElMessage.success('已模拟支付成功；订阅号尚未返回，稍后点「刷新状态」查看')
    } else {
      ElMessage.success('已模拟上游支付成功回调')
    }
  } finally {
    mockPaying.value = false
  }
}

/** 本次会话的实际路由分布 */
const routeStats = computed(() => {
  const map = new Map<string, number>()
  for (const h of history.value) map.set(h.passageName, (map.get(h.passageName) ?? 0) + 1)
  const totalCount = history.value.length
  return [...map.entries()]
    .map(([name, count]) => ({ name, count, rate: totalCount ? count / totalCount : 0 }))
    .sort((a, b) => b.count - a.count)
})

// ---------- 详情 ----------
const drawers = ref<InstanceType<typeof DetailDrawers>>()

function openDetail(orderNo: string) {
  drawers.value?.openOrder(orderNo)
}

function openSubscription(subNo: string | null) {
  drawers.value?.openSubscription(subNo)
}

function onDetailChanged(target: { kind: 'order' | 'subscription'; no: string }) {
  if (target.kind === 'order') {
    if (history.value.some((h) => h.orderNo === target.no)) refreshStatus(target.no, true)
  } else if (result.value) {
    // 订阅侧的操作（如模拟续费、撤销）可能影响当前订单
    refreshStatus(result.value.orderNo, true)
  }
}

function goBindings() {
  if (selectedChannel.value) router.push(`/channels/${selectedChannel.value.id}/bindings`)
}
</script>

<template>
  <div class="page">
    <el-row :gutter="16">
      <el-col :span="10">
        <el-card shadow="never">
          <div class="toolbar">
            <div class="toolbar__title">测试下单</div>
          </div>
          <el-alert type="info" :closable="false" show-icon class="tip">
            以业务方身份发起一笔真实下单，用来端到端验证「渠道 → 路由规则 → 通道」。选择演示（mock）通道时可以直接模拟支付成功。
          </el-alert>

          <el-form ref="formRef" :model="form" :rules="rules" label-width="92px" @submit.prevent>
            <el-form-item label="应用" prop="appId">
              <AppSelect
                v-model="form.appId"
                placeholder="请选择应用"
                :clearable="false"
                width="100%"
                @change="onAppChange"
              />
            </el-form-item>
            <el-form-item label="渠道" prop="channelCode">
              <el-select
                v-model="form.channelCode"
                :placeholder="form.appId === undefined ? '请先选择应用' : '请选择渠道'"
                :disabled="form.appId === undefined"
                :loading="channelsLoading"
                filterable
                style="width: 100%"
                @change="onChannelChange"
              >
                <el-option v-for="c in channels" :key="c.id" :value="c.code" :label="`${c.name}（${c.code}）`">
                  <span>{{ c.name }}</span>
                  <span class="option-sub">{{ c.code }}</span>
                  <el-tag v-if="c.status !== 1" type="info" size="small" class="option-tag">停用</el-tag>
                </el-option>
              </el-select>
              <div v-if="selectedChannel" class="form-help">
                <template v-if="boundProviders.size > 0">
                  该渠道已绑定：
                  <span v-for="[code, count] in boundProviders" :key="code" class="bound-item">
                    {{ meta.providerName(code) }} ×{{ count }}
                  </span>
                </template>
                <span v-else class="text-danger">该渠道还没有启用中的通道，下单会失败。</span>
                <el-button link type="primary" size="small" @click="goBindings">查看通道配置</el-button>
              </div>
            </el-form-item>
            <el-form-item label="支付方式" prop="payMethod">
              <el-select v-model="form.payMethod" placeholder="请选择支付方式" filterable style="width: 100%">
                <el-option-group v-for="g in methodGroups" :key="g.provider" :label="g.label">
                  <el-option v-for="m in g.methods" :key="m.code" :value="m.code" :label="m.name">
                    <span>{{ m.name }}</span>
                    <span class="option-sub">{{ m.code }}</span>
                  </el-option>
                </el-option-group>
              </el-select>
              <div v-if="methodNotBound" class="form-help text-danger">
                所选渠道没有绑定支持该支付方式的通道，下单大概率失败。
              </div>
            </el-form-item>
            <el-form-item label="订阅" prop="subscribe">
              <el-switch
                v-model="form.subscribe"
                :disabled="!subscriptionSupported"
                active-text="订阅（自动续费）"
                inactive-text="一次性支付"
              />
              <div class="form-help">
                <template v-if="subscriptionSupported">
                  打开后下的是「订阅首购」单：支付成功会创建一个订阅，之后每期续费由商店扣款并回调。
                </template>
                <template v-else-if="form.payMethod">
                  「{{ meta.providerName(methodProvider) }}」不支持自动续费订阅，只能下一次性支付单。
                </template>
                <template v-else>选择支持自动续费的支付方式后可用。</template>
              </div>
            </el-form-item>
            <el-form-item v-if="deductSupported" label="签约代扣" prop="signAgreement">
              <el-switch v-model="form.signAgreement" active-text="签约代扣" inactive-text="不签约" />
              <div class="form-help">
                打开后下的是「签约单」：签约成功会建立代扣订阅，之后由本系统按周期发起扣款。与上面的商店订阅互斥。
              </div>
              <div v-if="form.signAgreement" class="period-row">
                <span class="period-row__label">扣款周期</span>
                <el-input-number
                  v-model="form.periodDays"
                  :min="1"
                  :max="365"
                  :step="1"
                  :precision="0"
                  controls-position="right"
                  style="width: 130px"
                />
                <span class="period-row__unit">天</span>
                <span class="text-secondary text-small">默认 30 天</span>
              </div>
            </el-form-item>
            <el-form-item label="金额" prop="amount">
              <MoneyInput v-model="form.amount" />
              <div class="form-help">可以换不同金额多下几单，观察起付金额 / 单笔上限与权重分流是否生效。</div>
            </el-form-item>
            <el-form-item label="商品标题" prop="subject">
              <el-input v-model="form.subject" maxlength="64" />
            </el-form-item>
            <el-form-item label="商品 ID" prop="productId">
              <el-input v-model="form.productId" placeholder="应用商店内购需要填写商品 ID，其它可留空" />
            </el-form-item>
            <el-form-item label="OpenID" prop="openId">
              <el-input v-model="form.openId" placeholder="小程序 / 公众号支付需要，其它可留空" />
            </el-form-item>
            <el-form-item label="用户 ID" prop="userId">
              <el-input v-model="form.userId" placeholder="业务侧用户标识，可留空" />
            </el-form-item>
            <el-form-item>
              <el-button type="primary" :icon="Promotion" :loading="submitting" @click="submit">
                {{ result ? '再下一单' : '发起下单' }}
              </el-button>
            </el-form-item>
          </el-form>
        </el-card>
      </el-col>

      <el-col :span="14">
        <el-card shadow="never" class="result-card">
          <div class="toolbar">
            <div class="toolbar__title">下单结果</div>
            <div v-if="result" class="toolbar__actions">
              <el-button :icon="Refresh" :loading="refreshing" @click="refreshStatus(result.orderNo)">刷新状态</el-button>
              <el-button @click="openDetail(result.orderNo)">订单详情</el-button>
              <el-button v-if="currentSubNo" type="primary" plain @click="openSubscription(currentSubNo)">
                查看订阅
              </el-button>
              <el-button
                v-if="result.provider === 'mock' && currentStatus === 0"
                type="success"
                :loading="mockPaying"
                @click="mockPay"
              >
                模拟支付成功
              </el-button>
            </div>
          </div>

          <el-empty v-if="!result" description="填写左侧表单并发起下单，结果会显示在这里" />

          <template v-else>
            <div class="route-banner">
              <el-icon class="route-banner__icon"><CircleCheckFilled /></el-icon>
              <div>
                <div class="route-banner__label">本单路由到的通道</div>
                <div class="route-banner__name">{{ result.passageName }}</div>
              </div>
              <div class="route-banner__status">
                <EnumTag
                  v-if="result.orderType"
                  :options="ORDER_TYPE"
                  :value="result.orderType"
                  effect="plain"
                  size="large"
                />
                <EnumTag :options="ORDER_STATUS" :value="currentStatus" effect="dark" size="large" />
              </div>
            </div>

            <el-alert
              v-if="isSubOrder(result.orderType)"
              :type="currentSubNo ? 'success' : 'info'"
              show-icon
              :closable="false"
              class="block"
            >
              <template v-if="currentSubNo">
                {{ result.orderType === 3 ? '代扣订阅已创建：' : '订阅已创建：' }}
                <el-link type="primary" underline="never" class="mono" @click="openSubscription(currentSubNo)">
                  {{ currentSubNo }}
                </el-link>
                。{{ result.orderType === 3 ? '之后由本系统按周期发起扣款。' : '演示通道可以在订阅详情里用「模拟事件」体验续费、退订、到期、撤销。' }}
              </template>
              <template v-else-if="currentStatus === 0">
                <template v-if="result.orderType === 3">
                  这是一笔签约单：签约成功后会自动创建代扣订阅{{ result.provider === 'mock' ? '；点右上角「模拟支付成功」即可' : '' }}。
                </template>
                <template v-else>
                  这是一笔订阅首购单，支付成功后会自动创建订阅{{ result.provider === 'mock' ? '；点右上角「模拟支付成功」即可' : '' }}。
                </template>
              </template>
              <template v-else>
                {{ result.orderType === 3 ? '这是一笔签约单' : '这是一笔订阅首购单' }}；订阅号尚未返回，可点「刷新状态」重试。
              </template>
            </el-alert>

            <el-descriptions :column="2" border size="small" class="block">
              <el-descriptions-item label="订单号">
                <span class="mono">{{ result.orderNo }}</span>
              </el-descriptions-item>
              <el-descriptions-item label="商户订单号">
                <span class="mono">{{ result.mchOrderNo }}</span>
              </el-descriptions-item>
              <el-descriptions-item label="金额">
                <span class="money">{{ formatMoney(result.amount) }}</span>
              </el-descriptions-item>
              <el-descriptions-item label="当前状态">
                <EnumTag :options="ORDER_STATUS" :value="currentStatus" size="small" />
                <span v-if="currentPaidAt" class="text-secondary text-small paid-at">
                  支付于 {{ formatTime(currentPaidAt) }}
                </span>
              </el-descriptions-item>
              <el-descriptions-item label="支付类型">
                {{ meta.providerName(result.provider) }}
                <span class="mono text-secondary">{{ result.provider }}</span>
              </el-descriptions-item>
              <el-descriptions-item label="支付方式">
                {{ meta.methodName(result.payMethod) }}
                <span class="mono text-secondary">{{ result.payMethod }}</span>
              </el-descriptions-item>
              <el-descriptions-item label="通道">
                {{ result.passageName }}
                <span class="text-secondary text-small">（ID {{ result.passageId }}）</span>
              </el-descriptions-item>
              <el-descriptions-item label="过期时间">{{ formatTime(result.expireAt) }}</el-descriptions-item>
              <el-descriptions-item label="订单类型">
                <EnumTag :options="ORDER_TYPE" :value="result.orderType ?? 0" size="small" effect="plain" />
              </el-descriptions-item>
              <el-descriptions-item label="订阅号">
                <el-link
                  v-if="currentSubNo"
                  type="primary"
                  underline="never"
                  class="mono"
                  @click="openSubscription(currentSubNo)"
                >
                  {{ currentSubNo }}
                </el-link>
                <span v-else class="text-secondary">{{ isSubOrder(result.orderType) ? '成功后生成' : '-' }}</span>
              </el-descriptions-item>
              <el-descriptions-item label="凭据类型" :span="2">
                <el-tag effect="plain">{{ result.credential?.type || '-' }}</el-tag>
              </el-descriptions-item>
            </el-descriptions>

            <div class="block__title">支付凭据 credential.data</div>
            <JsonViewer :value="result.credential?.data" empty-text="无凭据数据" max-height="280px" />
            <div class="form-help">这是返回给客户端用于调起支付的数据（如预支付参数、二维码内容、跳转地址等）。</div>
          </template>
        </el-card>
      </el-col>
    </el-row>

    <el-card v-if="history.length > 0" shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          本次会话下单记录
          <span class="toolbar__sub">共 {{ history.length }} 单，可以直观看到每一单被路由到了哪条通道</span>
        </div>
      </div>
      <div class="route-stats">
        <div v-for="s in routeStats" :key="s.name" class="route-stat">
          <span class="route-stat__name">{{ s.name }}</span>
          <el-progress :percentage="Math.round(s.rate * 1000) / 10" :stroke-width="8" class="route-stat__bar" />
          <span class="route-stat__count">{{ s.count }} 单</span>
        </div>
      </div>
      <el-table :data="history" size="small" row-key="orderNo" max-height="360">
        <el-table-column label="下单时间" width="170">
          <template #default="{ row }">{{ formatTime(row.time) }}</template>
        </el-table-column>
        <el-table-column label="订单号" min-width="220">
          <template #default="{ row }">
            <el-link type="primary" underline="never" class="mono" @click="openDetail(row.orderNo)">
              {{ row.orderNo }}
            </el-link>
          </template>
        </el-table-column>
        <el-table-column label="支付方式" min-width="150">
          <template #default="{ row }">{{ meta.methodName(row.payMethod) }}</template>
        </el-table-column>
        <el-table-column label="金额" width="120" align="right">
          <template #default="{ row }">
            <span class="money">{{ formatMoney(row.amount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="路由通道" min-width="200">
          <template #default="{ row }">
            <span class="passage-name">{{ row.passageName }}</span>
          </template>
        </el-table-column>
        <el-table-column label="类型 / 订阅号" min-width="200">
          <template #default="{ row }">
            <EnumTag :options="ORDER_TYPE" :value="row.orderType ?? 0" size="small" effect="plain" />
            <el-link
              v-if="row.subNo"
              type="primary"
              underline="never"
              class="mono history-sub"
              @click="openSubscription(row.subNo)"
            >
              {{ row.subNo }}
            </el-link>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110" align="center">
          <template #default="{ row }">
            <EnumTag :options="ORDER_STATUS" :value="row.status" size="small" />
          </template>
        </el-table-column>
        <el-table-column label="操作" width="90" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="refreshStatus(row.orderNo)">刷新</el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <DetailDrawers ref="drawers" @changed="onDetailChanged" />
  </div>
</template>

<style scoped>
.tip {
  margin-bottom: 18px;
  line-height: 1.6;
}
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.option-tag {
  margin-left: 8px;
}
.form-help {
  width: 100%;
}
.bound-item {
  margin-right: 10px;
  color: var(--el-text-color-regular);
}
.result-card {
  min-height: 100%;
}
.route-banner {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-bottom: 16px;
  padding: 14px 18px;
  border-radius: 8px;
  background: linear-gradient(90deg, #eff6ff, #f8fafc);
  border: 1px solid #dbeafe;
}
.route-banner__icon {
  font-size: 34px;
  color: var(--el-color-primary);
}
.route-banner__label {
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.route-banner__name {
  margin-top: 2px;
  font-size: 20px;
  font-weight: 700;
}
.route-banner__status {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}
.history-sub {
  margin-left: 8px;
  font-size: 12px;
}
.block {
  margin-bottom: 16px;
}
.block__title {
  margin-bottom: 8px;
  font-weight: 600;
}
.paid-at {
  margin-left: 8px;
}
.period-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  margin-top: 10px;
}
.period-row__label {
  color: var(--el-text-color-regular);
}
.period-row__unit {
  color: var(--el-text-color-regular);
}
.route-stats {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-bottom: 16px;
  padding: 12px 16px;
  border-radius: 8px;
  background: #f8fafc;
}
.route-stat {
  display: grid;
  grid-template-columns: 240px 1fr 70px;
  align-items: center;
  gap: 14px;
}
.route-stat__name {
  font-weight: 600;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.route-stat__count {
  text-align: right;
  color: var(--el-text-color-secondary);
  font-variant-numeric: tabular-nums;
}
.passage-name {
  font-weight: 600;
}
</style>
