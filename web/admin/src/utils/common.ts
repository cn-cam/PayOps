import { ElMessage, ElMessageBox, type FormInstance } from 'element-plus'

/** 复制文本到剪贴板 */
export async function copyText(text: string, successMessage = '已复制到剪贴板'): Promise<boolean> {
  let ok = false
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      ok = true
    }
  } catch {
    ok = false
  }
  if (!ok) {
    const el = document.createElement('textarea')
    el.value = text
    el.setAttribute('readonly', '')
    el.style.position = 'fixed'
    el.style.top = '-1000px'
    el.style.opacity = '0'
    document.body.appendChild(el)
    el.select()
    try {
      ok = document.execCommand('copy')
    } catch {
      ok = false
    }
    document.body.removeChild(el)
  }
  if (ok) ElMessage.success(successMessage)
  else ElMessage.error('复制失败，请手动复制')
  return ok
}

/** 二次确认，取消时返回 false（不抛异常） */
export async function confirmAction(
  message: string,
  title = '请确认',
  type: 'warning' | 'info' | 'error' = 'warning',
): Promise<boolean> {
  try {
    await ElMessageBox.confirm(message, title, {
      type,
      confirmButtonText: '确定',
      cancelButtonText: '取消',
      closeOnClickModal: false,
    })
    return true
  } catch {
    return false
  }
}

/** 表单校验，未通过时返回 false（不抛异常） */
export async function validateForm(form: FormInstance | undefined | null): Promise<boolean> {
  if (!form) return false
  try {
    await form.validate()
    return true
  } catch {
    return false
  }
}

/** 错误是否已经由 http 层提示过 */
export function isHandledError(err: unknown): boolean {
  return typeof err === 'object' && err !== null && (err as { handled?: boolean }).handled === true
}

/** 尽量把内容格式化为缩进 JSON；不是 JSON 时原样返回 */
export function prettyJson(value: unknown): string {
  if (value === null || value === undefined || value === '') return ''
  if (typeof value === 'string') {
    const text = value.trim()
    if (!text) return ''
    if (/^[[{]/.test(text)) {
      try {
        return JSON.stringify(JSON.parse(text), null, 2)
      } catch {
        return value
      }
    }
    return value
  }
  try {
    return JSON.stringify(value, null, 2)
  } catch {
    return String(value)
  }
}

/** 百分比展示：0~1 → "12.3%" */
export function formatRate(rate: number | null | undefined, digits = 1): string {
  if (rate === null || rate === undefined || !Number.isFinite(rate)) return '-'
  return `${(rate * 100).toFixed(digits)}%`
}

export function formatCount(n: number | null | undefined): string {
  if (n === null || n === undefined || !Number.isFinite(n)) return '-'
  return String(Math.round(n)).replace(/\B(?=(\d{3})+(?!\d))/g, ',')
}
