<script setup lang="ts">
import { computed, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, type FormInstance, type FormRules } from 'element-plus'
import { bindingsApi } from '@/api/bindings'
import { passagesApi } from '@/api/passages'
import type { BindingInput, BindingItem, EnableStatus, PassageBrief } from '@/api/types'
import { useMetaStore } from '@/stores/meta'
import MoneyInput from '@/components/MoneyInput.vue'
import { formatRate, validateForm } from '@/utils/common'
import { ENABLE_STATUS } from '@/utils/enums'

const props = defineProps<{
  modelValue: boolean
  channelId: number
  /** 编辑的绑定；null 表示新增 */
  binding: BindingItem | null
  /** 当前渠道已有的全部绑定，用于提示与占比预估 */
  bindings: BindingItem[]
}>()

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  saved: []
}>()

const router = useRouter()
const meta = useMetaStore()

const isEdit = computed(() => props.binding !== null)
const formRef = ref<FormInstance>()
const saving = ref(false)

interface FormState extends BindingInput {
  provider: string
  passageId: number | undefined
}

function emptyForm(): FormState {
  return {
    provider: '',
    passageId: undefined,
    methods: [],
    weight: 100,
    priority: 1,
    minAmount: 0,
    maxAmount: 0,
    dailyLimitAmount: 0,
    dailyLimitCount: 0,
    startTime: '',
    endTime: '',
    status: 1,
    remark: '',
  }
}

const form = reactive<FormState>(emptyForm())

// ---------- 通道下拉 ----------
const passages = ref<PassageBrief[]>([])
const passagesLoading = ref(false)

async function loadPassages(provider: string) {
  passages.value = []
  if (!provider) return
  passagesLoading.value = true
  try {
    const list = await passagesApi.all(provider)
    if (form.provider === provider) passages.value = list ?? []
  } finally {
    passagesLoading.value = false
  }
}

function onProviderChange(provider: string) {
  form.passageId = undefined
  form.methods = []
  loadPassages(provider)
}

const boundPassageIds = computed(() => new Set(props.bindings.map((b) => b.passageId)))

const providerMeta = computed(() => meta.getProvider(form.provider))

/** 支付方式选项：来自 provider 元数据；编辑时保留元数据中已不存在的历史值 */
const methodOptions = computed(() => {
  const options = (providerMeta.value?.methods ?? []).map((m) => ({ code: m.code, name: m.name }))
  for (const code of form.methods) {
    if (!options.some((o) => o.code === code)) options.push({ code, name: code })
  }
  return options
})

// ---------- 占比预估 ----------
const sharePreview = computed(() => {
  if (!form.provider) return ''
  if (form.status !== 1) return '当前为停用状态，不参与路由。'
  const peers = props.bindings.filter(
    (b) =>
      b.id !== props.binding?.id &&
      b.provider === form.provider &&
      b.priority === form.priority &&
      b.status === 1 &&
      b.passageStatus === 1,
  )
  const peerWeight = peers.reduce((sum, b) => sum + b.weight, 0)
  const scope = `「${meta.providerName(form.provider)} · 优先级 ${form.priority}」组`
  if (form.weight === 0) {
    return peerWeight > 0
      ? `权重为 0：作为${scope}的备用通道，仅当组内其它通道都不可用时才会被使用。`
      : `权重为 0，且${scope}内没有其它正权重通道：该通道会作为备用直接承接流量。`
  }
  if (peers.length === 0) return `${scope}内目前只有这一条通道，将承接该组 100% 的流量。`
  const share = form.weight / (peerWeight + form.weight)
  return `${scope}内还有 ${peers.length} 条启用通道（权重合计 ${peerWeight}），本通道预计占比约 ${formatRate(share)}。`
})

