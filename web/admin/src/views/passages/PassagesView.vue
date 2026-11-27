<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import { CircleCheckFilled, CircleCloseFilled, CopyDocument, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage } from 'element-plus'
import { passagesApi } from '@/api/passages'
import type { EnableStatus, PassageDetail, PassageItem, PassageListQuery, PassageTestResult } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import EnumTag from '@/components/EnumTag.vue'
import ProviderSelect from '@/components/ProviderSelect.vue'
import TablePager from '@/components/TablePager.vue'
import { confirmAction, copyText } from '@/utils/common'
import { ENABLE_STATUS } from '@/utils/enums'
import { formatFeeRateBp } from '@/utils/money'
import { formatTime } from '@/utils/time'
import PassageDrawer from './PassageDrawer.vue'

const route = useRoute()
const auth = useAuthStore()
const meta = useMetaStore()

interface Query {
  provider: string | undefined
  keyword: string
  status: EnableStatus | undefined
}

const initialProvider = typeof route.query.provider === 'string' && route.query.provider ? route.query.provider : undefined
let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<PassageItem, Query, PassageListQuery>(
    passagesApi.list,
    () => {
      const provider = firstQuery ? initialProvider : undefined
      firstQuery = false
      return { provider, keyword: '', status: undefined }
    },
    (q) => ({ provider: q.provider, keyword: q.keyword, status: q.status }),
  )

onMounted(() => meta.loadProviders())

// ---------- 新建 / 编辑 ----------
const drawerVisible = ref(false)
const editingId = ref<number | null>(null)

function openCreate() {
  editingId.value = null
  drawerVisible.value = true
}

function openEdit(row: PassageItem) {
  editingId.value = row.id
  drawerVisible.value = true
}

async function onSaved(payload: { id: number; created: boolean; test: boolean }) {
  load()
  if (payload.created) {
    // 新建后展示回调地址，提醒去开放平台配置
    const detail = await passagesApi.get(payload.id)
    showNotifyInfo(detail)
  }
  if (payload.test) await runTest(payload.id)
}

// ---------- 回调地址提示 ----------
const notifyVisible = ref(false)
const notifyInfo = ref<PassageDetail | PassageItem | null>(null)

function showNotifyInfo(item: PassageDetail | PassageItem) {
  notifyInfo.value = item
  notifyVisible.value = true
}

// ---------- 启停 ----------
const busyId = ref<number | null>(null)

async function changeStatus(row: PassageItem, value: EnableStatus) {
  if (value === 0 && row.bindingCount > 0) {
    const ok = await confirmAction(
      `通道「${row.name}」已被 ${row.bindingCount} 个渠道绑定，停用后这些渠道都不会再路由到它。确定停用吗？`,
    )
    if (!ok) return
  }
  busyId.value = row.id
  try {
    // 更新接口需要完整配置：先取详情（密钥为掩码），原样回传即表示不修改
    const detail = await passagesApi.get(row.id)
    await passagesApi.update(row.id, {
      name: detail.name,
      status: value,
      feeRateBp: detail.feeRateBp ?? 0,
      remark: detail.remark ?? '',
      config: detail.config ?? {},
    })
    row.status = value
    ElMessage.success(value === 1 ? '已启用' : '已停用')
  } finally {
    busyId.value = null
  }
}

// ---------- 配置自检 ----------
const testVisible = ref(false)
const testResult = ref<PassageTestResult | null>(null)
const testingId = ref<number | null>(null)
const testName = ref('')

async function runTest(id: number, name?: string) {
  testingId.value = id
  try {
    const res = await passagesApi.test(id)
    testName.value = name ?? items.value.find((i) => i.id === id)?.name ?? ''
    testResult.value = res
    testVisible.value = true
  } finally {
    testingId.value = null
  }
}

