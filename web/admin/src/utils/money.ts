/**
 * 金额工具：接口统一使用整数「分」，界面展示与输入使用「元」。
 */

/** 元 → 分（四舍五入到分，基于定点字符串运算，避免 0.57 * 100 = 56.99999 之类的浮点误差） */
export function yuanToFen(yuan: number | string | null | undefined): number {
  if (yuan === null || yuan === undefined || yuan === '') return 0
  const n = typeof yuan === 'number' ? yuan : Number(String(yuan).trim())
  if (!Number.isFinite(n)) return 0
  const negative = n < 0
  const fixed = Math.abs(n).toFixed(6)
  const [intPart, fracPart] = fixed.split('.')
  let fen = Number(intPart) * 100 + Number(fracPart.slice(0, 2))
  if (Number(fracPart.charAt(2)) >= 5) fen += 1
  return negative ? -fen : fen
}

/** 分 → 元（数值，用于输入框回显） */
export function fenToYuan(fen: number | null | undefined): number {
  if (fen === null || fen === undefined || !Number.isFinite(fen)) return 0
  return Math.round(fen) / 100
}

/** 分 → "¥1,234.56" */
export function formatMoney(fen: number | null | undefined, symbol = '¥'): string {
  if (fen === null || fen === undefined || !Number.isFinite(fen)) return '-'
  const negative = fen < 0
  const abs = Math.round(Math.abs(fen))
  const intStr = String(Math.floor(abs / 100)).replace(/\B(?=(\d{3})+(?!\d))/g, ',')
  const decStr = String(abs % 100).padStart(2, '0')
  return `${negative ? '-' : ''}${symbol}${intStr}.${decStr}`
}

/** 限额类金额：0 表示不限 */
export function formatLimit(fen: number | null | undefined, unlimitedText = '不限'): string {
  if (!fen) return unlimitedText
  return formatMoney(fen)
}
