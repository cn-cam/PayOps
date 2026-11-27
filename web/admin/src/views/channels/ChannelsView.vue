<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Plus, Refresh, Search, SetUp } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormItemRule, type FormRules } from 'element-plus'
import { channelsApi } from '@/api/channels'
import type { ChannelItem, ChannelListQuery, EnableStatus } from '@/api/types'
import { usePagedTable } from '@/composables/usePagedTable'
import { useAuthStore } from '@/stores/auth'
import AppSelect from '@/components/AppSelect.vue'
import EnumTag from '@/components/EnumTag.vue'
import TablePager from '@/components/TablePager.vue'
import { confirmAction, validateForm } from '@/utils/common'
import { ENABLE_STATUS } from '@/utils/enums'
import { formatTime } from '@/utils/time'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

interface Query {
  appId: number | undefined
  keyword: string
  status: EnableStatus | undefined
}

const initialAppId = Number(route.query.appId) || undefined
let firstQuery = true

const { query, items, total, page, pageSize, loading, load, search, reset, onPageChange, onSizeChange } =
  usePagedTable<ChannelItem, Query, ChannelListQuery>(
    channelsApi.list,
    () => {
      // 仅首次带上从「应用管理」跳转过来的 appId，点击重置时清空
      const appId = firstQuery ? initialAppId : undefined
      firstQuery = false
      return { appId, keyword: '', status: undefined }
    },
    (q) => ({ appId: q.appId, keyword: q.keyword, status: q.status }),
  )

/** 常用分发渠道，仅用于新建时快速填写编码与名称 */
const presets = [
  { code: 'official', name: '官方包' },
  { code: 'huawei', name: '华为' },
  { code: 'oppo', name: 'OPPO' },
  { code: 'vivo', name: 'vivo' },
  { code: 'xiaomi', name: '小米' },
  { code: 'honor', name: '荣耀' },
  { code: 'appstore', name: 'App Store' },
  { code: 'douyin_mp', name: '抖音小程序' },
  { code: 'wechat_mp', name: '微信小程序' },
]

const codeRule: FormItemRule = {
  pattern: /^[A-Za-z][A-Za-z0-9_-]{1,31}$/,
  message: '2~32 位，字母开头，仅支持字母、数字、下划线、中划线',
  trigger: 'blur',
}

// ---------- 新建 / 编辑 ----------
const dialogVisible = ref(false)
const editingId = ref<number | null>(null)
const saving = ref(false)
const formRef = ref<FormInstance>()
const form = reactive({
  appId: undefined as number | undefined,
  code: '',
  name: '',
  remark: '',
  status: 1 as EnableStatus,
})

const rules: FormRules = {
  appId: [{ required: true, message: '请选择所属应用', trigger: 'change' }],
  code: [{ required: true, message: '请输入渠道编码', trigger: 'blur' }, codeRule],
  name: [{ required: true, message: '请输入渠道名称', trigger: 'blur' }],
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { appId: query.appId, code: '', name: '', remark: '', status: 1 })
  dialogVisible.value = true
}

function openEdit(row: ChannelItem) {
  editingId.value = row.id
  Object.assign(form, {
    appId: row.appId,
    code: row.code,
    name: row.name,
    remark: row.remark ?? '',
    status: row.status,
  })
  dialogVisible.value = true
}

function applyPreset(preset: { code: string; name: string }) {
  form.code = preset.code
  form.name = preset.name
  formRef.value?.clearValidate(['code', 'name'])
}

async function submit() {
  if (!(await validateForm(formRef.value))) return
  saving.value = true
  try {
    if (editingId.value === null) {
      const res = await channelsApi.create({
        appId: form.appId as number,
        code: form.code.trim(),
        name: form.name.trim(),
        remark: form.remark.trim(),
        status: form.status,
      })
      dialogVisible.value = false
      load()
      if (await confirmAction('渠道已创建。现在去为它配置支付通道吗？', '创建成功', 'info')) {
        goBindings(res.id)
      }
    } else {
      await channelsApi.update(editingId.value, {
        name: form.name.trim(),
        remark: form.remark.trim(),
        status: form.status,
      })
      dialogVisible.value = false
      ElMessage.success('已保存')
      load()
    }
  } finally {
    saving.value = false
  }
}

