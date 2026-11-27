import dayjs from 'dayjs'

export const TIME_FORMAT = 'YYYY-MM-DD HH:mm:ss'

export type DateRange = [Date, Date] | null

/** ISO UTC 字符串 → 本地时间 YYYY-MM-DD HH:mm:ss */
export function formatTime(value: string | number | Date | null | undefined, empty = '-'): string {
  if (value === null || value === undefined || value === '') return empty
  const d = dayjs(value)
  return d.isValid() ? d.format(TIME_FORMAT) : empty
}

/** 本地时间 → ISO UTC 字符串 */
export function toIsoUtc(value: Date | string | number): string {
  return dayjs(value).toISOString()
}

/** 时间范围选择器的值 → 接口筛选参数（ISO UTC） */
export function rangeToQuery(range: DateRange | undefined): { startTime?: string; endTime?: string } {
  if (!range || range.length !== 2 || !range[0] || !range[1]) return {}
  return {
    startTime: toIsoUtc(range[0]),
    endTime: dayjs(range[1]).millisecond(999).toISOString(),
  }
}

/** 距今多久（用于节点心跳展示） */
export function fromNow(value: string | null | undefined): string {
  if (!value) return '-'
  const d = dayjs(value)
  if (!d.isValid()) return '-'
  const seconds = Math.max(0, dayjs().diff(d, 'second'))
  if (seconds < 60) return `${seconds} 秒前`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} 分钟前`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} 小时前`
  return `${Math.floor(seconds / 86400)} 天前`
}

/** 运行时长 */
export function uptime(startedAt: string | null | undefined): string {
  if (!startedAt) return '-'
  const d = dayjs(startedAt)
  if (!d.isValid()) return '-'
  const minutes = Math.max(0, dayjs().diff(d, 'minute'))
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  const mins = minutes % 60
  if (days > 0) return `${days} 天 ${hours} 小时`
  if (hours > 0) return `${hours} 小时 ${mins} 分钟`
  return `${mins} 分钟`
}

function dayStart(offsetDays = 0): Date {
  return dayjs().add(offsetDays, 'day').startOf('day').toDate()
}

function dayEnd(offsetDays = 0): Date {
  return dayjs().add(offsetDays, 'day').endOf('day').millisecond(0).toDate()
}

export function todayRange(): [Date, Date] {
  return [dayStart(), dayEnd()]
}

export const rangeShortcuts = [
  { text: '今天', value: () => [dayStart(), dayEnd()] },
  { text: '昨天', value: () => [dayStart(-1), dayEnd(-1)] },
  { text: '近 7 天', value: () => [dayStart(-6), dayEnd()] },
  { text: '近 30 天', value: () => [dayStart(-29), dayEnd()] },
]
