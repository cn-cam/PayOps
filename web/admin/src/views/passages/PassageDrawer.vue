<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { CopyDocument } from '@element-plus/icons-vue'
import { ElMessage, type FormInstance, type FormItemRule, type FormRules } from 'element-plus'
import { passagesApi } from '@/api/passages'
import type { ConfigField, EnableStatus, PassageDetail } from '@/api/types'
import { useAuthStore } from '@/stores/auth'
import { useMetaStore } from '@/stores/meta'
import EnumTag from '@/components/EnumTag.vue'
import { copyText, validateForm } from '@/utils/common'
import { ENABLE_STATUS, PROVIDER_CATEGORY } from '@/utils/enums'

const SECRET_MASK = '******'

const props = defineProps<{
  modelValue: boolean
  /** 编辑的通道 ID；null 表示新建 */
  passageId: number | null
  /** 新建时预选的支付类型 */
  defaultProvider?: string
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  saved: [payload: { id: number; created: boolean; test: boolean }]
}>()

const auth = useAuthStore()
const meta = useMetaStore()

const isEdit = computed(() => props.passageId !== null)
const readonly = computed(() => !auth.canWrite)
const formRef = ref<FormInstance>()
const loading = ref(false)
const saving = ref(false)
const detail = ref<PassageDetail | null>(null)
/** 编辑时服务端返回为掩码的密钥字段：留空或保持掩码都表示不修改 */
const maskedKeys = ref(new Set<string>())

const form = reactive({
  name: '',
  provider: '',
  status: 1 as EnableStatus,
  feeRateBp: 0,
  remark: '',
  config: {} as Record<string, string>,
})

const providerMeta = computed(() => meta.getProvider(form.provider))
const fields = computed<ConfigField[]>(() => providerMeta.value?.configFields ?? [])
const feeRatePercent = computed(() => (form.feeRateBp > 0 ? `${(form.feeRateBp / 100).toFixed(2)}%` : '不核对'))

const capabilityTags = computed(() => {
  const c = providerMeta.value?.capabilities
  if (!c) return []
  return [
    { label: '订单查询', on: c.query },
    { label: '关单', on: c.close },
    { label: '退款', on: c.refund },
    { label: '票据校验', on: c.receiptVerify },
    { label: '服务端回调', on: c.serverNotify },
    { label: '自动续费订阅', on: c.subscription },
    // 不支持订阅的类型谈不上退订 / 暂停 / 升降级，不展示这几项
    ...(c.subscription
      ? [
          { label: '服务端退订', on: c.subscriptionCancel },
          { label: '暂停 / 恢复', on: c.subscriptionPause },
          { label: '升级 / 降级', on: c.subscriptionChange },
        ]
      : []),
    { label: '签约代扣', on: c.deduct },
    { label: '交易账单', on: c.bill },
    { label: '资金账单', on: c.fundBill },
  ]
})

const rules: FormRules = {
  name: [{ required: true, message: '请输入通道名称', trigger: 'blur' }],
  provider: [{ required: true, message: '请选择支付类型', trigger: 'change' }],
}

function defaultValueOf(field: ConfigField): string {
  if (field.defaultValue !== null && field.defaultValue !== undefined && field.defaultValue !== '') {
    return String(field.defaultValue)
  }
  return field.type === 'switch' ? 'false' : ''
}

function isKeepable(field: ConfigField): boolean {
  return isEdit.value && field.secret && maskedKeys.value.has(field.key)
}

function fieldRules(field: ConfigField): FormItemRule[] {
  const list: FormItemRule[] = []
  if (field.required && field.type !== 'switch' && !isKeepable(field)) {
    list.push({
      required: true,
      message: `${field.type === 'select' ? '请选择' : '请输入'}${field.label}`,
      trigger: field.type === 'select' ? 'change' : 'blur',
    })
  }
  if (field.type === 'number') {
    list.push({
      validator: (_rule, value, callback) => {
        const text = value === null || value === undefined ? '' : String(value).trim()
        if (text === '' || text === SECRET_MASK || /^-?\d+(\.\d+)?$/.test(text)) callback()
        else callback(new Error(`${field.label}必须是数字`))
      },
      trigger: 'blur',
    })
  }
  return list
}

