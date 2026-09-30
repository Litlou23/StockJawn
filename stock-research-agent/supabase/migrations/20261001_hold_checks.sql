-- Executor safety checks that used to live only in the job prompts.
insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('options_min_days_to_expiry', 7, 0, 7, 1, 0, 'active', 'Refuse option contracts expiring sooner than this many days', now()),
  ('block_earnings_during_hold', 1, 0, 1, 1, 0, 'active', '1 = refuse a pick that reports earnings before its exit (a report this morning is OK)', now())
on conflict (signal_name) do nothing;
