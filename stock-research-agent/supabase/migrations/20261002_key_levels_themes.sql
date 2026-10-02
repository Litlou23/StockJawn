-- Key support/resistance on every pick, sector-theme ETFs in the scanner, and relative strength vs SPY.
alter table claude_daily_picks add column if not exists key_levels jsonb;

insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('scan_theme_etfs',        0,   0, 0,   1, 0, 'active', 'XLE,USO,XBI,SMH,XLF,KRE,GLD,SLV,XLU,XHB,ITB,JETS,TAN,URA,XME,ARKK', now()),
  ('scan_etf_min_move_pct',  1.5, 0, 1.5, 1, 0, 'active', 'Theme ETF must move at least this % on the day (or run 3 of the last 4 days)', now()),
  ('scan_max_themes',        4,   0, 4,   1, 0, 'active', 'Most theme ETF rows the scanner stages per night', now()),
  ('levels_fill_time_et',    0,   0, 0,   1, 0, 'active', '07:30', now()),
  ('levels_finnhub_enabled', 1,   0, 1,   1, 0, 'active', '1 = also ask Finnhub for support/resistance and chart patterns (falls back to our own levels if the plan refuses)', now())
on conflict (signal_name) do nothing;
