-- Movers scanner (StockedUp's nightly routine as code). Stages 'research' rows with notes starting "SCANNER".
insert into scoring_weight_overrides
  (signal_name, base_weight, adjustment_percent, effective_weight, confidence, sample_size, status, reason, last_updated)
values
  ('movers_scan_enabled',           1,   0, 1,   1, 0, 'active', '1 = scan the day''s movers after the close and stage trigger setups as research rows', now()),
  ('movers_scan_time_et',           0,   0, 0,   1, 0, 'active', '16:30', now()),
  ('scan_min_move_pct',             3,   0, 3,   1, 0, 'active', 'Mover must be up/down at least this % on the day', now()),
  ('scan_max_move_pct',             25,  0, 25,  1, 0, 'active', 'Skip one-day moves bigger than this % (events, not continuations)', now()),
  ('scan_min_rel_volume',           1.5, 0, 1.5, 1, 0, 'active', 'Volume must be at least this multiple of the 20-day average', now()),
  ('scan_max_price',                100, 0, 100, 1, 0, 'active', 'Skip stocks above this price', now()),
  ('scan_max_candidates',           10,  0, 10,  1, 0, 'active', 'Most setups the scanner stages per night', now()),
  ('scan_stop_pct',                 2,   0, 2,   1, 0, 'active', 'Stop this % back through the trigger', now()),
  ('scan_max_trigger_distance_pct', 4,   0, 4,   1, 0, 'active', 'Skip if the trigger is further than this % from the close', now())
on conflict (signal_name) do nothing;
