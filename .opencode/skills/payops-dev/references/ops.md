# 部署与排障

完整说明见 `docs/05-部署手册.md`。这里是速查。

## 三种形态

| 形态 | 命令 |
| --- | --- |
| 本地开发 | `dotnet run --project src/PayOps.Server`（Development，连本机 `mysql.dev` / `redis.dev` / `rabbitmq.dev`） |
| 单体 | `pwsh deploy/standalone/publish.ps1` → `artifacts/payops`，`Roles=All` |
| Compose（自带中间件） | `cd deploy/compose; cp .env.example .env; docker compose up -d --build` |
| Compose（复用本机演示中间件） | `docker compose -f docker-compose.dev.yml --env-file .env.dev up -d --build` |
| K8s | `kubectl apply -k deploy/k8s/overlays/demo`（或 `prod`） |

角色：`PayOps__Roles = All | Gateway | Admin | Worker`（可逗号组合）。同一个镜像。

本机端口占用提醒：5173、8080~8088、3000、9009 已被其他容器占用；后端用 5080，前端开发用 5175，Compose 入口默认 8090——启动前先确认端口空闲，冲突时改 `HTTP_PORT`。

## 必须一致 / 必须备份

- `PayOps:Security:MasterKey`：所有副本一致；换了它，库里已有的通道凭证与应用密钥全部解不开。
- `PayOps:Security:JwtKey`：所有副本一致，否则后台登录在副本间来回失效。
- `PayOps:RabbitMq:QueueType`：队列建好后不能改，改了会 `PRECONDITION_FAILED`（需先删队列）。

## 排障速查

| 现象 | 看哪里 |
| --- | --- |
| 下单返回 `NO_AVAILABLE_PASSAGE` | message 里列了每个通道被排除的原因；后台"渠道通道配置 → 路由模拟"复现 |
| 下单返回 `PASSAGE_ERROR` | 所有尝试的通道都失败；看日志"下单失败，尝试下一个通道"与订单的 `error_msg` |
| 通道被跳过 | 绑定页的熔断标记；Redis `payops:breaker:open:{bindingId}`；可在后台"恢复" |
| 用户付了钱订单没成功 | 后台"上游回调日志"看是否收到、验签是否通过、处理结果；订单详情点"同步"补单 |
| 回调验签失败 | 通道凭证（平台公钥/Token/AppSecret）是否正确；`PublicBaseUrl` 与反向代理是否改写了请求体 |
| 回调提示"订单不属于该通道" | 上游后台配置的回调地址里的通道 ID 填错了 |
| 业务没收到通知 | 后台"业务通知"看 `lastHttpStatus / lastResponse`；业务端必须返回 `success`；可手动重发 |
| 通知一直"待通知" | Worker 是否在运行（仪表盘节点列表有无 Worker 角色）；RabbitMQ 是否连接；队列 `payops.notify` 是否堆积 |
| 后台登录后马上 401 | 各副本 `JwtKey` 不一致，或系统时间偏差过大 |
| 启动报"未知的密文格式 / 解密失败" | `MasterKey` 与写入时不一致 |
| 启动卡在迁移 | 另一个副本持有 `GET_LOCK('payops_migrate')`；或 DDL 被长事务阻塞 |

## 验证多副本

1. 起两个以上副本后，后台"仪表盘 → 集群节点"应看到每个副本及互不相同的节点号。
2. `pwsh scripts/demo.ps1 -BaseUrl <入口>`（容器部署加 `-InternalBaseUrl http://nginx`）应全部 `[PASS]`。
3. 运行中杀掉任意一个副本，再跑一遍脚本仍应全部通过。
