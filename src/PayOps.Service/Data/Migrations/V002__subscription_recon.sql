-- V002：自动续费订阅 + 对账。所有时间列为 UTC，金额单位分。

ALTER TABLE pay_order
  ADD COLUMN order_type TINYINT NOT NULL DEFAULT 0 COMMENT '0 一次性 1 订阅首购 2 订阅续费' AFTER pay_method,
  ADD COLUMN sub_no VARCHAR(32) NOT NULL DEFAULT '' COMMENT '所属订阅号' AFTER order_type,
  ADD KEY idx_sub (sub_no);

-- 订阅事件的业务单号形如 {订阅号}-{事件}-{时间}，超过 32 位
ALTER TABLE pay_notify_task MODIFY COLUMN biz_no VARCHAR(64) NOT NULL COMMENT '订单号/退款单号/订阅事件号';

CREATE TABLE IF NOT EXISTS pay_subscription (
  id              BIGINT        NOT NULL AUTO_INCREMENT,
  sub_no          VARCHAR(32)   NOT NULL COMMENT '平台订阅号',
  app_id          BIGINT        NOT NULL,
  app_code        VARCHAR(32)   NOT NULL,
  channel_id      BIGINT        NOT NULL,
  channel_code    VARCHAR(32)   NOT NULL,
  passage_id      BIGINT        NOT NULL,
  provider        VARCHAR(32)   NOT NULL,
  product_id      VARCHAR(128)  NOT NULL DEFAULT '',
  user_id         VARCHAR(64)   NOT NULL DEFAULT '',
  upstream_sub_id VARCHAR(128)  NOT NULL COMMENT '上游订阅标识：苹果 originalTransactionId / 华为 subscriptionId 等',
  upstream_token  VARCHAR(1024) NOT NULL DEFAULT '' COMMENT '后续查询/退订所需的上游凭证（如华为 purchaseToken）',
  status          TINYINT       NOT NULL DEFAULT 1 COMMENT '1 生效中 2 已过期 3 已撤销（退款/撤回）',
  auto_renew      TINYINT(1)    NOT NULL DEFAULT 1 COMMENT '1 到期自动续订 0 已退订（当前周期结束后失效）',
  amount          BIGINT        NOT NULL DEFAULT 0 COMMENT '最近一期金额（分）',
  period_start    DATETIME(3)   NULL COMMENT '当前周期开始',
  expires_at      DATETIME(3)   NULL COMMENT '当前周期到期',
  cancel_at       DATETIME(3)   NULL COMMENT '退订时间',
  renew_count     INT           NOT NULL DEFAULT 0 COMMENT '已续订次数',
  first_order_no  VARCHAR(32)   NOT NULL DEFAULT '',
  last_order_no   VARCHAR(32)   NOT NULL DEFAULT '',
  notify_url      VARCHAR(512)  NOT NULL DEFAULT '',
  attach          VARCHAR(512)  NOT NULL DEFAULT '',
  next_check_at   DATETIME(3)   NULL COMMENT '下次巡检时间（到期后主动向上游确认续订/过期）',
  check_count     INT           NOT NULL DEFAULT 0,
  created_at      DATETIME(3)   NOT NULL,
  updated_at      DATETIME(3)   NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_sub_no (sub_no),
  UNIQUE KEY uk_upstream (provider, upstream_sub_id),
  KEY idx_app_user (app_id, user_id),
  KEY idx_check (status, next_check_at),
  KEY idx_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='自动续费订阅';

CREATE TABLE IF NOT EXISTS pay_recon_batch (
  id                     BIGINT       NOT NULL AUTO_INCREMENT,
  passage_id             BIGINT       NOT NULL,
  provider               VARCHAR(32)  NOT NULL,
  bill_date              CHAR(10)     NOT NULL COMMENT '账单日 yyyy-MM-dd（业务时区）',
  status                 TINYINT      NOT NULL DEFAULT 0 COMMENT '0 待执行 1 已平账 2 有差异 3 失败',
  upstream_pay_count     INT          NOT NULL DEFAULT 0,
  upstream_pay_amount    BIGINT       NOT NULL DEFAULT 0,
  upstream_refund_count  INT          NOT NULL DEFAULT 0,
  upstream_refund_amount BIGINT       NOT NULL DEFAULT 0,
  local_pay_count        INT          NOT NULL DEFAULT 0,
  local_pay_amount       BIGINT       NOT NULL DEFAULT 0,
  local_refund_count     INT          NOT NULL DEFAULT 0,
  local_refund_amount    BIGINT       NOT NULL DEFAULT 0,
  diff_count             INT          NOT NULL DEFAULT 0,
  error_msg              VARCHAR(512) NOT NULL DEFAULT '',
  attempts               INT          NOT NULL DEFAULT 0,
  next_run_at            DATETIME(3)  NULL COMMENT '下次执行时间（兼作派发租约）',
  finished_at            DATETIME(3)  NULL,
  created_at             DATETIME(3)  NOT NULL,
  updated_at             DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_passage_date (passage_id, bill_date),
  KEY idx_run (status, next_run_at),
  KEY idx_date (bill_date)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='对账批次（每通道每天一条）';

CREATE TABLE IF NOT EXISTS pay_recon_diff (
  id                BIGINT       NOT NULL AUTO_INCREMENT,
  batch_id          BIGINT       NOT NULL,
  passage_id        BIGINT       NOT NULL,
  provider          VARCHAR(32)  NOT NULL,
  bill_date         CHAR(10)     NOT NULL,
  biz_type          TINYINT      NOT NULL COMMENT '1 支付 2 退款',
  diff_type         TINYINT      NOT NULL COMMENT '1 上游有本地无(或本地未成功) 2 本地有上游无 3 金额不一致',
  order_no          VARCHAR(64)  NOT NULL DEFAULT '',
  refund_no         VARCHAR(64)  NOT NULL DEFAULT '',
  upstream_trade_no VARCHAR(128) NOT NULL DEFAULT '',
  local_amount      BIGINT       NULL,
  upstream_amount   BIGINT       NULL,
  local_status      VARCHAR(32)  NOT NULL DEFAULT '' COMMENT '本地单据当时的状态描述',
  status            TINYINT      NOT NULL DEFAULT 0 COMMENT '0 待处理 1 已处理',
  remark            VARCHAR(255) NOT NULL DEFAULT '',
  handled_by        VARCHAR(32)  NOT NULL DEFAULT '',
  handled_at        DATETIME(3)  NULL,
  created_at        DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  KEY idx_batch (batch_id),
  KEY idx_status (status, bill_date),
  KEY idx_order (order_no)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='对账差异明细';
