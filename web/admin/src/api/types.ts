// 与 docs/04-管理后台API.md 一一对应的类型定义

export interface ApiEnvelope<T = unknown> {
  code: number
  message: string
  data: T
}

export interface PageQuery {
  page: number
  pageSize: number
}

export interface PageResult<T> {
  total: number
  items: T[]
}

export interface TimeRangeQuery {
  startTime?: string
  endTime?: string
}

/** 启停：1 启用，0 停用 */
export type EnableStatus = 0 | 1
/** 0 待支付，1 支付成功，2 已关闭，3 支付失败 */
export type OrderStatus = 0 | 1 | 2 | 3
/** 0 处理中，1 退款成功，2 退款失败 */
export type RefundStatus = 0 | 1 | 2
/** 0 待通知，1 通知成功，2 通知失败（重试耗尽） */
export type NotifyStatus = 0 | 1 | 2
/** 1 支付，2 退款 */
export type NotifyType = 1 | 2
export type Role = 'admin' | 'operator' | 'viewer'

// ---------- 认证 ----------
export interface UserInfo {
  id: number
  username: string
  displayName: string
  role: Role
}

export interface LoginInput {
  username: string
  password: string
}

export interface LoginResult {
  token: string
  expiresIn: number
  user: UserInfo
}

export interface ChangePasswordInput {
  oldPassword: string
  newPassword: string
}

// ---------- 元数据 ----------
export type ProviderCategory = 'direct' | 'store' | 'mock'
export type ConfigFieldType = 'text' | 'password' | 'textarea' | 'number' | 'switch' | 'select'

export interface ProviderMethod {
  code: string
  name: string
}

export interface ProviderCapabilities {
  query: boolean
  close: boolean
  refund: boolean
  receiptVerify: boolean
  serverNotify: boolean
}

export interface ConfigFieldOption {
  label: string
  value: string
}

export interface ConfigField {
  key: string
  label: string
  type: ConfigFieldType
  required: boolean
  secret: boolean
  placeholder?: string | null
  help?: string | null
  defaultValue?: string | null
  options?: ConfigFieldOption[] | null
}

export interface ProviderMeta {
  code: string
  name: string
  category: ProviderCategory
  methods: ProviderMethod[]
  capabilities: ProviderCapabilities
  configFields: ConfigField[]
  notifyNote?: string | null
}

// ---------- 仪表盘 ----------
export interface DashboardStat {
  orderCount: number
  paidCount: number
  paidAmount: number
  refundAmount: number
  /** 0~1 */
  successRate: number
}

export interface DashboardSummary {
  today: DashboardStat
  yesterday: DashboardStat
  pendingNotify: number
  failedNotify: number
  processingRefund: number
}

export interface TrendPoint {
  date: string
  orderCount: number
  paidCount: number
  paidAmount: number
}

export type DistributionDimension = 'channel' | 'passage' | 'provider'

export interface DistributionItem {
  key: string
  name: string
  orderCount: number
  paidCount: number
  paidAmount: number
  successRate: number
}

export interface SystemCheck {
  name: string
  ok: boolean
  message: string
}

export interface SystemNode {
  instanceId: string
  host: string
  roles: string[]
  workerId: number
  version: string
  startedAt: string
  lastSeen: string
}

export interface SystemInfo {
  version: string
  instanceId: string
  roles: string[]
  workerId: number
  startedAt: string
  checks: SystemCheck[]
  nodes: SystemNode[]
}

// ---------- 应用 ----------
export interface AppItem {
  id: number
  appCode: string
  name: string
  notifyUrl: string
  status: EnableStatus
  remark: string
  channelCount: number
  createdAt: string
  updatedAt: string
}

export interface AppBrief {
  id: number
  appCode: string
  name: string
  status: EnableStatus
}

export interface AppListQuery extends PageQuery {
  keyword?: string
  status?: EnableStatus
}

export interface AppCreateInput {
  appCode: string
  name: string
  notifyUrl: string
  remark: string
  status: EnableStatus
}

export type AppUpdateInput = Omit<AppCreateInput, 'appCode'>

