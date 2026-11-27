<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { CopyDocument, Plus, Refresh, Search } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { appsApi } from '@/api/apps'
import type { AppCreateInput, AppItem, AppListQuery, EnableStatus } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import EnumTag from '@/components/EnumTag.vue'
import TablePager from '@/components/TablePager.vue'
import { confirmAction, copyText, validateForm } from '@/utils/common'
import { ENABLE_STATUS } from '@/utils/enums'
import { formatTime } from '@/utils/time'

const router = useRouter()
const auth = useAuthStore()
const meta = useMetaStore()

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<AppItem, { keyword: string; status: EnableStatus | undefined }, AppListQuery>(
    appsApi.list,
    () => ({ keyword: '', status: undefined }),
    (q) => ({ keyword: q.keyword, status: q.status }),
  )

// 注意：模板的属性值里不要直接写 "//"（vue-tsc 3.3 会把它当成注释处理），含 URL 的文案放到脚本常量里
const notifyUrlPlaceholder = 'https://your-app.example.com/pay/notify'

// ---------- 新建 / 编辑 ----------
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const saving = ref(false)
const formRef = ref<FormInstance>()
const form = reactive<AppCreateInput>({ appCode: '', name: '', notifyUrl: '', remark: '', status: 1 })

const rules: FormRules = {
  appCode: [
    { required: true, message: '请输入应用编码', trigger: 'blur' },
    {
      pattern: /^[A-Za-z][A-Za-z0-9_-]{1,31}$/,
      message: '2~32 位，字母开头，仅支持字母、数字、下划线、中划线',
      trigger: 'blur',
    },
  ],
  name: [{ required: true, message: '请输入应用名称', trigger: 'blur' }],
  notifyUrl: [{ pattern: /^https?:\/\/\S+$/i, message: '请输入 http(s):// 开头的地址', trigger: 'blur' }],
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { appCode: '', name: '', notifyUrl: '', remark: '', status: 1 })
  dialogVisible.value = true
}

function openEdit(row: AppItem) {
  editingId.value = row.id
  Object.assign(form, {
    appCode: row.appCode,
    name: row.name,
    notifyUrl: row.notifyUrl ?? '',
    remark: row.remark ?? '',
    status: row.status,
  })
  dialogVisible.value = true
}

async function submit() {
  if (!(await validateForm(formRef.value))) return
  saving.value = true
  try {
    const payload = {
      name: form.name.trim(),
      notifyUrl: form.notifyUrl.trim(),
      remark: form.remark.trim(),
      status: form.status,
    }
    if (editingId.value === null) {
      const appCode = form.appCode.trim()
      const res = await appsApi.create({ appCode, ...payload })
      dialogVisible.value = false
      ElMessage.success('应用已创建')
      showSecret({ appCode, name: payload.name, appSecret: res.appSecret, mode: 'created' })
    } else {
      await appsApi.update(editingId.value, payload)
      dialogVisible.value = false
      ElMessage.success('已保存')
    }
    load()
    meta.loadApps()
  } finally {
    saving.value = false
  }
}

// ---------- 启停 / 删除 ----------
const statusLoadingId = ref<number | null>(null)

async function changeStatus(row: AppItem, value: EnableStatus) {
  if (value === 0 && !(await confirmAction(`停用后「${row.name}」将无法发起新的支付，确定停用吗？`))) return
  statusLoadingId.value = row.id
  try {
    await appsApi.update(row.id, {
      name: row.name,
      notifyUrl: row.notifyUrl ?? '',
      remark: row.remark ?? '',
      status: value,
    })
    row.status = value
    ElMessage.success(value === 1 ? '已启用' : '已停用')
    meta.loadApps()
  } finally {
    statusLoadingId.value = null
  }
}

async function remove(row: AppItem) {
  const tip =
    row.channelCount > 0
      ? `应用「${row.name}」下还有 ${row.channelCount} 个渠道，需先删除渠道才能删除应用。仍要尝试删除吗？`
      : `确定删除应用「${row.name}」吗？删除后不可恢复。`
  if (!(await confirmAction(tip, '删除应用'))) return
  await appsApi.remove(row.id)
  ElMessage.success('已删除')
  load()
  meta.loadApps()
}

// ---------- 密钥 ----------
interface SecretView {
  appCode: string
  name: string
  appSecret: string
  mode: 'created' | 'reset' | 'view'
}

const secretVisible = ref(false)
const secret = ref<SecretView>({ appCode: '', name: '', appSecret: '', mode: 'view' })

function showSecret(view: SecretView) {
  secret.value = view
  secretVisible.value = true
}

async function viewSecret(row: AppItem) {
  const res = await appsApi.secret(row.id)
  showSecret({ appCode: row.appCode, name: row.name, appSecret: res.appSecret, mode: 'view' })
}

async function resetSecret(row: AppItem) {
  const ok = await confirmAction(
    `重置后「${row.name}」的旧密钥立即失效，业务方必须同步更换为新密钥，否则下单与通知验签都会失败。确定重置吗？`,
    '重置密钥',
    'error',
  )
  if (!ok) return
  const res = await appsApi.resetSecret(row.id)
  ElMessage.success('密钥已重置')
  showSecret({ appCode: row.appCode, name: row.name, appSecret: res.appSecret, mode: 'reset' })
}

