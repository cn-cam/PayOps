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
/** 1 支付，2 退款，3 订阅状态变更 */
export type NotifyType = 1 | 2 | 3
/** 0 一次性支付，1 订阅首购，2 订阅续费（由上游扣款事件生成，生来已支付） */
export type OrderType = 0 | 1 | 2
/** 1 生效中，2 已过期，3 已撤销（退款/撤回） */
export type SubscriptionStatus = 1 | 2 | 3
/** 0 待执行，1 已平账，2 有差异，3 失败 */
export type ReconStatus = 0 | 1 | 2 | 3
/** 1 支付，2 退款 */
export type ReconBizType = 1 | 2
/** 1 上游有本地无（或本地未成功），2 本地有上游无，3 金额不一致 */
export type ReconDiffType = 1 | 2 | 3
/** 0 待处理，1 已处理 */
export type ReconDiffStatus = 0 | 1
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
  /** 支持自动续费订阅 */
  subscription: boolean
  /** 支持服务端退订（否则只能用户在商店侧退订） */
  subscriptionCancel: boolean
  /** 支持下载对账单（可对账） */
  bill: boolean
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
  activeSubscriptions: number
  openReconDiffs: number
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
  orderType: OrderType
  /** 所属订阅号；非订阅订单为空 */
  subNo: string | null
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
  orderType?: OrderType
  subNo?: string
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
  /** true 为订阅首购，仅 capabilities.subscription 的支付类型可用 */
  subscribe: boolean
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
  orderType: OrderType
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

// ---------- 订阅（自动续费） ----------
export interface SubscriptionItem {
  id: number
  subNo: string
  appId: number
  appCode: string
  channelCode: string
  passageId: number
  passageName: string
  provider: string
  productId: string | null
  userId: string | null
  upstreamSubId: string | null
  status: SubscriptionStatus
  /** false 表示已退订，当前周期结束后失效 */
  autoRenew: boolean
  /** 最近一期金额（分） */
  amount: number
  periodStart: string | null
  expiresAt: string | null
  cancelAt: string | null
  renewCount: number
  firstOrderNo: string | null
  lastOrderNo: string | null
  createdAt: string
  updatedAt: string
}

export interface SubscriptionDetail extends SubscriptionItem {
  notifyUrl: string | null
  attach: string | null
  nextCheckAt: string | null
  checkCount: number
  /** 首购单 + 各期续费单，按时间倒序 */
  orders: OrderItem[]
  /** 订阅状态变更通知（type=3） */
  notifyTasks: NotifyTaskItem[]
}

export interface SubscriptionListQuery extends PageQuery, TimeRangeQuery {
  subNo?: string
  appId?: number
  provider?: string
  status?: SubscriptionStatus
  autoRenew?: boolean
  userId?: string
  productId?: string
}

/** renew 续费一期 / cancel 退订 / resume 恢复续订 / expire 到期 / revoke 撤销 */
export type SubscriptionMockEvent = 'renew' | 'cancel' | 'resume' | 'expire' | 'revoke'

// ---------- 对账 ----------
export interface ReconBatchItem {
  id: number
  passageId: number
  passageName: string
  provider: string
  providerName: string
  /** yyyy-MM-dd */
  billDate: string
  status: ReconStatus
  upstreamPayCount: number
  upstreamPayAmount: number
  upstreamRefundCount: number
  upstreamRefundAmount: number
  localPayCount: number
  localPayAmount: number
  localRefundCount: number
  localRefundAmount: number
  /** 差异总数 */
  diffCount: number
  /** 其中未处理数 */
  openDiffCount: number
  errorMsg: string | null
  attempts: number
  nextRunAt: string | null
  finishedAt: string | null
  createdAt: string
}

export interface ReconBatchListQuery extends PageQuery {
  passageId?: number
  provider?: string
  status?: ReconStatus
  /** yyyy-MM-dd，按账单日筛选，闭区间 */
  startDate?: string
  endDate?: string
}

export interface ReconBatchCreateInput {
  passageId: number
  /** yyyy-MM-dd，必须早于今天 */
  billDate: string
}

export interface ReconDiffItem {
  id: number
  batchId: number
  passageId: number
  passageName: string
  provider: string
  billDate: string
  bizType: ReconBizType
  diffType: ReconDiffType
  orderNo: string | null
  refundNo: string | null
  upstreamTradeNo: string | null
  /** 可能为 null（本地一侧不存在） */
  localAmount: number | null
  /** 可能为 null（上游一侧不存在） */
  upstreamAmount: number | null
  /** 本地单据当时的状态描述，如"待支付""订单不存在" */
  localStatus: string | null
  status: ReconDiffStatus
  remark: string | null
  handledBy: string | null
  handledAt: string | null
  createdAt: string
}

export interface ReconDiffListQuery extends PageQuery {
  batchId?: number
  passageId?: number
  status?: ReconDiffStatus
  diffType?: ReconDiffType
  bizType?: ReconBizType
  orderNo?: string
  startDate?: string
  endDate?: string
}

export interface ReconDiffHandleInput {
  remark: string
}