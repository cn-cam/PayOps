import { computed, ref } from 'vue'
import { defineStore } from 'pinia'
import { authApi } from '@/api/auth'
import type { LoginInput, Role, UserInfo } from '@/api/types'

const TOKEN_KEY = 'payops_admin_token'
const USER_KEY = 'payops_admin_user'
const EXPIRES_KEY = 'payops_admin_token_expires'

function readToken(): string {
  const token = localStorage.getItem(TOKEN_KEY) ?? ''
  const expiresAt = Number(localStorage.getItem(EXPIRES_KEY) ?? 0)
  if (token && expiresAt > 0 && Date.now() >= expiresAt) return ''
  return token
}

function readUser(): UserInfo | null {
  try {
    const raw = localStorage.getItem(USER_KEY)
    return raw ? (JSON.parse(raw) as UserInfo) : null
  } catch {
    return null
  }
}

export const useAuthStore = defineStore('auth', () => {
  const token = ref(readToken())
  const user = ref<UserInfo | null>(token.value ? readUser() : null)
  /** 本次页面生命周期内是否已向服务端确认过当前用户 */
  const verified = ref(false)

  const isLoggedIn = computed(() => !!token.value)
  const role = computed<Role>(() => user.value?.role ?? 'viewer')
  const isAdmin = computed(() => role.value === 'admin')
  /** admin / operator 可以执行写操作，viewer 只读 */
  const canWrite = computed(() => role.value === 'admin' || role.value === 'operator')
  /** 仅 admin 可查看 / 重置应用密钥 */
  const canViewSecret = computed(() => role.value === 'admin')

  function setUser(u: UserInfo | null) {
    user.value = u
    if (u) localStorage.setItem(USER_KEY, JSON.stringify(u))
    else localStorage.removeItem(USER_KEY)
  }

  async function login(input: LoginInput) {
    const res = await authApi.login(input)
    token.value = res.token
    localStorage.setItem(TOKEN_KEY, res.token)
    if (res.expiresIn > 0) {
      localStorage.setItem(EXPIRES_KEY, String(Date.now() + res.expiresIn * 1000))
    } else {
      localStorage.removeItem(EXPIRES_KEY)
    }
    setUser(res.user)
    verified.value = true
  }

  async function fetchMe() {
    const me = await authApi.me()
    setUser(me)
    verified.value = true
    return me
  }

  function clear() {
    token.value = ''
    verified.value = false
    setUser(null)
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(EXPIRES_KEY)
  }

  function hasRole(roles?: readonly Role[]): boolean {
    if (!roles || roles.length === 0) return true
    return roles.includes(role.value)
  }

  return {
    token,
    user,
    verified,
    isLoggedIn,
    role,
    isAdmin,
    canWrite,
    canViewSecret,
    login,
    fetchMe,
    clear,
    hasRole,
  }
})
