import { NextRequest, NextResponse } from 'next/server';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';

// GET: fetch today's pending picks for the approval page
export async function GET() {
  if (!isSupabaseConfigured()) {
    return NextResponse.json({ error: 'DB not configured' }, { status: 500 });
  }
  const sb = getSupabaseClient();

  // Get today's picks that need approval (exclude CASH entries)
  const { data: picks, error } = await sb
    .from('claude_daily_picks')
    .select('id, ticker, direction, conviction, entry_price, target_price, stop_price, total_score, notes, catalyst, pick_date, approval_status, sector, execution_notes')
    .neq('ticker', 'CASH')
    .in('approval_status', ['pending', 'approved', 'executing', 'executed', 'ready'])
    .gte('pick_date', new Date().toISOString().split('T')[0])
    .order('total_score', { ascending: false });

  if (error) {
    return NextResponse.json({ error: error.message }, { status: 500 });
  }

  // Get safeguard configs
  const { data: configs } = await sb
    .from('scoring_weight_overrides')
    .select('signal_name, effective_weight')
    .in('signal_name', ['approval_pin', 'approval_expiry_minutes', 'max_position_pct', 'circuit_breaker_weekly_loss_pct']);

  const configMap: Record<string, number> = {};
  configs?.forEach((c: { signal_name: string; effective_weight: number }) => {
    configMap[c.signal_name] = c.effective_weight;
  });

  return NextResponse.json({ picks: picks || [], configs: configMap });
}

// POST: approve a pick
export async function POST(req: NextRequest) {
  if (!isSupabaseConfigured()) {
    return NextResponse.json({ error: 'DB not configured' }, { status: 500 });
  }

  const body = await req.json();
  const { pickId, pin } = body;

  if (!pickId || !pin) {
    return NextResponse.json({ error: 'Missing pickId or pin' }, { status: 400 });
  }

  const sb = getSupabaseClient();

  // Verify PIN
  const { data: pinConfig } = await sb
    .from('scoring_weight_overrides')
    .select('effective_weight')
    .eq('signal_name', 'approval_pin')
    .single();

  if (!pinConfig || String(pinConfig.effective_weight) !== String(pin)) {
    return NextResponse.json({ error: 'Invalid PIN' }, { status: 403 });
  }

  // Check approval expiry
  const { data: expiryConfig } = await sb
    .from('scoring_weight_overrides')
    .select('effective_weight')
    .eq('signal_name', 'approval_expiry_minutes')
    .single();

  const expiryMinutes = expiryConfig?.effective_weight || 120;

  // Get the pick
  const { data: pick, error: pickError } = await sb
    .from('claude_daily_picks')
    .select('id, pick_date, approval_status, created_at')
    .eq('id', pickId)
    .single();

  if (pickError || !pick) {
    return NextResponse.json({ error: 'Pick not found' }, { status: 404 });
  }

  if (pick.approval_status !== 'pending') {
    return NextResponse.json({ error: `Pick already ${pick.approval_status}` }, { status: 409 });
  }

  // Check if expired (created_at + expiry minutes < now)
  const createdAt = new Date(pick.created_at);
  const expiresAt = new Date(createdAt.getTime() + expiryMinutes * 60 * 1000);
  if (new Date() > expiresAt) {
    // Mark as expired
    await sb
      .from('claude_daily_picks')
      .update({ approval_status: 'expired', execution_notes: 'Approval window expired' })
      .eq('id', pickId);
    return NextResponse.json({ error: 'Pick expired — approval window closed' }, { status: 410 });
  }

  // Approve the pick
  const { error: updateError } = await sb
    .from('claude_daily_picks')
    .update({
      approval_status: 'approved',
      approved_at: new Date().toISOString(),
    })
    .eq('id', pickId);

  if (updateError) {
    return NextResponse.json({ error: updateError.message }, { status: 500 });
  }

  return NextResponse.json({ success: true, message: `${pick.id} approved — executor will place the order on the next cycle.` });
}