// ---------- 启停 / 删除 ----------
const statusLoadingId = ref<number | null>(null)

async function changeStatus(row: ChannelItem, value: EnableStatus) {
  if (value === 0 && !(await confirmAction(`停用后渠道「${row.name}」将无法发起新的支付，确定停用吗？`))) return
  statusLoadingId.value = row.id
  try {
    await channelsApi.update(row.id, { name: row.name, remark: row.remark ?? '', status: value })
    row.status = value
    ElMessage.success(value === 1 ? '已启用' : '已停用')
  } finally {
    statusLoadingId.value = null
  }
}

async function remove(row: ChannelItem) {
  const extra = row.bindingCount > 0 ? `其下 ${row.bindingCount} 条通道绑定会一并删除。` : ''
  if (!(await confirmAction(`确定删除渠道「${row.appName} / ${row.name}」吗？${extra}删除后不可恢复。`, '删除渠道'))) {
    return
  }
  await channelsApi.remove(row.id)
  ElMessage.success('已删除')
  load()
}

// ---------- 复制 ----------
const copyVisible = ref(false)
const copySaving = ref(false)
const copyFormRef = ref<FormInstance>()
const copySource = ref<ChannelItem | null>(null)
const copyForm = reactive({ targetAppId: undefined as number | undefined, code: '', name: '' })

const copyRules: FormRules = {
  targetAppId: [{ required: true, message: '请选择目标应用', trigger: 'change' }],
  code: [{ required: true, message: '请输入新渠道编码', trigger: 'blur' }, codeRule],
  name: [{ required: true, message: '请输入新渠道名称', trigger: 'blur' }],
}

function openCopy(row: ChannelItem) {
  copySource.value = row
  Object.assign(copyForm, { targetAppId: undefined, code: row.code, name: row.name })
  copyVisible.value = true
}

async function submitCopy() {
  if (!copySource.value) return
  if (!(await validateForm(copyFormRef.value))) return
  copySaving.value = true
  try {
    const res = await channelsApi.copy(copySource.value.id, {
      targetAppId: copyForm.targetAppId as number,
      code: copyForm.code.trim(),
      name: copyForm.name.trim(),
    })
    copyVisible.value = false
    load()
    if (await confirmAction('渠道及其通道绑定已复制。现在去查看新渠道的通道配置吗？', '复制成功', 'info')) {
      goBindings(res.id)
    }
  } finally {
    copySaving.value = false
  }
}

function goBindings(id: number) {
  router.push(`/channels/${id}/bindings`)
}
</script>