function fieldPlaceholder(field: ConfigField): string {
  if (isKeepable(field)) return '已设置，留空表示不修改'
  return field.placeholder || ''
}

/** 密钥字段聚焦时清掉掩码，方便直接输入新值；未输入则恢复掩码（= 不修改） */
function onSecretFocus(field: ConfigField) {
  if (isKeepable(field) && form.config[field.key] === SECRET_MASK) form.config[field.key] = ''
}

function onSecretBlur(field: ConfigField) {
  if (isKeepable(field) && !form.config[field.key]) form.config[field.key] = SECRET_MASK
}

function resetConfig() {
  const config: Record<string, string> = {}
  for (const f of fields.value) config[f.key] = defaultValueOf(f)
  form.config = config
  maskedKeys.value = new Set()
}

function onProviderChange() {
  resetConfig()
  formRef.value?.clearValidate()
}

function resetForm() {
  detail.value = null
  maskedKeys.value = new Set()
  Object.assign(form, { name: '', provider: '', status: 1, feeRateBp: 0, remark: '', config: {} })
}

/** 关闭后立即清空，避免下次打开时先按上一次的支付类型渲染一遍动态表单 */
function onClosed() {
  resetForm()
}

async function onOpen() {
  resetForm()
  formRef.value?.clearValidate()
  loading.value = true
  try {
    await meta.loadProviders()
    if (props.passageId !== null) {
      const d = await passagesApi.get(props.passageId)
      detail.value = d
      form.name = d.name
      form.provider = d.provider
      form.status = d.status
      form.feeRateBp = d.feeRateBp ?? 0
      form.remark = d.remark ?? ''
      const source = d.config ?? {}
      const config: Record<string, string> = { ...source }
      const masked = new Set<string>()
      for (const f of fields.value) {
        const value = source[f.key]
        config[f.key] = value === undefined || value === null ? defaultValueOf(f) : String(value)
        if (f.secret && config[f.key] === SECRET_MASK) masked.add(f.key)
      }
      form.config = config
      maskedKeys.value = masked
    } else if (props.defaultProvider && meta.getProvider(props.defaultProvider)) {
      form.provider = props.defaultProvider
      resetConfig()
    }
  } catch (err) {
    emit('update:modelValue', false)
    throw err
  } finally {
    loading.value = false
  }
}

function buildConfig(): Record<string, string> {
  const config: Record<string, string> = { ...form.config }
  for (const f of fields.value) {
    const raw = config[f.key]
    config[f.key] = raw === undefined || raw === null ? '' : String(raw).trim()
  }
  return config
}

