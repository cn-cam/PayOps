-- V004：签约代扣（周期扣款）所需的周期与下单附加参数。

ALTER TABLE pay_subscription
  ADD COLUMN charge_interval_days INT NOT NULL DEFAULT 30 COMMENT '代扣周期（天）' AFTER charge_fail_count;

ALTER TABLE pay_order
  ADD COLUMN extra VARCHAR(1024) NOT NULL DEFAULT '' COMMENT '下单附加参数 JSON（如代扣周期）' AFTER attach;