<template>
  <div class="page">
    <el-card shadow="never" class="filter-card">
      <el-form inline @submit.prevent="search">
        <el-form-item label="所属应用">
          <AppSelect v-model="query.appId" @change="search" />
        </el-form-item>
        <el-form-item label="关键字">
          <el-input
            v-model="query.keyword"
            placeholder="渠道编码 / 名称"
            clearable
            style="width: 200px"
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
          渠道列表
          <span class="toolbar__sub">渠道 = 应用的分发渠道；每个渠道可绑定多条支付通道并设置权重、起付金额等路由规则</span>
        </div>
        <div class="toolbar__actions">
          <el-button v-if="auth.canWrite" type="primary" :icon="Plus" @click="openCreate">新建渠道</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="items" row-key="id">
        <el-table-column label="所属应用" min-width="150">
          <template #default="{ row }">
            <div class="cell-stack">
              <span>{{ row.appName }}</span>
              <span class="mono text-secondary">{{ row.appCode }}</span>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="渠道编码" min-width="120">
          <template #default="{ row }">
            <span class="mono">{{ row.code }}</span>
          </template>
        </el-table-column>
        <el-table-column label="渠道名称" prop="name" min-width="120" show-overflow-tooltip />
        <el-table-column label="已绑通道" width="100" align="center">
          <template #default="{ row }">
            <el-tag :type="row.bindingCount > 0 ? 'primary' : 'danger'" effect="plain" round>
              {{ row.bindingCount }}
            </el-tag>
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
              @change="(v: string | number | boolean) => changeStatus(row as ChannelItem, v === 1 ? 1 : 0)"
            />
            <EnumTag v-else :options="ENABLE_STATUS" :value="row.status" />
          </template>
        </el-table-column>
        <el-table-column label="备注" prop="remark" min-width="120" show-overflow-tooltip />
        <el-table-column label="更新时间" width="170">
          <template #default="{ row }">{{ formatTime(row.updatedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" :width="auth.canWrite ? 250 : 120" fixed="right">
          <template #default="{ row }">
            <el-button type="primary" size="small" :icon="SetUp" @click="goBindings(row.id)">通道配置</el-button>
            <template v-if="auth.canWrite">
              <el-button link type="primary" @click="openEdit(row as ChannelItem)">编辑</el-button>
              <el-button link type="primary" @click="openCopy(row as ChannelItem)">复制</el-button>
              <el-button link type="danger" @click="remove(row as ChannelItem)">删除</el-button>
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
      :title="editingId === null ? '新建渠道' : '编辑渠道'"
      width="580px"
      :close-on-click-modal="false"
      destroy-on-close
    >
      <el-form ref="formRef" :model="form" :rules="rules" label-width="96px" @submit.prevent>
        <el-form-item label="所属应用" prop="appId">
          <AppSelect
            v-model="form.appId"
            placeholder="请选择应用"
            :clearable="false"
            :disabled="editingId !== null"
            width="100%"
          />
        </el-form-item>
        <el-form-item v-if="editingId === null" label="常用渠道">
          <div class="preset-list">
            <el-tag
              v-for="p in presets"
              :key="p.code"
              class="preset-tag"
              :effect="form.code === p.code ? 'dark' : 'plain'"
              @click="applyPreset(p)"
            >
              {{ p.name }}
            </el-tag>
          </div>
          <div class="form-help">点击可快速填入编码与名称，也可以自行输入。</div>
        </el-form-item>
        <el-form-item label="渠道编码" prop="code">
          <el-input
            v-model="form.code"
            :disabled="editingId !== null"
            placeholder="如 huawei，创建后不可修改"
            maxlength="64"
          />
          <div class="form-help">业务方下单时传入的 channelCode，同一应用内唯一。</div>
        </el-form-item>
        <el-form-item label="渠道名称" prop="name">
          <el-input v-model="form.name" placeholder="如 华为" maxlength="64" />
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

    <!-- 复制 -->
    <el-dialog v-model="copyVisible" title="复制渠道" width="540px" :close-on-click-modal="false" destroy-on-close>
      <el-alert
        v-if="copySource"
        type="info"
        show-icon
        :closable="false"
        class="copy-alert"
        :title="`将「${copySource.appName} / ${copySource.name}」连同 ${copySource.bindingCount} 条通道绑定复制为一个新渠道`"
      />
      <el-form ref="copyFormRef" :model="copyForm" :rules="copyRules" label-width="96px" @submit.prevent>
        <el-form-item label="目标应用" prop="targetAppId">
          <AppSelect v-model="copyForm.targetAppId" placeholder="请选择目标应用" :clearable="false" width="100%" />
          <div class="form-help">可以选择其它应用，也可以选择同一应用（此时渠道编码需不同）。</div>
        </el-form-item>
        <el-form-item label="新渠道编码" prop="code">
          <el-input v-model="copyForm.code" maxlength="64" />
        </el-form-item>
        <el-form-item label="新渠道名称" prop="name">
          <el-input v-model="copyForm.name" maxlength="64" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="copyVisible = false">取消</el-button>
        <el-button type="primary" :loading="copySaving" @click="submitCopy">复制</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.preset-list {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.preset-tag {
  cursor: pointer;
  user-select: none;
}
.copy-alert {
  margin-bottom: 16px;
}
</style>
