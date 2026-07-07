-- ============================================================
-- Portfolio Challenge System — Phase 1
-- Supports simulated portfolio growth tracking ($100 → $1,000)
-- ============================================================

-- portfolio_challenges: each row is a challenge (e.g. "Small Account Challenge")
create table if not exists portfolio_challenges (
    id              uuid primary key default gen_random_uuid(),
    name            text not null,
    starting_balance double precision not null,
    current_balance  double precision not null,
    target_balance   double precision not null,
    current_cash     double precision not null,
    buying_power     double precision not null,
    realized_profit  double precision not null default 0,
    unrealized_profit double precision not null default 0,
    total_return     double precision not null default 0,
    percent_return   double precision not null default 0,
    number_of_trades int not null default 0,
    winning_trades   int not null default 0,
    losing_trades    int not null default 0,
    win_rate         double precision not null default 0,
    status           text not null default 'active',          -- active, completed, paused, abandoned
    portfolio_mode   text not null default 'swing_trading',   -- swing_trading, day_trading, options_only, stock_only, mixed
    risk_profile     text not null default 'moderate',        -- conservative, moderate, aggressive
    notes            text,
    created_at       timestamptz not null default now(),
    updated_at       timestamptz not null default now()
);

-- portfolio_positions: each row is a paper trade linked to a challenge
create table if not exists portfolio_positions (
    id                  uuid primary key default gen_random_uuid(),
    portfolio_id        uuid not null references portfolio_challenges(id),
    prediction_id       text,                                 -- links to prediction_candidates.id
    ticker              text not null,
    asset_type          text not null default 'stock',        -- stock, option
    entry_date          timestamptz not null default now(),
    exit_date           timestamptz,
    entry_price         double precision not null,
    exit_price          double precision,
    quantity            double precision not null,
    dollars_invested    double precision not null,
    dollars_returned    double precision,
    profit_loss         double precision,
    percent_gain        double precision,
    reason_entered      text,
    reason_exited       text,
    status              text not null default 'open',         -- open, closed, cancelled
    created_at          timestamptz not null default now(),
    updated_at          timestamptz not null default now()
);

-- Indexes for common queries
create index if not exists idx_portfolio_positions_portfolio_id on portfolio_positions(portfolio_id);
create index if not exists idx_portfolio_positions_status on portfolio_positions(status);
create index if not exists idx_portfolio_positions_ticker on portfolio_positions(ticker);
create index if not exists idx_portfolio_challenges_status on portfolio_challenges(status);

-- Seed the default challenge
insert into portfolio_challenges (
    name, starting_balance, current_balance, target_balance,
    current_cash, buying_power, status, portfolio_mode, risk_profile, notes
) values (
    'Small Account Challenge',
    100.00,
    100.00,
    1000.00,
    100.00,
    100.00,
    'active',
    'swing_trading',
    'moderate',
    'Grow a simulated $100 account into $1,000 using swing trading. The foundational portfolio challenge.'
);
