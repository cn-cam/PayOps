<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { Plus, Refresh } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { usersApi } from '@/api/users'
import type { AdminUser, EnableStatus, Role } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import EnumTag from '@/components/EnumTag.vue'
import { confirmAction, validateForm } from '@/utils/common'
import { ENABLE_STATUS, ROLES } from '@/utils/enums'
import { formatTime } from '@/utils/time'

const auth = useAuthStore()

const users = ref<AdminUser[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    users.value = (await usersApi.list()) ?? []
  } finally {
    loading.value = false
  }
}

onMounted(load)

function isSelf(row: AdminUser): boolean {
  return row.id === auth.user?.id
}

const roleHelp: Record<Role, string> = {
  admin: '全部权限，包括用户管理、查看 / 重置应用密钥',
  operator: '可配置应用 / 渠道 / 通道并操作订单，不能管理用户、不能查看密钥',
  viewer: '只读，不能做任何修改',
}

// ---------- 新建 / 编辑 ----------
const dialogVisible = ref(false)
const editing = ref<AdminUser | null>(null)
const saving = ref(false)
const formRef = ref<FormInstance>()
const form = reactive({
  username: '',
  displayName: '',
  password: '',
  role: 'operator' as Role,
  status: 1 as EnableStatus,
})

const rules: FormRules = {
  username: [
    { required: true, message: '请输入用户名', trigger: 'blur' },
    { pattern: /^[A-Za-z0-9_.-]{3,32}$/, message: '3~32 位，仅支持字母、数字、下划线、中划线、点', trigger: 'blur' },
  ],
  displayName: [{ required: true, message: '请输入显示名', trigger: 'blur' }],
  password: [
    { required: true, message: '请输入初始密码', trigger: 'blur' },
    { min: 6, message: '密码至少 6 位', trigger: 'blur' },
  ],
  role: [{ required: true, message: '请选择角色', trigger: 'change' }],
}

function openCreate() {
  editing.value = null
  Object.assign(form, { username: '', displayName: '', password: '', role: 'operator', status: 1 })
  dialogVisible.value = true
}

function openEdit(row: AdminUser) {
  editing.value = row
  Object.assign(form, {
    username: row.username,
    displayName: row.displayName,
    password: '',
    role: row.role,
    status: row.status,
  })
  dialogVisible.value = true
}

async function submit() {
  if (!(await validateForm(formRef.value))) return
  saving.value = true
  try {
    if (editing.value) {
      await usersApi.update(editing.value.id, {
        displayName: form.displayName.trim(),
        role: form.role,
        status: form.status,
      })
      // 修改的是自己时同步刷新右上角显示
      if (isSelf(editing.value)) await auth.fetchMe()
    } else {
      await usersApi.create({
        username: form.username.trim(),
        displayName: form.displayName.trim(),
        password: form.password,
        role: form.role,
        status: form.status,
      })
    }
    dialogVisible.value = false
    ElMessage.success('已保存')
    load()
  } finally {
    saving.value = false
  }
}

// ---------- 启停 / 删除 ----------
const busyId = ref<number | null>(null)

async function changeStatus(row: AdminUser, value: EnableStatus) {
  if (value === 0 && !(await confirmAction(`停用后「${row.username}」将无法登录，确定停用吗？`))) return
  busyId.value = row.id
  try {
    await usersApi.update(row.id, { displayName: row.displayName, role: row.role, status: value })
    row.status = value
    ElMessage.success(value === 1 ? '已启用' : '已停用')
  } finally {
    busyId.value = null
  }
}

async function remove(row: AdminUser) {
  if (!(await confirmAction(`确定删除用户「${row.username}」吗？删除后不可恢复。`, '删除用户'))) return
  await usersApi.remove(row.id)
  ElMessage.success('已删除')
  load()
}

// ---------- 重置密码 ----------
const pwdVisible = ref(false)
const pwdSaving = ref(false)
const pwdFormRef = ref<FormInstance>()
const pwdTarget = ref<AdminUser | null>(null)
const pwdForm = reactive({ password: '', confirm: '' })

const pwdRules: FormRules = {
  password: [
    { required: true, message: '请输入新密码', trigger: 'blur' },
    { min: 6, message: '密码至少 6 位', trigger: 'blur' },
  ],
  confirm: [
    { required: true, message: '请再次输入新密码', trigger: 'blur' },
    {
      validator: (_rule, value: string, callback) => {
        if (value !== pwdForm.password) callback(new Error('两次输入的密码不一致'))
        else callback()
      },
      trigger: 'blur',
    },
  ],
}

function openResetPassword(row: AdminUser) {
  pwdTarget.value = row
  pwdForm.password = ''
  pwdForm.confirm = ''
  pwdVisible.value = true
}

async function submitPassword() {
  if (!pwdTarget.value) return
  if (!(await validateForm(pwdFormRef.value))) return
  pwdSaving.value = true
  try {
    await usersApi.resetPassword(pwdTarget.value.id, pwdForm.password)
    pwdVisible.value = false
    ElMessage.success(`「${pwdTarget.value.username}」的密码已重置`)
  } finally {
    pwdSaving.value = false
  }
}
</script>

