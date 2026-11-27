import { http } from './http'
import type { ChangePasswordInput, LoginInput, LoginResult, UserInfo } from './types'

export const authApi = {
  login: (data: LoginInput) => http.post<LoginResult>('/auth/login', data),
  me: () => http.get<UserInfo>('/auth/me'),
  changePassword: (data: ChangePasswordInput) => http.post<null>('/auth/password', data),
}
