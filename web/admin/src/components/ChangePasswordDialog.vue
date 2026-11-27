<script setup lang="ts">
import { reactive, ref } from 'vue'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { authApi } from '@/api/auth'
import { validateForm } from '@/utils/common'

defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  changed: []
}>()

const formRef = ref<FormInstance>()
const saving = ref(false)
const form = reactive({ oldPassword: '', newPassword: '', confirmPassword: '' })

const rules: FormRules = {
  oldPassword: [{ required: true, message: '请输入当前密码', trigger: 'blur' }],
  newPassword: [
    { required: true, message: '请输入新密码', trigger: 'blur' },
    { min: 6, message: '新密码至少 6 位', trigger: 'blur' },
  ],
  confirmPassword: [
    { required: true, message: '请再次输入新密码', trigger: 'blur' },
    {
      validator: (_rule, value: string, callback) => {
        if (value !== form.newPassword) callback(new Error('两次输入的新密码不一致'))
        else callback()
      },
      trigger: 'blur',
    },
  ],
}

function onOpen() {
  form.oldPassword = ''
  form.newPassword = ''
  form.confirmPassword = ''
  formRef.value?.clearValidate()
}

async function submit() {
  if (!(await validateForm(formRef.value))) return
  saving.value = true
  try {
    await authApi.changePassword({ oldPassword: form.oldPassword, newPassword: form.newPassword })
    ElMessage.success('密码已修改，请重新登录')
    emit('update:modelValue', false)
    emit('changed')
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    title="修改密码"
    width="440px"
    :close-on-click-modal="false"
    append-to-body
    @open="onOpen"
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  >
    <el-form ref="formRef" :model="form" :rules="rules" label-width="96px" @submit.prevent>
      <el-form-item label="当前密码" prop="oldPassword">
        <el-input v-model="form.oldPassword" type="password" show-password autocomplete="current-password" />
      </el-form-item>
      <el-form-item label="新密码" prop="newPassword">
        <el-input v-model="form.newPassword" type="password" show-password autocomplete="new-password" />
      </el-form-item>
      <el-form-item label="确认新密码" prop="confirmPassword">
        <el-input
          v-model="form.confirmPassword"
          type="password"
          show-password
          autocomplete="new-password"
          @keyup.enter="submit"
        />
      </el-form-item>
    </el-form>
    <template #footer>
      <el-button @click="emit('update:modelValue', false)">取消</el-button>
      <el-button type="primary" :loading="saving" @click="submit">确定</el-button>
    </template>
  </el-dialog>
</template>