// ---------- 删除 ----------
async function remove(row: PassageItem) {
  const tip =
    row.bindingCount > 0
      ? `通道「${row.name}」已被 ${row.bindingCount} 个渠道绑定，需先在渠道通道配置中移除后才能删除。仍要尝试删除吗？`
      : `确定删除通道「${row.name}」吗？删除后不可恢复。`
  if (!(await confirmAction(tip, '删除通道'))) return
  await passagesApi.remove(row.id)
  ElMessage.success('已删除')
  load()
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="支付类型">
          <ProviderSelect v-model="query.provider" @change="search" />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 120px" @change="search">
            <el-option v-for="s in ENABLE_STATUS" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
        </el-form-item>
        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="通道名称"
            clearable
            style="width: 200px"
            @keyup.enter="search"
            @clear="search"
          />
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
          通道列表
          <span class="toolbar__sub">通道 = 某个支付类型下的一套商户 / 应用凭证，可被多个渠道复用</span>
        </div>
        <div class="toolbar__actions">
          <el-button v-if="auth.canWrite" type="primary" :icon="Plus" @click="openCreate">新建通道</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id">
        <el-table-column label="通道名称" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">
            <span class="passage-name">{{ row.name }}</span>
          </template>
        </el-table-column>
        <el-table-column label="支付类型" min-width="150">
          <template #default="{ row }">
            <div class="cell-stack cell-stack--tight">
              <el-tag effect="plain">{{ row.providerName || meta.providerName(row.provider) }}</el-tag>
              <span
                v-if="
                  meta.getProvider(row.provider)?.capabilities?.bill ||
                  meta.getProvider(row.provider)?.capabilities?.subscription
                "
                class="cap-tags"
              >
                <el-tag v-if="meta.getProvider(row.provider)?.capabilities?.bill" type="success" size="small">
                  可对账
                </el-tag>
                <el-tag v-if="meta.getProvider(row.provider)?.capabilities?.subscription" type="warning" size="small">
                  支持订阅
                </el-tag>
              </span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90" align="center">
          <template #default="{ row }">
            <el-switch
              v-if="auth.canWrite"
              :model-value="row.status"
              :active-value="1"
              :inactive-value="0"
              :loading="busyId === row.id"
              @change="(v: string | number | boolean) => changeStatus(row as PassageItem, v === 1 ? 1 : 0)"
            />
            <EnumTag v-else :options="ENABLE_STATUS" :value="row.status" />
          </template>
        </el-table-column>
        <el-table-column label="手续费率" width="100" align="right">
          <template #default="{ row }">
            <el-tooltip
              :content="
                row.feeRateBp
                  ? `万分比 ${row.feeRateBp}，用于手续费核对`
                  : '未配置费率，对账时不做手续费核对'
              "
              placement="top"
            >
              <span class="fee-rate" :class="{ 'text-secondary': !row.feeRateBp }">
                {{ formatFeeRateBp(row.feeRateBp) }}
              </span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="绑定渠道数" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="row.bindingCount > 0 ? 'primary' : 'info'" effect="plain" round>
              {{ row.bindingCount }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="上游回调地址" min-width="200">
          <template #default="{ row }">
            <div v-if="row.notifyUrl" class="notify-cell">
              <span class="mono ellipsis" :title="row.notifyUrl">{{ row.notifyUrl }}</span>
              <el-button link type="primary" :icon="CopyDocument" @click="copyText(row.notifyUrl)" />
              <el-button link type="primary" @click="showNotifyInfo(row as PassageItem)">说明</el-button>
            </div>
            <span v-else class="text-secondary">-</span>
          </template>
        </el-table-column>
        <el-table-column label="备注" prop="remark" min-width="80" show-overflow-tooltip />
        <el-table-column label="更新时间" width="160">
          <template #default="{ row }">{{ formatTime(row.updatedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" :width="auth.canWrite ? 186 : 80" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row as PassageItem)">
              {{ auth.canWrite ? '编辑' : '查看' }}
            </el-button>
            <template v-if="auth.canWrite">
              <el-button link type="primary" :loading="testingId === row.id" @click="runTest(row.id, row.name)">
                配置自检
              </el-button>
              <el-button link type="danger" @click="remove(row as PassageItem)">删除</el-button>
            </template>
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

    <PassageDrawer
      v-model="drawerVisible"
      :passage-id="editingId"
      :default-provider="query.provider"
      @saved="onSaved"
    />

    <!-- 回调地址说明 -->
    <el-dialog v-model="notifyVisible" title="上游回调地址" width="620px">
      <template v-if="notifyInfo">
        <el-descriptions :column="1" border>
          <el-descriptions-item label="通道">{{ notifyInfo.name }}</el-descriptions-item>
          <el-descriptions-item label="支付类型">
            {{ notifyInfo.providerName || meta.providerName(notifyInfo.provider) }}
          </el-descriptions-item>
          <el-descriptions-item label="回调地址">
            <div v-if="notifyInfo.notifyUrl" class="notify-row">
              <span class="mono notify-row__url">{{ notifyInfo.notifyUrl }}</span>
              <el-button type="primary" size="small" :icon="CopyDocument" @click="copyText(notifyInfo.notifyUrl)">
                复制
              </el-button>
            </div>
            <span v-else class="text-secondary">-</span>
          </el-descriptions-item>
        </el-descriptions>
        <el-alert
          v-if="meta.getProvider(notifyInfo.provider)?.notifyNote"
          type="info"
          show-icon
          :closable="false"
          title="配置说明"
          class="notify-note"
        >
          <div class="notify-note__text">{{ meta.getProvider(notifyInfo.provider)?.notifyNote }}</div>
        </el-alert>
      </template>
      <template #footer>
        <el-button type="primary" @click="notifyVisible = false">知道了</el-button>
      </template>
    </el-dialog>

    <!-- 自检结果 -->
    <el-dialog v-model="testVisible" title="配置自检结果" width="520px">
      <div v-if="testResult" class="test-result" :class="testResult.ok ? 'test-result--ok' : 'test-result--fail'">
        <el-icon class="test-result__icon">
          <CircleCheckFilled v-if="testResult.ok" />
          <CircleCloseFilled v-else />
        </el-icon>
        <div>
          <div class="test-result__title">
            {{ testName ? `「${testName}」` : '' }}{{ testResult.ok ? '自检通过' : '自检未通过' }}
          </div>
          <div class="test-result__msg">{{ testResult.message || (testResult.ok ? '配置检查正常' : '配置存在问题') }}</div>
        </div>
      </div>
      <div class="form-help">自检只检查必填项、密钥 / 证书格式等本地可验证的内容，不代表上游一定可用；建议再用「测试下单」验证。</div>
      <template #footer>
        <el-button type="primary" @click="testVisible = false">关闭</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.passage-name {
  font-weight: 600;
}
.fee-rate {
  font-variant-numeric: tabular-nums;
  font-weight: 500;
  cursor: help;
}
.cap-tags {
  display: inline-flex;
  flex-wrap: nowrap;
  gap: 4px;
}
.notify-cell {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
}
.notify-cell .ellipsis {
  flex: 1;
  min-width: 0;
}
.notify-cell .el-button + .el-button {
  margin-left: 4px;
}
.notify-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.notify-row__url {
  word-break: break-all;
}
.notify-note {
  margin-top: 14px;
}
.notify-note__text {
  white-space: pre-wrap;
  line-height: 1.6;
}
.test-result {
  display: flex;
  align-items: flex-start;
  gap: 14px;
  padding: 16px;
  border-radius: 8px;
}
.test-result--ok {
  background: #f0fdf4;
}
.test-result--fail {
  background: #fef2f2;
}
.test-result__icon {
  font-size: 32px;
}
.test-result--ok .test-result__icon {
  color: var(--el-color-success);
}
.test-result--fail .test-result__icon {
  color: var(--el-color-danger);
}
.test-result__title {
  font-size: 15px;
  font-weight: 600;
}
.test-result__msg {
  margin-top: 4px;
  color: #475569;
  line-height: 1.6;
  white-space: pre-wrap;
  word-break: break-all;
}
</style>
