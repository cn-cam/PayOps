import dayjs from 'dayjs'
import type { SubscriptionItem } from '@/api/types'

/** 距到期不足该天数时高亮提醒 */
export const EXPIRING_SOON_DAYS = 3

export interface ExpiryInfo {
  /** normal 正常 / soon 即将到期 / expired 已过到期时间但仍标记为生效（等待同步或续费入账）/ ended 订阅已结束 */
  level: 'normal' | 'soon' | 'expired' | 'ended'
  text: string
}

function humanize(ms: number): string {
  const minutes = Math.max(1, Math.floor(ms / 60000))
  if (minutes < 60) return `${minutes} 分钟`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} 小时`
  return `${Math.floor(hours / 24)} 天`
}

/** 当前周期的到期提示 */
export function expiryInfo(
  sub: Pick<SubscriptionItem, 'status' | 'autoRenew' | 'expiresAt'>,
  now: number = Date.now(),
): ExpiryInfo {
  if (sub.status !== 1) return { level: 'ended', text: '' }
  if (!sub.expiresAt) return { level: 'normal', text: '' }
  const expires = dayjs(sub.expiresAt)
  if (!expires.isValid()) return { level: 'normal', text: '' }
  const diff = expires.valueOf() - now
  if (diff <= 0) {
    return { level: 'expired', text: `已超过到期时间 ${humanize(-diff)}，待续费入账或同步` }
  }
  const tail = sub.autoRenew ? '将自动续费' : '到期后失效'
  if (diff <= EXPIRING_SOON_DAYS * 86400000) {
    return { level: 'soon', text: `${humanize(diff)}后到期，${tail}` }
  }
  return { level: 'normal', text: `剩余 ${humanize(diff)}，${tail}` }
}
