import axios, { type AxiosRequestConfig, type AxiosResponse } from 'axios'
import { ElMessage } from 'element-plus'
import type { ApiEnvelope } from './types'

/** 请求失败时抛出的错误；handled = true 表示已经向用户提示过，调用方无需再提示 */
export class HttpError extends Error {
  readonly handled = true
  constructor(
    message: string,
    public readonly code: number,
    public readonly httpStatus: number,
  ) {
    super(message)
    this.name = 'HttpError'
  }
}

interface HttpHooks {
  getToken: () => string
  onUnauthorized: () => void
}

let hooks: HttpHooks = {
  getToken: () => '',
  onUnauthorized: () => {},
}

/** 由 main.ts 注入 token 读取与 401 处理，避免 http 层与 store / router 循环依赖 */
export function setupHttp(h: HttpHooks) {
  hooks = h
}

function toastError(message: string) {
  ElMessage({ type: 'error', message, grouping: true })
}

const instance = axios.create({
  baseURL: '/api/admin',
  timeout: 30000,
})

instance.interceptors.request.use((config) => {
  const token = hooks.getToken()
  if (token) config.headers.set('Authorization', `Bearer ${token}`)
  return config
})

instance.interceptors.response.use(
  (res: AxiosResponse<ApiEnvelope>) => {
    const body = res.data
    if (!body || typeof body !== 'object' || typeof body.code !== 'number') {
      toastError('响应格式错误')
      return Promise.reject(new HttpError('响应格式错误', -1, res.status))
    }
    if (body.code !== 0) {
      const message = body.message || '操作失败'
      toastError(message)
      return Promise.reject(new HttpError(message, body.code, res.status))
    }
    return res
  },
  (error: unknown) => {
    if (axios.isCancel(error)) return Promise.reject(error)
    if (!axios.isAxiosError(error)) {
      toastError('请求失败')
      return Promise.reject(new HttpError('请求失败', -1, 0))
    }

    const status = error.response?.status ?? 0
    const body = error.response?.data as Partial<ApiEnvelope> | undefined
    const serverMessage = body && typeof body === 'object' && typeof body.message === 'string' ? body.message : ''
    let message: string

    if (status === 401) {
      const isLogin = (error.config?.url ?? '').includes('/auth/login')
      if (isLogin) {
        message = serverMessage || '用户名或密码错误'
      } else {
        message = '登录已失效，请重新登录'
        hooks.onUnauthorized()
      }
    } else if (status === 403) {
      message = '无权限'
    } else if (status === 0) {
      message = error.code === 'ECONNABORTED' ? '请求超时，请稍后重试' : '网络异常，无法连接服务器'
    } else if (status >= 500) {
      message = serverMessage || `服务器错误（${status}）`
    } else {
      message = serverMessage || `请求失败（${status}）`
    }

    toastError(message)
    return Promise.reject(new HttpError(message, body?.code ?? -1, status))
  },
)

function cleanParams(params?: object): Record<string, unknown> | undefined {
  if (!params) return undefined
  const out: Record<string, unknown> = {}
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null) continue
    if (typeof v === 'string' && v.trim() === '') continue
    out[k] = typeof v === 'string' ? v.trim() : v
  }
  return out
}

async function request<T>(config: AxiosRequestConfig): Promise<T> {
  const res = await instance.request<ApiEnvelope<T>>(config)
  return res.data.data
}

export const http = {
  get<T>(url: string, params?: object): Promise<T> {
    return request<T>({ method: 'GET', url, params: cleanParams(params) })
  },
  post<T>(url: string, data?: unknown): Promise<T> {
    return request<T>({ method: 'POST', url, data: data ?? {} })
  },
  put<T>(url: string, data?: unknown): Promise<T> {
    return request<T>({ method: 'PUT', url, data: data ?? {} })
  },
  delete<T>(url: string): Promise<T> {
    return request<T>({ method: 'DELETE', url })
  },
}

export default instance
