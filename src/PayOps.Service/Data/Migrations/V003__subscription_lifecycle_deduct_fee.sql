-- V003：订阅的升级/降级/暂停、签约代扣、手续费与资金账单核对。

ALTER TABLE pay_subscription
  ADD COLUMN mode TINYINT NOT NULL DEFAULT 0 COMMENT '0 应用商店订阅（商店扣款） 1 签约代扣（本系统发起扣款）' AFTER provider,
  ADD COLUMN next_product_id VARCHAR(128) NOT NULL DEFAULT '' COMMENT '下一期生效的商品（降级/换购预约）' AFTER product_id,
  ADD COLUMN paused_at DATETIME(3) NULL COMMENT '暂停时间' AFTER cancel_at,
  ADD COLUMN next_charge_at DATETIME(3) NULL COMMENT '代扣模式下下次发起扣款的时间' AFTER next_check_at,
  ADD COLUMN charge_fail_count INT NOT NULL DEFAULT 0 COMMENT '代扣连续失败次数' AFTER next_charge_at,
  ADD COLUMN upstream_product_id VARCHAR(128) NOT NULL DEFAULT '' COMMENT '上游当前生效的商品（可能与本系统商品不同）' AFTER upstream_token,
  ADD KEY idx_charge (status, next_charge_at);

-- 通道的手续费率（万分比，60 = 0.60%），用于手续费核对；0 表示不核对
ALTER TABLE pay_passage
  ADD COLUMN fee_rate_bp INT NOT NULL DEFAULT 0 COMMENT '手续费率（万分比），0 = 不核对' AFTER status;

ALTER TABLE pay_recon_batch
  ADD COLUMN kind TINYINT NOT NULL DEFAULT 1 COMMENT '1 交易账单 2 资金账单' AFTER bill_date,
  ADD COLUMN upstream_fee_amount BIGINT NOT NULL DEFAULT 0 COMMENT '上游账单手续费合计（分）' AFTER upstream_refund_amount,
  ADD COLUMN expected_fee_amount BIGINT NOT NULL DEFAULT 0 COMMENT '按通道费率计算的应收手续费（分）' AFTER upstream_fee_amount,
  ADD COLUMN fund_income BIGINT NOT NULL DEFAULT 0 COMMENT '资金账单收入合计（分）' AFTER expected_fee_amount,
  ADD COLUMN fund_expense BIGINT NOT NULL DEFAULT 0 COMMENT '资金账单支出合计（分）' AFTER fund_income,
  ADD COLUMN fund_refund BIGINT NOT NULL DEFAULT 0 COMMENT '资金账单退款支出（分）' AFTER fund_expense,
  ADD COLUMN fund_fee BIGINT NOT NULL DEFAULT 0 COMMENT '资金账单手续费支出（分）' AFTER fund_refund,
  DROP INDEX uk_passage_date,
  ADD UNIQUE KEY uk_passage_date_kind (passage_id, bill_date, kind);
