<script setup lang="ts">
import { reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { Lock, User } from '@element-plus/icons-vue'
import type { FormInstance, FormRules } from 'element-plus'
import { useAuthStore } from '@/stores/auth'
import { validateForm } from '@/utils/common'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()

const formRef = ref<FormInstance>()
const loading = ref(false)
const form = reactive({ username: '', password: '' })

const rules: FormRules = {
  username: [{ required: true, message: '请输入用户名', trigger: 'blur' }],
  password: [{ required: true, message: '请输入密码', trigger: 'blur' }],
}

async function submit() {
  if (loading.value) return
  if (!(await validateForm(formRef.value))) return
  loading.value = true
  try {
    await auth.login({ username: form.username.trim(), password: form.password })
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : ''
    // 只接受站内地址，避免开放重定向
    const target = redirect.startsWith('/') && !redirect.startsWith('//') ? redirect : '/'
    await router.replace(target)
  } finally {
    loading.value = false
  }
}
</script>

<template>
  <div class="login">
    <div class="login__panel">
      <div class="login__brand">
        <div class="login__mark">P</div>
        <div>
          <div class="login__name">PayOps</div>
          <div class="login__sub">多渠道 · 多通道 支付管理后台</div>
        </div>
      </div>

      <el-form ref="formRef" :model="form" :rules="rules" size="large" @submit.prevent="submit">
        <el-form-item prop="username">
          <el-input
            v-model="form.username"
            placeholder="用户名"
            :prefix-icon="User"
            autocomplete="username"
            autofocus
          />
        </el-form-item>
        <el-form-item prop="password">
          <el-input
            v-model="form.password"
            type="password"
            placeholder="密码"
            :prefix-icon="Lock"
            show-password
            autocomplete="current-password"
            @keyup.enter="submit"
          />
        </el-form-item>
        <el-button type="primary" class="login__submit" :loading="loading" @click="submit">登 录</el-button>
      </el-form>

      <div class="login__footer">自用支付系统 · 仅限内部人员使用</div>
    </div>
  </div>
</template>

<style scoped>
.login {
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100%;
  padding: 24px;
  background:
    radial-gradient(1200px 600px at 10% -10%, rgba(37, 99, 235, 0.35), transparent 60%),
    radial-gradient(900px 500px at 110% 110%, rgba(14, 165, 233, 0.25), transparent 60%), #0f172a;
}
.login__panel {
  width: 400px;
  max-width: 100%;
  padding: 36px 36px 24px;
  border-radius: 14px;
  background: #fff;
  box-shadow: 0 24px 60px rgba(2, 6, 23, 0.45);
}
.login__brand {
  display: flex;
  align-items: center;
  gap: 14px;
  margin-bottom: 30px;
}
.login__mark {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 46px;
  height: 46px;
  border-radius: 12px;
  background: linear-gradient(135deg, #3b82f6, #2563eb);
  color: #fff;
  font-size: 26px;
  font-weight: 700;
}
.login__name {
  font-size: 22px;
  font-weight: 700;
  line-height: 1.2;
}
.login__sub {
  margin-top: 2px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.login__submit {
  width: 100%;
  margin-top: 4px;
}
.login__footer {
  margin-top: 22px;
  text-align: center;
  color: var(--el-text-color-placeholder);
  font-size: 12px;
}
</style>
