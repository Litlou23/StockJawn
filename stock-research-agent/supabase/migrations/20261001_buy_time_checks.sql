-- Intraday checks right before a buy.
insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('trigger_confirm_seconds', 60, 0, 60, 1, 0, 'active', 'The break must hold this long before buying (0 = off)', now()),
  ('spy_gate_pct',            1,  0, 1,  1, 0, 'active', 'No bullish buys while SPY is down this % today, no bearish buys while up this % (0 = off)', now()),
  ('options_max_spread_pct',  20, 0, 20, 1, 0, 'active', 'Wait if the option bid/ask spread is wider than this % of the ask (0 = off)', now()),
  ('inverse_etfs',            0,  0, 0,  1, 0, 'active', 'SPXS,SQQQ,UVXY,SPXU,SDS,SH,PSQ,VXX', now())
on conflict (signal_name) do nothing;
