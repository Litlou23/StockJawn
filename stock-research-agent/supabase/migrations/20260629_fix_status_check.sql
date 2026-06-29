-- Fix watchlist_items status check constraint to include all valid statuses.
-- The original constraint only allowed 'active' and 'archived', but the app
-- also uses 'review_needed' and 'swap_candidate'.

ALTER TABLE watchlist_items DROP CONSTRAINT IF EXISTS watchlist_items_status_check;
ALTER TABLE watchlist_items ADD CONSTRAINT watchlist_items_status_check
  CHECK (status IN ('active', 'review_needed', 'swap_candidate', 'archived'));