// ---------- 校验 ----------
const rules: FormRules = {
  provider: [{ required: true, message: '请选择支付类型', trigger: 'change' }],
  passageId: [{ required: true, message: '请选择通道', trigger: 'change' }],
  weight: [{ required: true, type: 'number', message: '请输入权重', trigger: 'blur' }],
  priority: [{ required: true, type: 'number', message: '请输入优先级', trigger: 'blur' }],
  maxAmount: [
    {
      validator: (_rule, _value, callback) => {
        if (form.maxAmount > 0 && form.maxAmount < form.minAmount) {
          callback(new Error('单笔上限不能小于起付金额'))
        } else {
          callback()
        }
      },
      trigger: 'change',
    },
  ],
  endTime: [
    {
      validator: (_rule, _value, callback) => {
        if (!!form.startTime !== !!form.endTime) callback(new Error('起止时间需同时填写，或同时留空表示全天'))
        else if (form.startTime && form.startTime === form.endTime) callback(new Error('起止时间不能相同'))
        else callback()
      },
      trigger: 'change',
    },
  ],
}

function onTimeChange(field: 'startTime' | 'endTime', value: unknown) {
  form[field] = typeof value === 'string' ? value : ''
  formRef.value?.validateField('endTime').catch(() => {})
}

const crossDay = computed(() => !!form.startTime && !!form.endTime && form.startTime > form.endTime)

// ---------- 打开 / 提交 ----------
function onOpen() {
  const b = props.binding
  if (b) {
    Object.assign(form, {
      provider: b.provider,
      passageId: b.passageId,
      methods: [...(b.methods ?? [])],
      weight: b.weight,
      priority: b.priority,
      minAmount: b.minAmount,
      maxAmount: b.maxAmount,
      dailyLimitAmount: b.dailyLimitAmount,
      dailyLimitCount: b.dailyLimitCount,
      startTime: b.startTime ?? '',
      endTime: b.endTime ?? '',
      status: b.status,
      remark: b.remark ?? '',
    } satisfies FormState)
    passages.value = []
  } else {
    const init = emptyForm()
    // 新增时默认加入当前最优先的一组，方便直接按权重分流
    if (props.bindings.length > 0) init.priority = Math.min(...props.bindings.map((x) => x.priority))
    Object.assign(form, init)
    passages.value = []
  }
  formRef.value?.clearValidate()
}

async function submit() {
  if (!(await validateForm(formRef.value))) return
  const input: BindingInput = {
    methods: [...form.methods],
    weight: form.weight,
    priority: form.priority,
    minAmount: form.minAmount,
    maxAmount: form.maxAmount,
    dailyLimitAmount: form.dailyLimitAmount,
    dailyLimitCount: form.dailyLimitCount,
    startTime: form.startTime,
    endTime: form.endTime,
    status: form.status,
    remark: form.remark.trim(),
  }
  saving.value = true
  try {
    if (props.binding) {
      await bindingsApi.update(props.binding.id, input)
      ElMessage.success('路由规则已保存')
    } else {
      await bindingsApi.create(props.channelId, { ...input, passageId: form.passageId as number })
      ElMessage.success('通道已添加')
    }
    emit('update:modelValue', false)
    emit('saved')
  } finally {
    saving.value = false
  }
}

function goPassages() {
  emit('update:modelValue', false)
  router.push({ path: '/passages', query: form.provider ? { provider: form.provider } : {} })
}