function goChannels(row: AppItem) {
  router.push({ path: '/channels', query: { appId: String(row.id) } })
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="应用编码 / 名称"
            clearable
            style="width: 220px"
            @keyup.enter="search"
            @clear="search"
          />
        </el-form-item>
        <el-form-item label="状态">
          <el-select v-model="query.status" placeholder="全部" clearable style="width: 120px" @change="search">
            <el-option v-for="s in ENABLE_STATUS" :key="s.value" :value="s.value" :label="s.label" />
          </el-select>
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
          应用列表
          <span class="toolbar__sub">应用 = 公司自有的业务产品，每个应用下可以有多个分发渠道</span>
        </div>
        <div class="toolbar__actions">
          <el-button v-if="auth.canWrite" type="primary" :icon="Plus" @click="openCreate">新建应用</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id">
        <el-table-column label="应用编码" min-width="130">
          <template #default="{ row }">
            <span class="mono">{{ row.appCode }}</span>
          </template>
        </el-table-column>
        <el-table-column label="应用名称" prop="name" min-width="130" show-overflow-tooltip />
        <el-table-column label="默认通知地址" min-width="160" show-overflow-tooltip>
          <template #default="{ row }">
            <span v-if="row.notifyUrl" class="mono">{{ row.notifyUrl }}</span>
            <span v-else class="text-secondary">未设置</span>
          </template>
        </el-table-column>
        <el-table-column label="渠道数" width="90" align="center">
          <template #default="{ row }">
            <el-button link type="primary" @click="goChannels(row as AppItem)">{{ row.channelCount }}</el-button>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90" align="center">
          <template #default="{ row }">
            <el-switch
              v-if="auth.canWrite"
              :model-value="row.status"
              :active-value="1"
              :inactive-value="0"
              :loading="statusLoadingId === row.id"
              @change="(v: string | number | boolean) => changeStatus(row as AppItem, v === 1 ? 1 : 0)"
            />
            <EnumTag v-else :options="ENABLE_STATUS" :value="row.status" />
          </template>
        </el-table-column>
        <el-table-column label="备注" prop="remark" min-width="100" show-overflow-tooltip />
        <el-table-column label="创建时间" width="170">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" :width="auth.canViewSecret ? 250 : auth.canWrite ? 110 : 70" fixed="right">
          <template #default="{ row }">
            <el-button v-if="!auth.canWrite" link type="primary" @click="goChannels(row as AppItem)">渠道</el-button>
            <template v-if="auth.canWrite">
              <el-button link type="primary" @click="openEdit(row as AppItem)">编辑</el-button>
            </template>
            <template v-if="auth.canViewSecret">
              <el-button link type="primary" @click="viewSecret(row as AppItem)">查看密钥</el-button>
              <el-button link type="warning" @click="resetSecret(row as AppItem)">重置密钥</el-button>
            </template>
            <template v-if="auth.canWrite">
              <el-button link type="danger" @click="remove(row as AppItem)">删除</el-button>
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

    <!-- 新建 / 编辑 -->
    <el-dialog
      v-model="dialogVisible"
      :title="editingId === null ? '新建应用' : '编辑应用'"
      width="560px"
      :close-on-click-modal="false"
      destroy-on-close
    >
      <el-form ref="formRef" :model="form" :rules="rules" label-width="110px" @submit.prevent>
        <el-form-item label="应用编码" prop="appCode">
          <el-input
            v-model="form.appCode"
            :disabled="editingId !== null"
            placeholder="如 game_x，创建后不可修改"
            maxlength="64"
          />
          <div class="form-help">业务方调用下单接口时用它标识应用（appCode）。</div>
        </el-form-item>
        <el-form-item label="应用名称" prop="name">
          <el-input v-model="form.name" placeholder="如 游戏 X" maxlength="64" />
        </el-form-item>
        <el-form-item label="默认通知地址" prop="notifyUrl">
          <el-input v-model="form.notifyUrl" :placeholder="notifyUrlPlaceholder" />
          <div class="form-help">支付 / 退款结果会以该地址通知业务服务端；下单时未单独指定通知地址则使用它。</div>
        </el-form-item>
        <el-form-item label="状态" prop="status">
          <el-radio-group v-model="form.status">
            <el-radio v-for="s in ENABLE_STATUS" :key="s.value" :value="s.value">{{ s.label }}</el-radio>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="备注" prop="remark">
          <el-input v-model="form.remark" type="textarea" :rows="2" maxlength="255" show-word-limit />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">保存</el-button>
      </template>
    </el-dialog>

    <!-- 密钥 -->
    <el-dialog
      v-model="secretVisible"
      :title="secret.mode === 'created' ? '应用创建成功' : secret.mode === 'reset' ? '密钥已重置' : '应用密钥'"
      width="560px"
      :close-on-click-modal="false"
      :close-on-press-escape="secret.mode === 'view'"
    >
      <el-alert
        v-if="secret.mode !== 'view'"
        type="warning"
        show-icon
        :closable="false"
        title="请立即复制并妥善保存密钥"
        description="密钥仅在创建与重置时返回，关闭后只有管理员可以通过「查看密钥」再次查看。"
        class="secret-alert"
      />
      <el-descriptions :column="1" border>
        <el-descriptions-item label="应用">{{ secret.name }}</el-descriptions-item>
        <el-descriptions-item label="appCode">
          <span class="mono">{{ secret.appCode }}</span>
        </el-descriptions-item>
        <el-descriptions-item label="appSecret">
          <div class="secret-row">
            <span class="mono secret-text">{{ secret.appSecret }}</span>
            <el-button type="primary" size="small" :icon="CopyDocument" @click="copyText(secret.appSecret)">
              复制
            </el-button>
          </div>
        </el-descriptions-item>
      </el-descriptions>
      <template #footer>
        <el-button type="primary" @click="secretVisible = false">
          {{ secret.mode === 'view' ? '关闭' : '我已保存' }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.secret-alert {
  margin-bottom: 16px;
}
.secret-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.secret-text {
  word-break: break-all;
  font-size: 13px;
}
</style>
