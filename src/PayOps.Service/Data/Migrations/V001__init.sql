-- PayOps 初始化结构。所有时间列均为 UTC。金额单位：分。

CREATE TABLE IF NOT EXISTS pay_app (
  id            BIGINT       NOT NULL AUTO_INCREMENT,
  app_code      VARCHAR(32)  NOT NULL COMMENT '应用编码（接入方 AppId）',
  name          VARCHAR(64)  NOT NULL,
  secret_cipher VARCHAR(512) NOT NULL COMMENT '接入密钥（AES-GCM 密文）',
  notify_url    VARCHAR(512) NOT NULL DEFAULT '' COMMENT '默认业务通知地址',
  status        TINYINT      NOT NULL DEFAULT 1,
  remark        VARCHAR(255) NOT NULL DEFAULT '',
  created_at    DATETIME(3)  NOT NULL,
  updated_at    DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_app_code (app_code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='应用（自有业务产品）';

CREATE TABLE IF NOT EXISTS pay_channel (
  id         BIGINT       NOT NULL AUTO_INCREMENT,
  app_id     BIGINT       NOT NULL,
  code       VARCHAR(32)  NOT NULL COMMENT '渠道编码，如 official / huawei / appstore',
  name       VARCHAR(64)  NOT NULL,
  status     TINYINT      NOT NULL DEFAULT 1,
  remark     VARCHAR(255) NOT NULL DEFAULT '',
  created_at DATETIME(3)  NOT NULL,
  updated_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_app_code (app_id, code)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='渠道（应用的分发渠道）';

CREATE TABLE IF NOT EXISTS pay_passage (
  id            BIGINT       NOT NULL AUTO_INCREMENT,
  name          VARCHAR(64)  NOT NULL,
  provider      VARCHAR(32)  NOT NULL COMMENT '支付类型：wechat/alipay/douyin/huawei/oppo/vivo/xiaomi/honor/apple/mock',
  config_cipher MEDIUMTEXT   NOT NULL COMMENT '凭证配置 JSON（AES-GCM 密文）',
  status        TINYINT      NOT NULL DEFAULT 1,
  remark        VARCHAR(255) NOT NULL DEFAULT '',
  created_at    DATETIME(3)  NOT NULL,
  updated_at    DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  KEY idx_provider (provider)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='通道（一套商户/应用凭证）';

CREATE TABLE IF NOT EXISTS pay_channel_passage (
  id                 BIGINT       NOT NULL AUTO_INCREMENT,
  channel_id         BIGINT       NOT NULL,
  passage_id         BIGINT       NOT NULL,
  provider           VARCHAR(32)  NOT NULL,
  methods            VARCHAR(255) NOT NULL DEFAULT '' COMMENT '允许的支付方式，逗号分隔；空=全部',
  weight             INT          NOT NULL DEFAULT 100 COMMENT '权重；0=备用',
  priority           INT          NOT NULL DEFAULT 0 COMMENT '优先级，越小越优先',
  min_amount         BIGINT       NOT NULL DEFAULT 0 COMMENT '起付金额（分）',
  max_amount         BIGINT       NOT NULL DEFAULT 0 COMMENT '单笔上限（分），0=不限',
  daily_limit_amount BIGINT       NOT NULL DEFAULT 0 COMMENT '日成功金额限额（分），0=不限',
  daily_limit_count  INT          NOT NULL DEFAULT 0 COMMENT '日成功笔数限额，0=不限',
  start_time         VARCHAR(5)   NOT NULL DEFAULT '' COMMENT '可用时段起 HH:mm',
  end_time           VARCHAR(5)   NOT NULL DEFAULT '' COMMENT '可用时段止 HH:mm',
  status             TINYINT      NOT NULL DEFAULT 1,
  remark             VARCHAR(255) NOT NULL DEFAULT '',
  created_at         DATETIME(3)  NOT NULL,
  updated_at         DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  KEY idx_channel (channel_id),
  KEY idx_passage (passage_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='渠道通道绑定（路由规则）';

CREATE TABLE IF NOT EXISTS pay_order (
  id                BIGINT        NOT NULL AUTO_INCREMENT,
  order_no          VARCHAR(32)   NOT NULL COMMENT '平台订单号',
  app_id            BIGINT        NOT NULL,
  app_code          VARCHAR(32)   NOT NULL,
  mch_order_no      VARCHAR(64)   NOT NULL COMMENT '业务订单号',
  channel_id        BIGINT        NOT NULL,
  channel_code      VARCHAR(32)   NOT NULL,
  passage_id        BIGINT        NOT NULL DEFAULT 0,
  binding_id        BIGINT        NOT NULL DEFAULT 0,
  provider          VARCHAR(32)   NOT NULL,
  pay_method        VARCHAR(32)   NOT NULL,
  subject           VARCHAR(128)  NOT NULL,
  body              VARCHAR(255)  NOT NULL DEFAULT '',
  amount            BIGINT        NOT NULL,
  refund_amount     BIGINT        NOT NULL DEFAULT 0 COMMENT '已占用退款额（处理中+成功）',
  currency          VARCHAR(8)    NOT NULL DEFAULT 'CNY',
  status            TINYINT       NOT NULL DEFAULT 0 COMMENT '0待支付 1成功 2关闭 3失败',
  upstream_trade_no VARCHAR(128)  NULL COMMENT '上游交易号',
  user_id           VARCHAR(64)   NOT NULL DEFAULT '',
  buyer_id          VARCHAR(128)  NOT NULL DEFAULT '',
  product_id        VARCHAR(128)  NOT NULL DEFAULT '',
  client_ip         VARCHAR(64)   NOT NULL DEFAULT '',
  notify_url        VARCHAR(512)  NOT NULL DEFAULT '',
  return_url        VARCHAR(512)  NOT NULL DEFAULT '',
  attach            VARCHAR(512)  NOT NULL DEFAULT '',
  credential        TEXT          NULL,
  error_code        VARCHAR(64)   NOT NULL DEFAULT '',
  error_msg         VARCHAR(512)  NOT NULL DEFAULT '',
  expire_at         DATETIME(3)   NOT NULL,
  paid_at           DATETIME(3)   NULL,
  closed_at         DATETIME(3)   NULL,
  next_check_at     DATETIME(3)   NULL COMMENT '下次主动查单/超时关单时间',
  check_count       INT           NOT NULL DEFAULT 0,
  created_at        DATETIME(3)   NOT NULL,
  updated_at        DATETIME(3)   NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_order_no (order_no),
  UNIQUE KEY uk_app_mch_order (app_id, mch_order_no),
  UNIQUE KEY uk_upstream (provider, upstream_trade_no),
  KEY idx_check (status, next_check_at),
  KEY idx_created (created_at),
  KEY idx_channel_created (channel_id, created_at),
  KEY idx_passage_created (passage_id, created_at),
  KEY idx_user (user_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='支付订单';

CREATE TABLE IF NOT EXISTS pay_refund (
  id                 BIGINT       NOT NULL AUTO_INCREMENT,
  refund_no          VARCHAR(32)  NOT NULL,
  order_no           VARCHAR(32)  NOT NULL,
  app_id             BIGINT       NOT NULL,
  app_code           VARCHAR(32)  NOT NULL,
  mch_refund_no      VARCHAR(64)  NOT NULL,
  passage_id         BIGINT       NOT NULL,
  provider           VARCHAR(32)  NOT NULL,
  amount             BIGINT       NOT NULL,
  reason             VARCHAR(255) NOT NULL DEFAULT '',
  status             TINYINT      NOT NULL DEFAULT 0 COMMENT '0处理中 1成功 2失败',
  upstream_refund_no VARCHAR(128) NULL,
  notify_url         VARCHAR(512) NOT NULL DEFAULT '',
  error_msg          VARCHAR(512) NOT NULL DEFAULT '',
  source             VARCHAR(16)  NOT NULL DEFAULT 'api' COMMENT 'api/admin/upstream',
  success_at         DATETIME(3)  NULL,
  next_check_at      DATETIME(3)  NULL,
  check_count        INT          NOT NULL DEFAULT 0,
  created_at         DATETIME(3)  NOT NULL,
  updated_at         DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_refund_no (refund_no),
  UNIQUE KEY uk_app_mch_refund (app_id, mch_refund_no),
  KEY idx_order (order_no),
  KEY idx_check (status, next_check_at),
  KEY idx_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='退款单';

CREATE TABLE IF NOT EXISTS pay_notify_task (
  id               BIGINT       NOT NULL AUTO_INCREMENT,
  app_id           BIGINT       NOT NULL,
  app_code         VARCHAR(32)  NOT NULL,
  type             TINYINT      NOT NULL COMMENT '1支付 2退款',
  biz_no           VARCHAR(32)  NOT NULL COMMENT '订单号/退款单号',
  url              VARCHAR(512) NOT NULL DEFAULT '',
  payload          TEXT         NOT NULL,
  status           TINYINT      NOT NULL DEFAULT 0 COMMENT '0待通知 1成功 2失败',
  attempts         INT          NOT NULL DEFAULT 0,
  max_attempts     INT          NOT NULL DEFAULT 10,
  next_time        DATETIME(3)  NOT NULL COMMENT '下次投递时间（同时充当租约）',
  last_http_status INT          NOT NULL DEFAULT 0,
  last_response    VARCHAR(512) NOT NULL DEFAULT '',
  last_time        DATETIME(3)  NULL,
  event_published  TINYINT(1)   NOT NULL DEFAULT 0 COMMENT '领域事件是否已发布到 MQ',
  created_at       DATETIME(3)  NOT NULL,
  updated_at       DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_biz (type, biz_no),
  KEY idx_dispatch (status, next_time),
  KEY idx_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='业务通知任务（事务发件箱）';

CREATE TABLE IF NOT EXISTS pay_upstream_notify (
  id           BIGINT       NOT NULL AUTO_INCREMENT,
  provider     VARCHAR(32)  NOT NULL,
  passage_id   BIGINT       NOT NULL,
  kind         VARCHAR(16)  NOT NULL DEFAULT '',
  order_no     VARCHAR(32)  NOT NULL DEFAULT '',
  verified     TINYINT(1)   NOT NULL DEFAULT 0,
  result       VARCHAR(512) NOT NULL DEFAULT '',
  request_body TEXT         NULL,
  client_ip    VARCHAR(64)  NOT NULL DEFAULT '',
  created_at   DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  KEY idx_order (order_no),
  KEY idx_created (created_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='上游回调日志';

CREATE TABLE IF NOT EXISTS sys_user (
  id            BIGINT       NOT NULL AUTO_INCREMENT,
  username      VARCHAR(32)  NOT NULL,
  display_name  VARCHAR(64)  NOT NULL DEFAULT '',
  password_hash VARCHAR(255) NOT NULL,
  role          VARCHAR(16)  NOT NULL DEFAULT 'viewer',
  status        TINYINT      NOT NULL DEFAULT 1,
  last_login_at DATETIME(3)  NULL,
  created_at    DATETIME(3)  NOT NULL,
  updated_at    DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  UNIQUE KEY uk_username (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='后台用户';

CREATE TABLE IF NOT EXISTS sys_audit_log (
  id         BIGINT       NOT NULL AUTO_INCREMENT,
  user_id    BIGINT       NOT NULL DEFAULT 0,
  username   VARCHAR(32)  NOT NULL DEFAULT '',
  action     VARCHAR(64)  NOT NULL,
  target     VARCHAR(128) NOT NULL DEFAULT '',
  detail     TEXT         NULL,
  ip         VARCHAR(64)  NOT NULL DEFAULT '',
  created_at DATETIME(3)  NOT NULL,
  PRIMARY KEY (id),
  KEY idx_created (created_at),
  KEY idx_user (username)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COMMENT='操作审计';