<template>
  <div class="page">
    <el-card shadow="never">
      <div class="toolbar">
        <div class="toolbar__title">
          后台用户
          <span class="toolbar__sub">共 {{ users.length }} 个账号</span>
        </div>
        <div class="toolbar__actions">
          <el-button :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
          <el-button type="primary" :icon="Plus" @click="openCreate">新建用户</el-button>
        </div>
      </div>

      <el-table v-loading="loading" :data="users" row-key="id">
        <el-table-column label="用户名" min-width="150">
          <template #default="{ row }">
            <span class="mono">{{ row.username }}</span>
            <el-tag v-if="isSelf(row as AdminUser)" size="small" type="success" class="self-tag">当前账号</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="显示名" prop="displayName" min-width="140" show-overflow-tooltip />
        <el-table-column label="角色" width="110">
          <template #default="{ row }">
            <EnumTag :options="ROLES" :value="row.role" />
          </template>
        </el-table-column>
        <el-table-column label="状态" width="90" align="center">
          <template #default="{ row }">
            <el-switch
              :model-value="row.status"
              :active-value="1"
              :inactive-value="0"
              :disabled="isSelf(row as AdminUser)"
              :loading="busyId === row.id"
              @change="(v: string | number | boolean) => changeStatus(row as AdminUser, v === 1 ? 1 : 0)"
            />
          </template>
        </el-table-column>
        <el-table-column label="最近登录" width="180">
          <template #default="{ row }">{{ formatTime(row.lastLoginAt, '从未登录') }}</template>
        </el-table-column>
        <el-table-column label="创建时间" width="180">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="200" fixed="right">
          <template #default="{ row }">
            <el-button link type="primary" @click="openEdit(row as AdminUser)">编辑</el-button>
            <el-button link type="primary" @click="openResetPassword(row as AdminUser)">重置密码</el-button>
            <el-button link type="danger" :disabled="isSelf(row as AdminUser)" @click="remove(row as AdminUser)">
              删除
            </el-button>
          </template>
        </el-table-column>
      </el-table>
    </el-card>

    <!-- 新建 / 编辑 -->
    <el-dialog
      v-model="dialogVisible"
      :title="editing ? '编辑用户' : '新建用户'"
      width="540px"
      :close-on-click-modal="false"
      destroy-on-close
    >
      <el-form ref="formRef" :model="form" :rules="rules" label-width="90px" @submit.prevent>
        <el-form-item label="用户名" prop="username">
          <el-input v-model="form.username" :disabled="!!editing" placeholder="登录用户名，创建后不可修改" maxlength="32" />
        </el-form-item>
        <el-form-item label="显示名" prop="displayName">
          <el-input v-model="form.displayName" placeholder="如 张三" maxlength="32" />
        </el-form-item>
        <el-form-item v-if="!editing" label="初始密码" prop="password">
          <el-input v-model="form.password" type="password" show-password autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="角色" prop="role">
          <el-radio-group v-model="form.role" :disabled="!!editing && isSelf(editing)">
            <el-radio v-for="r in ROLES" :key="r.value" :value="r.value">{{ r.label }}</el-radio>
          </el-radio-group>
          <div class="form-help">{{ roleHelp[form.role] }}</div>
          <div v-if="editing && isSelf(editing)" class="form-help">不能修改自己的角色与状态，避免把自己锁在外面。</div>
        </el-form-item>
        <el-form-item label="状态" prop="status">
          <el-radio-group v-model="form.status" :disabled="!!editing && isSelf(editing)">
            <el-radio v-for="s in ENABLE_STATUS" :key="s.value" :value="s.value">{{ s.label }}</el-radio>
          </el-radio-group>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogVisible = false">取消</el-button>
        <el-button type="primary" :loading="saving" @click="submit">保存</el-button>
      </template>
    </el-dialog>

    <!-- 重置密码 -->
    <el-dialog v-model="pwdVisible" title="重置密码" width="440px" :close-on-click-modal="false" destroy-on-close>
      <el-form ref="pwdFormRef" :model="pwdForm" :rules="pwdRules" label-width="90px" @submit.prevent>
        <el-form-item label="用户">
          <span class="mono">{{ pwdTarget?.username }}</span>
        </el-form-item>
        <el-form-item label="新密码" prop="password">
          <el-input v-model="pwdForm.password" type="password" show-password autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="确认密码" prop="confirm">
          <el-input
            v-model="pwdForm.confirm"
            type="password"
            show-password
            autocomplete="new-password"
            @keyup.enter="submitPassword"
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="pwdVisible = false">取消</el-button>
        <el-button type="primary" :loading="pwdSaving" @click="submitPassword">确定</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.self-tag {
  margin-left: 8px;
}
.form-help {
  width: 100%;
}
</style>
