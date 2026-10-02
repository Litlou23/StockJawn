-- Phone alerts for trade events; alert_log makes each one send once.
create table if not exists alert_log (
  pick_id text not null,
  event text not null,
  detail text,
  sent_at timestamptz not null default now(),
  primary key (pick_id, event)
);
alter table alert_log enable row level security;
revoke all on alert_log from anon, authenticated;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('trade_alerts_enabled', 1, 0, 1, 1, 0, 'active', '1 = phone alert on trigger hit, order, fill, sale, failed/blocked pick, Robinhood NOT READY', now())
on conflict (signal_name) do nothing;