async function submit(test: boolean) {
  if (!(await validateForm(formRef.value))) {
    ElMessage.warning('请先完善表单中标红的内容')
    return
  }
  saving.value = true
  try {
    const base = {
      name: form.name.trim(),
      status: form.status,
      feeRateBp: form.feeRateBp ?? 0,
      remark: form.remark.trim(),
      config: buildConfig(),
    }
    let id: number
    if (props.passageId !== null) {
      id = props.passageId
      await passagesApi.update(id, base)
    } else {
      const res = await passagesApi.create({ ...base, provider: form.provider })
      id = res.id
    }
    ElMessage.success('通道已保存')
    emit('update:modelValue', false)
    emit('saved', { id, created: props.passageId === null, test })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <el-drawer
    :model-value="modelValue"
    :title="readonly ? '查看通道' : isEdit ? '编辑通道' : '新建通道'"
    size="680px"
    :close-on-click-modal="false"
    destroy-on-close
    @open="onOpen"
    @closed="onClosed"
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  >
    <el-form
      ref="formRef"
      v-loading="loading"
      :model="form"
      :rules="rules"
      :disabled="readonly"
      label-width="130px"
      class="passage-form"
      @submit.prevent
    >
      <div class="section-title">基本信息</div>
      <el-form-item label="通道名称" prop="name">
        <el-input v-model="form.name" placeholder="如 微信支付-商户号A" maxlength="64" />
        <div class="form-help">建议用「支付类型-主体 / 商户号」命名，便于在渠道通道配置中区分。</div>
      </el-form-item>
      <el-form-item label="支付类型" prop="provider">
        <el-select
          v-model="form.provider"
          placeholder="请选择支付类型"
          filterable
          :disabled="isEdit"
          style="width: 100%"
          @change="onProviderChange"
        >
          <el-option v-for="p in meta.providers" :key="p.code" :value="p.code" :label="p.name">
            <span>{{ p.name }}</span>
            <span class="option-sub">{{ p.code }}</span>
          </el-option>
        </el-select>
        <div v-if="isEdit" class="form-help">支付类型创建后不可修改。</div>
      </el-form-item>

      <div v-if="providerMeta" class="provider-info">
        <div class="provider-info__row">
          <span class="provider-info__label">类别</span>
          <EnumTag :options="PROVIDER_CATEGORY" :value="providerMeta.category" size="small" />
        </div>
        <div class="provider-info__row">
          <span class="provider-info__label">能力</span>
          <div class="tag-list">
            <el-tag
              v-for="c in capabilityTags"
              :key="c.label"
              :type="c.on ? 'success' : 'info'"
              :effect="c.on ? 'light' : 'plain'"
              size="small"
            >
              {{ c.label }}{{ c.on ? '' : '（不支持）' }}
            </el-tag>
          </div>
        </div>
        <div class="provider-info__row">
          <span class="provider-info__label">支付方式</span>
          <div class="tag-list">
            <el-tag v-for="m in providerMeta.methods" :key="m.code" effect="plain" size="small">
              {{ m.name }}
            </el-tag>
          </div>
        </div>
      </div>

      <el-form-item label="状态" prop="status">
        <el-radio-group v-model="form.status">
          <el-radio v-for="s in ENABLE_STATUS" :key="s.value" :value="s.value">{{ s.label }}</el-radio>
        </el-radio-group>
        <div class="form-help">停用后，所有绑定了该通道的渠道都不会再路由到它。</div>
      </el-form-item>
      <el-form-item label="手续费率" prop="feeRateBp">
        <el-input-number
          v-model="form.feeRateBp"
          :min="0"
          :max="10000"
          :step="1"
          :precision="0"
          controls-position="right"
          style="width: 160px"
        />
        <span class="fee-rate-value">= {{ feeRatePercent }}</span>
        <div class="form-help">用于手续费核对；0 表示不核对。按万分比填写，60 表示 0.60%。</div>
      </el-form-item>
      <el-form-item label="备注" prop="remark">
        <el-input v-model="form.remark" type="textarea" :rows="2" maxlength="255" show-word-limit />
      </el-form-item>

      <template v-if="providerMeta">
        <div class="section-title">通道参数</div>
        <el-empty v-if="fields.length === 0" description="该支付类型无需配置参数" :image-size="60" />

        <el-form-item
          v-for="field in fields"
          :key="`${form.provider}:${field.key}`"
          :label="field.label"
          :prop="`config.${field.key}`"
          :rules="fieldRules(field)"
          :required="field.required && field.type !== 'switch'"
        >
          <el-switch
            v-if="field.type === 'switch'"
            v-model="form.config[field.key]"
            active-value="true"
            inactive-value="false"
          />
          <el-select
            v-else-if="field.type === 'select'"
            v-model="form.config[field.key]"
            :placeholder="field.placeholder || '请选择'"
            :clearable="!field.required"
            style="width: 100%"
          >
            <el-option v-for="o in field.options ?? []" :key="o.value" :value="o.value" :label="o.label" />
          </el-select>
          <el-input
            v-else-if="field.type === 'textarea'"
            v-model="form.config[field.key]"
            type="textarea"
            :autosize="{ minRows: 3, maxRows: 10 }"
            :placeholder="fieldPlaceholder(field)"
            class="mono-input"
            spellcheck="false"
            @focus="onSecretFocus(field)"
            @blur="onSecretBlur(field)"
          />
          <el-input
            v-else-if="field.type === 'password'"
            v-model="form.config[field.key]"
            type="password"
            show-password
            autocomplete="new-password"
            :placeholder="fieldPlaceholder(field)"
            @focus="onSecretFocus(field)"
            @blur="onSecretBlur(field)"
          />
          <el-input
            v-else
            v-model="form.config[field.key]"
            :inputmode="field.type === 'number' ? 'decimal' : 'text'"
            :placeholder="fieldPlaceholder(field)"
            autocomplete="off"
            @focus="onSecretFocus(field)"
            @blur="onSecretBlur(field)"
          />
          <div v-if="field.help || isKeepable(field)" class="form-help">
            <el-tag v-if="isKeepable(field)" type="success" size="small" effect="plain" class="keep-tag">
              已设置
            </el-tag>
            <span v-if="isKeepable(field)">密钥不回显；不修改请保持原样或留空。</span>
            <span v-if="field.help">{{ field.help }}</span>
          </div>
        </el-form-item>

        <div class="section-title">上游回调地址</div>
        <div class="notify-box">
          <template v-if="detail?.notifyUrl">
            <div class="notify-box__url">
              <span class="mono">{{ detail.notifyUrl }}</span>
              <el-button
                type="primary"
                size="small"
                :icon="CopyDocument"
                :disabled="false"
                @click="copyText(detail.notifyUrl)"
              >
                复制
              </el-button>
            </div>
          </template>
          <div v-else class="text-secondary">保存后自动生成该通道专属的回调地址。</div>
          <div v-if="providerMeta.notifyNote" class="notify-box__note">{{ providerMeta.notifyNote }}</div>
          <div v-if="!providerMeta.capabilities?.serverNotify" class="notify-box__note">
            该支付类型没有服务端回调，支付结果通过票据校验 / 主动查询确认。
          </div>
        </div>
      </template>
    </el-form>

    <template #footer>
      <template v-if="readonly">
        <el-button @click="emit('update:modelValue', false)">关闭</el-button>
      </template>
      <template v-else>
        <span class="footer-hint">「保存并自检」会在保存后立即检查必填项、密钥格式等</span>
        <el-button @click="emit('update:modelValue', false)">取消</el-button>
        <el-button :loading="saving" :disabled="loading" @click="submit(true)">保存并自检</el-button>
        <el-button type="primary" :loading="saving" :disabled="loading" @click="submit(false)">保存</el-button>
      </template>
    </template>
  </el-drawer>
</template>

<style scoped>
.passage-form {
  padding-right: 6px;
}
.section-title {
  margin: 6px 0 16px;
  padding-left: 10px;
  border-left: 3px solid var(--el-color-primary);
  font-weight: 600;
  line-height: 1.2;
}
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.provider-info {
  margin: -4px 0 18px 130px;
  padding: 10px 14px;
  border-radius: 6px;
  background: #f8fafc;
}
.provider-info__row {
  display: flex;
  align-items: flex-start;
  gap: 12px;
  padding: 4px 0;
}
.provider-info__label {
  width: 56px;
  flex-shrink: 0;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 24px;
}
.tag-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.fee-rate-value {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12.5px;
  font-variant-numeric: tabular-nums;
}
.mono-input :deep(.el-textarea__inner) {
  font-family: var(--mono-font);
  font-size: 12.5px;
}
.form-help {
  width: 100%;
}
.keep-tag {
  margin-right: 6px;
}
.notify-box {
  margin-bottom: 12px;
  padding: 12px 14px;
  border: 1px dashed var(--el-border-color);
  border-radius: 6px;
  background: #fbfdff;
  font-size: 13px;
}
.notify-box__url {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  word-break: break-all;
}
.notify-box__note {
  margin-top: 8px;
  color: var(--el-text-color-secondary);
  line-height: 1.6;
  white-space: pre-wrap;
}
.footer-hint {
  margin-right: 12px;
  color: var(--el-text-color-placeholder);
  font-size: 12px;
}
</style>