export interface AppCreateResult {
  id: number
  appSecret: string
}

export interface AppSecretResult {
  appSecret: string
}

// ---------- 渠道 ----------
export interface ChannelItem {
  id: number
  appId: number
  appCode: string
  appName: string
  code: string
  name: string
  status: EnableStatus
  remark: string
  bindingCount: number
  createdAt: string
  updatedAt: string
}

export interface ChannelBrief {
  id: number
  appId: number
  code: string
  name: string
  status: EnableStatus
}

export interface ChannelListQuery extends PageQuery {
  appId?: number
  keyword?: string
  status?: EnableStatus
}

export interface ChannelCreateInput {
  appId: number
  code: string
  name: string
  remark: string
  status: EnableStatus
}

export interface ChannelUpdateInput {
  name: string
  remark: string
  status: EnableStatus
}

export interface ChannelCopyInput {
  targetAppId: number
  code: string
  name: string
}

export interface IdResult {
  id: number
}

// ---------- 渠道通道绑定（路由规则） ----------
export interface BindingInput {
  /** 允许的支付方式，空数组 = 该通道类型支持的全部 */
  methods: string[]
  /** 权重 0~10000；同优先级内按权重随机；0 = 备用 */
  weight: number
  /** 优先级，越小越优先 */
  priority: number
  /** 起付金额（分），0 = 不限 */
  minAmount: number
  /** 单笔上限（分），0 = 不限 */
  maxAmount: number
  /** 日成功金额限额（分），0 = 不限 */
  dailyLimitAmount: number
  /** 日成功笔数限额，0 = 不限 */
  dailyLimitCount: number
  /** 可用时段起 "HH:mm"，空串 = 全天 */
  startTime: string
  /** 可用时段止 "HH:mm"，支持跨零点 */
  endTime: string
  status: EnableStatus
  remark: string
}

export interface BindingCreateInput extends BindingInput {
  passageId: number
}

export interface BindingRuntime {
  todayAmount: number
  todayCount: number
  breakerOpen: boolean
  recentFailures: number
}

export interface BindingItem extends BindingInput {
  id: number
  channelId: number
  passageId: number
  passageName: string
  passageStatus: EnableStatus
  provider: string
  providerName: string
  createdAt: string
  updatedAt: string
  runtime: BindingRuntime
}

export interface RouteSimulateInput {
  payMethod: string
  amount: number
  times: number
}

export interface RouteSimulateItem {
  bindingId: number
  passageId: number
  passageName: string
  provider: string
  priority: number
  weight: number
  eligible: boolean
  reason: string
  hitCount: number
  /** 0~1 */
  hitRate: number
}

export interface RouteSimulateResult {
  payMethod: string
  amount: number
  times: number
  items: RouteSimulateItem[]
}

// ---------- 通道 ----------
export interface PassageItem {
  id: number
  name: string
  provider: string
  providerName: string
  status: EnableStatus
  remark: string
  bindingCount: number
  notifyUrl: string
  createdAt: string
  updatedAt: string
}

export interface PassageDetail extends PassageItem {
  config: Record<string, string>
}

export interface PassageBrief {
  id: number
  name: string
  provider: string
  providerName: string
  status: EnableStatus
}

export interface PassageListQuery extends PageQuery {
  provider?: string
  keyword?: string
  status?: EnableStatus
}

export interface PassageCreateInput {
  name: string
  provider: string
  status: EnableStatus
  remark: string
  config: Record<string, string>
}

export type PassageUpdateInput = Omit<PassageCreateInput, 'provider'>

export interface PassageTestResult {
  ok: boolean
  message: string
}

// ---------- 订单 ----------
export interface OrderItem {
  id: number
  orderNo: string
  mchOrderNo: string
  appId: number
  appCode: string
  channelId: number
  channelCode: string
  passageId: number
  passageName: string
  provider: string
  payMethod: string
  subject: string
  amount: number
  refundAmount: number
  currency: string
  status: OrderStatus
  upstreamTradeNo: string | null
  userId: string | null
  createdAt: string
  paidAt: string | null
  expireAt: string | null
}

