# PayOps 管理后台

PayOps（自用多渠道、多通道支付系统）的管理后台。Vue 3 + TypeScript + Vite + Element Plus + Pinia + vue-router + axios + ECharts。

接口契约见 `docs/04-管理后台API.md`，前端的类型定义（`src/api/types.ts`）与之一一对应，改接口先改文档。

## 开发与构建

```powershell
cd web/admin
npm install
npm run dev          # 开发：http://localhost:5175
npm run build        # vue-tsc --noEmit 类型检查 + vite build，产物在 web/admin/dist
npm run type-check   # 只做类型检查
npm run preview      # 本地预览 dist（同样使用 5175 端口与 /api 代理）
```

- 开发端口固定 **5175**（`strictPort`，被占用会直接报错而不是换端口）。
- 代理：`/api` → `http://localhost:5080`（后端 `dotnet run --project src/PayOps.Server`）。
  需要联调其它环境时用环境变量覆盖：`$env:PAYOPS_API_TARGET = 'http://10.0.0.8:5080'; npm run dev`。
- 生产：`base: '/'`、history 路由，所有请求都是相对路径 `/api/admin/...`，由后端托管 `dist` 并对未知路径回退到 `index.html`。

## 目录结构

```text
src/
  main.ts                 入口：Pinia、路由、Element Plus（zh-cn）、http 注入、全局错误处理
  App.vue
  router/index.ts         路由表、登录与角色守卫（meta.roles）
  layouts/MainLayout.vue  左侧菜单 + 顶栏（当前用户、修改密码、退出）
  stores/
    auth.ts               token（localStorage）、当前用户、角色判断 canWrite / canViewSecret
    meta.ts               支付类型元数据（/meta/providers）与应用下拉缓存
  api/
    http.ts               axios 实例：带 token、解包 {code,message,data}、401 跳登录、403 提示无权限
    types.ts              与接口契约对应的全部类型
    *.ts                  按模块划分的接口方法
  composables/
    usePagedTable.ts      分页表格通用逻辑（查询条件、分页、loading、防乱序）
  utils/
    money.ts              分 ↔ 元（整数运算，避免浮点误差）
    time.ts               UTC ISO ↔ 本地时间、时间范围筛选参数
    enums.ts              状态枚举的文案与标签颜色
    common.ts             复制、二次确认、表单校验、JSON 美化等
    echarts.ts            ECharts 按需注册
  components/             MoneyInput、EnumTag、JsonViewer、TablePager、TimeRangePicker、
                          AppSelect、ProviderSelect、EChart、ContentDialog、
                          OrderDetailDrawer（订单详情与操作，多个页面复用）、ChangePasswordDialog
  views/
    login/                登录
    dashboard/            仪表盘（今日/昨日、趋势、分布、集群节点）
    apps/                 应用管理
    channels/             渠道管理、渠道通道配置（BindingsView + BindingDialog + RouteSimulatePanel）
    passages/             通道管理（配置表单由 provider 元数据动态渲染）
    orders/               订单管理、测试下单
    refunds/              退款管理
    notify/               业务通知、上游回调日志
    system/               用户管理、操作审计
    error/                403 / 404
```

## 约定

- **金额**：接口一律是整数「分」。展示用 `formatMoney`（`¥12.34`），输入用 `MoneyInput`（界面输入元，`v-model` 是分）。
- **时间**：接口是 UTC ISO 字符串。展示用 `formatTime`（本地时间 `YYYY-MM-DD HH:mm:ss`），筛选用 `rangeToQuery` 转回 UTC。
- **支付类型**：不在前端写死任何 provider 列表或配置字段，全部来自 `GET /api/admin/meta/providers`。
- **角色**：`admin` 全部；`operator` 无用户管理、不能查看 / 重置应用密钥；`viewer` 只读（写操作按钮不渲染，测试下单不可见）。
  页面里用 `auth.canWrite` / `auth.canViewSecret` / `auth.isAdmin` 判断，路由用 `meta.roles` 限制。
- **错误提示**：接口错误已在 `api/http.ts` 统一弹出，页面里只需要 `try { ... } finally { loading = false }`，不要重复提示。
- **表格行类型**：Element Plus 的列插槽 `row` 没有具体类型，调用处理函数时写 `fn(row as XxxItem)`。

## 已知的坑

- **模板属性值里不要出现 `//`**（例如 `placeholder="https://..."`）。vue-tsc 3.3 生成类型检查代码时会把它当成行注释，
  导致同一组件后面的 `v-model` 报莫名其妙的类型错误。含 URL 的文案放到 `<script>` 常量里再绑定（见 `AppsView.vue`）。
  文本节点（标签之间的文字）不受影响。
- **TypeScript 固定在 6.x**：vue-tsc 3.3 尚不支持 TypeScript 7（原生版不再提供 `typescript/lib/tsc`），升级前先确认 vue-tsc 已适配。
- Element Plus 的表格会用一个空行（`row = {}`）预渲染每列插槽来测量宽度，插槽里访问嵌套字段要用可选链（如 `row.runtime?.breakerOpen`）。