function setStatus(value: string | number | boolean | undefined) {
  form.status = (value === 1 ? 1 : 0) as EnableStatus
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="isEdit ? '编辑路由规则' : '添加通道'"
    width="760px"
    top="6vh"
    :close-on-click-modal="false"
    @open="onOpen"
    @update:model-value="(v: boolean) => emit('update:modelValue', v)"
  >
    <el-form ref="formRef" :model="form" :rules="rules" label-width="104px" class="binding-form" @submit.prevent>
      <div class="section-title">选择通道</div>

      <template v-if="isEdit && binding">
        <el-form-item label="通道">
          <div class="readonly-passage">
            <el-tag effect="dark">{{ binding.providerName || meta.providerName(binding.provider) }}</el-tag>
            <span class="readonly-passage__name">{{ binding.passageName }}</span>
            <el-tag v-if="binding.passageStatus !== 1" type="danger" size="small">通道已停用</el-tag>
          </div>
          <div class="form-help">绑定的通道不可更换；如需换通道，请删除后重新添加。</div>
        </el-form-item>
      </template>

      <template v-else>
        <el-form-item label="支付类型" prop="provider">
          <el-select
            v-model="form.provider"
            placeholder="请选择支付类型"
            filterable
            style="width: 100%"
            @change="onProviderChange"
          >
            <el-option v-for="p in meta.providers" :key="p.code" :value="p.code" :label="p.name">
              <span>{{ p.name }}</span>
              <span class="option-sub">{{ p.code }}</span>
            </el-option>
          </el-select>
        </el-form-item>
        <el-form-item label="通道" prop="passageId">
          <el-select
            v-model="form.passageId"
            :placeholder="form.provider ? '请选择通道（商户号 / 应用凭证）' : '请先选择支付类型'"
            :disabled="!form.provider"
            :loading="passagesLoading"
            filterable
            style="width: 100%"
          >
            <el-option v-for="p in passages" :key="p.id" :value="p.id" :label="p.name">
              <span>{{ p.name }}</span>
              <el-tag v-if="p.status !== 1" type="info" size="small" class="option-tag">已停用</el-tag>
              <el-tag v-if="boundPassageIds.has(p.id)" type="warning" size="small" class="option-tag">
                本渠道已绑定
              </el-tag>
            </el-option>
            <template #empty>
              <div class="passage-empty">
                {{ passagesLoading ? '加载中…' : '该支付类型下还没有通道' }}
                <el-button v-if="!passagesLoading" link type="primary" @click="goPassages">去通道管理创建</el-button>
              </div>
            </template>
          </el-select>
          <div class="form-help">
            同一渠道可以绑定同一支付类型下的多条通道（如 微信支付-商户号A、微信支付-商户号B），再用权重 / 优先级 /
            起付金额分流。
          </div>
        </el-form-item>
      </template>

      <el-form-item label="支付方式" prop="methods">
        <el-select
          v-model="form.methods"
          multiple
          clearable
          :disabled="!form.provider"
          placeholder="不选 = 该支付类型支持的全部支付方式"
          style="width: 100%"
        >
          <el-option v-for="m in methodOptions" :key="m.code" :value="m.code" :label="m.name">
            <span>{{ m.name }}</span>
            <span class="option-sub">{{ m.code }}</span>
          </el-option>
        </el-select>
        <div class="form-help">只有订单的支付方式在所选范围内，该通道才参与路由；留空表示不限制。</div>
      </el-form-item>

      <div class="section-title">分流规则</div>
      <el-row :gutter="16">
        <el-col :span="12">
          <el-form-item label="优先级" prop="priority">
            <el-input-number
              v-model="form.priority"
              :min="0"
              :max="9999"
              :step="1"
              step-strictly
              controls-position="right"
              style="width: 180px"
            />
            <div class="form-help">数字越小越优先。高优先级一组的通道全部不可用时，才会落到下一组。</div>
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="权重" prop="weight">
            <el-input-number
              v-model="form.weight"
              :min="0"
              :max="10000"
              :step="10"
              :precision="0"
              controls-position="right"
              style="width: 180px"
            />
            <div class="form-help">0~10000。同优先级内按权重比例随机分流（如 70 : 30）；0 = 备用。</div>
          </el-form-item>
        </el-col>
      </el-row>
      <el-alert v-if="sharePreview" type="info" :closable="false" show-icon class="share-preview" :title="sharePreview" />

      <div class="section-title">金额条件</div>
      <el-row :gutter="16">
        <el-col :span="12">
          <el-form-item label="起付金额" prop="minAmount">
            <MoneyInput v-model="form.minAmount" @change="formRef?.validateField('maxAmount').catch(() => {})" />
            <div class="form-help">订单金额 ≥ 该值才使用此通道；0 = 不限。例如设为 100 元，则小于 100 元的订单不会走它。</div>
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="单笔上限" prop="maxAmount">
            <MoneyInput v-model="form.maxAmount" />
            <div class="form-help">订单金额 ≤ 该值才使用此通道；0 = 不限。</div>
          </el-form-item>
        </el-col>
      </el-row>

      <div class="section-title">限额与时段</div>
      <el-row :gutter="16">
        <el-col :span="12">
          <el-form-item label="日限额(金额)" prop="dailyLimitAmount">
            <MoneyInput v-model="form.dailyLimitAmount" />
            <div class="form-help">当日成功金额累计达到该值后，当天不再路由到此通道；0 = 不限。</div>
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="日限额(笔数)" prop="dailyLimitCount">
            <el-input-number
              v-model="form.dailyLimitCount"
              :min="0"
              :max="100000000"
              :precision="0"
              :controls="false"
              style="width: 180px"
            />
            <span class="unit">笔</span>
            <div class="form-help">当日成功笔数达到该值后，当天不再路由到此通道；0 = 不限。</div>
          </el-form-item>
        </el-col>
      </el-row>
      <el-form-item label="可用时段" prop="endTime">
        <div class="time-range">
          <el-time-picker
            :model-value="form.startTime || undefined"
            format="HH:mm"
            value-format="HH:mm"
            placeholder="开始"
            style="width: 140px"
            @update:model-value="(v: unknown) => onTimeChange('startTime', v)"
          />
          <span class="text-secondary">至</span>
          <el-time-picker
            :model-value="form.endTime || undefined"
            format="HH:mm"
            value-format="HH:mm"
            placeholder="结束"
            style="width: 140px"
            @update:model-value="(v: unknown) => onTimeChange('endTime', v)"
          />
          <el-tag v-if="crossDay" type="warning" size="small">跨零点</el-tag>
          <el-tag v-else-if="!form.startTime && !form.endTime" type="info" size="small">全天可用</el-tag>
        </div>
        <div class="form-help">留空 = 全天可用。结束时间早于开始时间表示跨零点，如 22:00 至 06:00。</div>
      </el-form-item>

      <div class="section-title">其它</div>
      <el-form-item label="状态" prop="status">
        <el-radio-group :model-value="form.status" @update:model-value="setStatus">
          <el-radio v-for="s in ENABLE_STATUS" :key="s.value" :value="s.value">{{ s.label }}</el-radio>
        </el-radio-group>
        <div class="form-help">停用后该绑定不参与路由，但保留配置。</div>
      </el-form-item>
      <el-form-item label="备注" prop="remark">
        <el-input v-model="form.remark" type="textarea" :rows="2" maxlength="255" show-word-limit />
      </el-form-item>
    </el-form>

    <template #footer>
      <el-button @click="emit('update:modelValue', false)">取消</el-button>
      <el-button type="primary" :loading="saving" @click="submit">保存</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.binding-form {
  max-height: 68vh;
  padding-right: 8px;
  overflow-y: auto;
  overflow-x: hidden;
}
.section-title {
  margin: 4px 0 14px;
  padding-left: 10px;
  border-left: 3px solid var(--el-color-primary);
  font-weight: 600;
  line-height: 1.2;
}
.readonly-passage {
  display: flex;
  align-items: center;
  gap: 10px;
}
.readonly-passage__name {
  font-weight: 600;
}
.option-sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.option-tag {
  margin-left: 8px;
}
.passage-empty {
  padding: 14px;
  text-align: center;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.share-preview {
  margin: -4px 0 18px;
}
.time-range {
  display: flex;
  align-items: center;
  gap: 10px;
}
.unit {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 13px;
}
.form-help {
  width: 100%;
}
</style>