export interface OrderDetail extends OrderItem {
  body: string | null
  productId: string | null
  clientIp: string | null
  buyerId: string | null
  notifyUrl: string | null
  returnUrl: string | null
  attach: string | null
  /** 下单返回给客户端的支付凭据（JSON 字符串，可能为空） */
  credential: string | null
  errorCode: string | null
  errorMsg: string | null
  closedAt: string | null
  checkCount: number
  nextCheckAt: string | null
  updatedAt: string
  bindingId: number | null
  refunds: RefundItem[]
  notifyTasks: NotifyTaskItem[]
  upstreamNotifies: UpstreamNotifyItem[]
}

export interface OrderListQuery extends PageQuery, TimeRangeQuery {
  orderNo?: string
  mchOrderNo?: string
  upstreamTradeNo?: string
  appId?: number
  channelId?: number
  passageId?: number
  provider?: string
  status?: OrderStatus
  userId?: string
}

export interface SyncResult<S extends number = number> {
  status: S
  changed: boolean
  message: string
}

export interface OrderRefundInput {
  amount: number
  reason: string
}

export interface OrderRefundResult {
  refundNo: string
  status: RefundStatus
}

export interface TestOrderInput {
  appId: number
  channelCode: string
  payMethod: string
  amount: number
  subject: string
  productId: string
  openId: string
  userId: string
}

export interface TestOrderCredential {
  type: string
  data: Record<string, unknown> | null
}

export interface TestOrderResult {
  orderNo: string
  mchOrderNo: string
  status: OrderStatus
  amount: number
  provider: string
  payMethod: string
  passageId: number
  passageName: string
  expireAt: string | null
  credential: TestOrderCredential | null
}

// ---------- 退款 ----------
export type RefundSource = 'api' | 'admin' | 'upstream'

export interface RefundItem {
  id: number
  refundNo: string
  orderNo: string
  mchRefundNo: string | null
  appId: number
  appCode: string
  passageId: number
  provider: string
  amount: number
  reason: string | null
  status: RefundStatus
  upstreamRefundNo: string | null
  errorMsg: string | null
  source: RefundSource
  createdAt: string
  successAt: string | null
}

export interface RefundListQuery extends PageQuery, TimeRangeQuery {
  refundNo?: string
  orderNo?: string
  mchRefundNo?: string
  appId?: number
  status?: RefundStatus
}

// ---------- 业务通知任务 ----------
export interface NotifyTaskItem {
  id: number
  appId: number
  appCode: string
  type: NotifyType
  bizNo: string
  url: string
  payload: string | null
  status: NotifyStatus
  attempts: number
  maxAttempts: number
  nextTime: string | null
  lastHttpStatus: number | null
  lastResponse: string | null
  lastTime: string | null
  createdAt: string
}

export interface NotifyTaskListQuery extends PageQuery, TimeRangeQuery {
  bizNo?: string
  appId?: number
  status?: NotifyStatus
  type?: NotifyType
}

// ---------- 上游回调日志 ----------
export interface UpstreamNotifyItem {
  id: number
  provider: string
  passageId: number | null
  kind: string
  orderNo: string | null
  verified: boolean
  result: string | null
  requestBody: string | null
  clientIp: string | null
  createdAt: string
}

export interface UpstreamNotifyListQuery extends PageQuery, TimeRangeQuery {
  provider?: string
  orderNo?: string
  passageId?: number
  verified?: boolean
}

// ---------- 用户与审计 ----------
export interface AdminUser {
  id: number
  username: string
  displayName: string
  role: Role
  status: EnableStatus
  lastLoginAt: string | null
  createdAt: string
}

export interface UserCreateInput {
  username: string
  displayName: string
  password: string
  role: Role
  status: EnableStatus
}

export interface UserUpdateInput {
  displayName: string
  role: Role
  status: EnableStatus
}

export interface AuditLogItem {
  id: number
  userId: number
  username: string
  action: string
  target: string | null
  detail: string | null
  ip: string | null
  createdAt: string
}

export interface AuditLogQuery extends PageQuery, TimeRangeQuery {
  username?: string
  action?: string
}
