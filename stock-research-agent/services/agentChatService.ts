import { AgentAction, AgentResponse, ChatMessage, OptionsSignal, Pick } from '@/types/stockAgent';
import { AgentCard, AgentChatApiResponse } from '@/types/agentChat';
import {
  buildComparisonContext,
  buildHistoryContext,
  buildOptionsContext,
  buildRiskContext,
  buildSectorContext,
  buildSignalPerformanceContext,
  buildTickerContext,
  buildTodayMarketContext,
  buildWatchlistContext,
  extractMentionedTickers,
  TickerContext,
} from './contextBuilder';

const DEFAULT_SUGGESTED_PROMPTS = [
  'What stocks look interesting today?',
  'What options setups look interesting?',
  'What should I avoid today?',
  'Which signals have been working lately?',
];

function formatPercent(value?: number): string {
  if (value === undefined) return 'pending';
  const sign = value > 0 ? '+' : '';
  return `${sign}${value.toFixed(1)}%`;
}

function describePick(pick: Pick): string {
  const signals = pick.supportingSignals.map((s) => s.name.replace(/_/g, ' ')).join(', ');
  return (
    `${pick.ticker} (${pick.companyName}) scored ${pick.score} (${pick.convictionLevel === 'higher_conviction' ? 'higher conviction' : 'watchlist-only'}), ` +
    `${pick.riskLevel} risk. Main reason: ${pick.mainReason} Supporting signals: ${signals}.`
  );
}

function describeOptionsSignal(opt: OptionsSignal): string {
  return (
    `${opt.ticker} ${opt.strike}${opt.contractType === 'call' ? 'C' : 'P'} exp ${opt.expiration} (${opt.daysToExpiration}d): ` +
    `IV ${Math.round(opt.impliedVolatility * 100)}% (rank ${opt.ivRank}), OI ${opt.openInterest.toLocaleString()}, ` +
    `spread ${opt.bidAskSpreadPercent.toFixed(1)}%, liquidity score ${opt.liquidityScore}/100, ${opt.optionsRiskLevel} options risk.`
  );
}

function tickerSuggestedPrompts(ticker: string): string[] {
  return [
    `What is the risk with ${ticker}?`,
    `Is IV too high on ${ticker} options?`,
    `What would prove ${ticker} wrong?`,
  ];
}

function confidenceFooter(level: 'low' | 'medium' | 'high', reason: string): string {
  return `Data confidence: ${level} — ${reason}`;
}

async function handleCompare(tickers: string[]): Promise<AgentResponse> {
  const [tickerA, tickerB] = tickers;
  const { a, b } = await buildComparisonContext(tickerA, tickerB);

  if (!a.pick || !b.pick) {
    return {
      text: `I couldn't find both tickers to compare. I have data on ${tickerA} ${a.pick ? '' : '(missing)'} and ${tickerB} ${b.pick ? '' : '(missing)'}.`,
      action: 'compare_tickers',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const optionsLine = (ctx: TickerContext) =>
    ctx.optionsSignals.length > 0
      ? `options: ${ctx.optionsSignals[0].optionsRiskLevel} risk, liquidity ${ctx.optionsSignals[0].liquidityScore}/100`
      : 'no options data tracked';

  const text =
    `${a.pick.ticker} scored ${a.pick.score} (${a.pick.riskLevel} risk) vs ${b.pick.ticker} scored ${b.pick.score} (${b.pick.riskLevel} risk).\n\n` +
    `${a.pick.ticker}: ${a.pick.mainReason} ${optionsLine(a)}.\n` +
    `${b.pick.ticker}: ${b.pick.mainReason} ${optionsLine(b)}.\n\n` +
    `Evidence against: ${a.pick.ticker} — ${a.pick.bearishCounterpoint}\n` +
    `${b.pick.ticker} — ${b.pick.bearishCounterpoint}\n\n` +
    `${a.pick.convictionLevel === 'higher_conviction' ? a.pick.ticker : b.pick.convictionLevel === 'higher_conviction' ? b.pick.ticker : 'Neither'} currently reads as the higher-conviction idea of the two — that is not the same as a recommendation to act on either.\n\n` +
    `${confidenceFooter(a.pick.scoreBreakdown.confidenceLevel, `based on ${a.pick.ticker}'s mock signal data`)} / ${confidenceFooter(b.pick.scoreBreakdown.confidenceLevel, `based on ${b.pick.ticker}'s mock signal data`)}`;

  return {
    text,
    action: 'compare_tickers',
    pickIds: [a.pick.id, b.pick.id],
    optionsSignalIds: [...a.optionsSignals, ...b.optionsSignals].map((o) => o.id),
    suggestedPrompts: [`What is the risk with ${a.pick.ticker}?`, 'What stocks look interesting today?'],
  };
}

async function handleExplainTicker(ticker: string): Promise<AgentResponse> {
  const ctx = await buildTickerContext(ticker);
  if (!ctx.pick) {
    return {
      text: `I don't have any recent picks for ${ticker.toUpperCase()}. Try asking about a ticker from today's watchlist.`,
      action: 'fallback',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const { pick, sectorContext, news } = ctx;
  const sectorLine = sectorContext ? ` Sector backdrop (${sectorContext.sector}) is ${sectorContext.trend}.` : '';
  const newsLine = news.length > 0 ? ` Recent news: ${news[0].headline} (${news[0].sentiment}).` : '';

  return {
    text:
      `Bottom line: ${describePick(pick)}${sectorLine}${newsLine}\n\n` +
      `Evidence against / risks: ${pick.bearishCounterpoint}\n` +
      `Missing confirmation: ${pick.invalidationPoint}\n` +
      `${confidenceFooter(pick.scoreBreakdown.confidenceLevel, 'based on mock signal data, no live price/volume confirmation')}\n` +
      `Suggested next step: ${pick.suggestedResearchAction}`,
    action: 'explain_ticker',
    pickIds: [pick.id],
    optionsSignalIds: ctx.optionsSignals.map((o) => o.id),
    suggestedPrompts: tickerSuggestedPrompts(pick.ticker),
  };
}

async function handleOptions(ticker?: string): Promise<AgentResponse> {
  if (ticker) {
    const { pick, optionsSignals } = await buildOptionsContext(ticker);
    if (optionsSignals.length === 0) {
      return {
        text: `I don't have any options signals tracked for ${ticker.toUpperCase()} right now.`,
        action: 'fallback',
        suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
      };
    }
    const lines = optionsSignals.map(describeOptionsSignal).join('\n');
    return {
      text:
        `${pick ? `${pick.ticker} options setup:` : 'Options setup:'}\n\n${lines}\n\n` +
        'This is mock options data — no live IV/liquidity confirmation. Treat as a watchlist candidate, not a confirmed setup, until checked against real chain data.\n' +
        confidenceFooter('low', 'mock options data only, no live confirmation'),
      action: 'show_options',
      pickIds: pick ? [pick.id] : undefined,
      optionsSignalIds: optionsSignals.map((o) => o.id),
      suggestedPrompts: tickerSuggestedPrompts(ticker.toUpperCase()),
    };
  }

  const { topPicks } = await buildTodayMarketContext();
  const optionsByPick = await Promise.all(topPicks.map((p) => buildOptionsContext(p.ticker)));
  const withOptions = optionsByPick.filter((o) => o.optionsSignals.length > 0);

  if (withOptions.length === 0) {
    return {
      text: "I don't have any options setups tracked today.",
      action: 'fallback',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const lines = withOptions.map((o) => describeOptionsSignal(o.optionsSignals[0])).join('\n');
  return {
    text:
      `Options setups worth reviewing today (not recommendations):\n\n${lines}\n\n` +
      'These are research labels only, built on mock options data — check IV, liquidity, and spread yourself before treating any of these as confirmed. If none of this looks clean, watchlist-only or no action is a valid call.\n' +
      confidenceFooter('low', 'mock options data only, no live confirmation'),
    action: 'show_options',
    pickIds: withOptions.map((o) => o.pick?.id).filter((id): id is string => Boolean(id)),
    optionsSignalIds: withOptions.flatMap((o) => o.optionsSignals.map((s) => s.id)),
    suggestedPrompts: ['Is IV too high on any of these?', 'What stocks look interesting today?'],
  };
}

async function handleRisk(ticker?: string): Promise<AgentResponse> {
  const { picks, riskRules } = await buildRiskContext(ticker);
  if (picks.length === 0) {
    return {
      text: "I don't have risk data for that yet. Ask about a ticker from today's watchlist.",
      action: 'fallback',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const lines = picks.map(
    (p) =>
      `${p.ticker} (${p.riskLevel} risk): ${p.bearishCounterpoint} Invalidation: ${p.invalidationPoint}`,
  );

  const highSeverityRules = riskRules.filter((r) => r.severity === 'high').map((r) => r.label);

  return {
    text:
      `Here's what could go wrong:\n\n${lines.join('\n')}\n\n` +
      `Risk filters worth checking before acting: ${highSeverityRules.join(', ')}.`,
    action: 'show_risk',
    pickIds: picks.map((p) => p.id),
    suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
  };
}

async function handleAvoidToday(): Promise<AgentResponse> {
  const { topPicks, marketContext } = await buildTodayMarketContext();
  const weakerPicks = topPicks.filter(
    (p) => p.riskLevel === 'high' || p.scoreBreakdown.confidenceLevel === 'low',
  );

  const marketLine =
    marketContext.marketBias === 'bearish'
      ? 'The broad market backdrop itself is bearish, which argues for caution on new longs.'
      : `Broad market backdrop is ${marketContext.marketBias}, so this is mostly stock-specific caution.`;

  if (weakerPicks.length === 0) {
    return {
      text: `Nothing on today's list stands out as a clear avoid. ${marketLine}`,
      action: 'show_risk',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const lines = weakerPicks.map((p) => `${p.ticker}: ${p.bearishCounterpoint}`);
  return {
    text: `I'd be most cautious on:\n\n${lines.join('\n')}\n\n${marketLine}`,
    action: 'show_risk',
    pickIds: weakerPicks.map((p) => p.id),
    suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
  };
}

async function handleVolumeOrHype(ticker: string): Promise<AgentResponse> {
  const ctx = await buildTickerContext(ticker);
  if (!ctx.pick) {
    return {
      text: `I don't have data on ${ticker.toUpperCase()} to check that.`,
      action: 'fallback',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }
  const volumeSignal = ctx.pick.supportingSignals.find((s) => s.name === 'volume_spike');
  const newsSignal = ctx.pick.supportingSignals.find((s) => s.name === 'news_sentiment');
  const unusualOptions = ctx.optionsSignals.find((o) => o.unusualActivityScore > 60);

  const parts: string[] = [];
  if (volumeSignal) parts.push(`Volume is running ${volumeSignal.value}x average — that's real confirmation.`);
  if (unusualOptions) parts.push(`Options volume is also elevated relative to open interest (unusual activity score ${unusualOptions.unusualActivityScore}/100).`);
  if (newsSignal && !volumeSignal) parts.push('News sentiment is positive, but I don\'t see strong volume confirming the move yet — that leans more toward hype than confirmed interest.');
  if (parts.length === 0) parts.push("I don't have enough volume or options-flow data on this one to say confidently either way.");

  return {
    text: parts.join(' '),
    action: 'explain_ticker',
    pickIds: [ctx.pick.id],
    suggestedPrompts: tickerSuggestedPrompts(ctx.pick.ticker),
  };
}

async function handleIvQuestion(ticker: string): Promise<AgentResponse> {
  const { pick, optionsSignals } = await buildOptionsContext(ticker);
  if (optionsSignals.length === 0) {
    return {
      text: `I don't have options data for ${ticker.toUpperCase()} to evaluate IV.`,
      action: 'fallback',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }
  const opt = optionsSignals[0];
  const verdict =
    opt.ivRank >= 60
      ? `IV rank is ${opt.ivRank}, which is elevated — you'd be paying a real volatility premium, and IV crush risk is worth considering${opt.earningsRisk ? ' especially with earnings in the window' : ''}.`
      : opt.ivRank >= 35
        ? `IV rank is ${opt.ivRank}, which is moderate — not cheap, not extreme.`
        : `IV rank is ${opt.ivRank}, which is fairly low relative to its own history.`;

  return {
    text: `${describeOptionsSignal(opt)}\n\n${verdict}\n\n${confidenceFooter('low', 'mock options data only, no live confirmation')}`,
    action: 'show_options',
    pickIds: pick ? [pick.id] : undefined,
    optionsSignalIds: [opt.id],
    suggestedPrompts: tickerSuggestedPrompts(ticker.toUpperCase()),
  };
}

async function handleLiquidContracts(ticker?: string): Promise<AgentResponse> {
  const { topPicks } = await buildTodayMarketContext();
  const candidates = ticker
    ? [await buildOptionsContext(ticker)]
    : await Promise.all(topPicks.map((p) => buildOptionsContext(p.ticker)));

  const liquid = candidates.flatMap((c) => c.optionsSignals.filter((o) => o.liquidityScore >= 70));

  if (liquid.length === 0) {
    return {
      text: "None of the options I'm tracking right now clear a liquidity score of 70+. Tighter spreads and higher open interest would be needed before I'd call these comfortably liquid.",
      action: 'show_options',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const lines = liquid.map((o) => `${o.ticker} ${o.strike}${o.contractType === 'call' ? 'C' : 'P'}: liquidity ${o.liquidityScore}/100, spread ${o.bidAskSpreadPercent.toFixed(1)}%.`);
  return {
    text: `These look the most liquid right now:\n\n${lines.join('\n')}`,
    action: 'show_options',
    optionsSignalIds: liquid.map((o) => o.id),
    suggestedPrompts: ['Is IV too high on any of these?', 'What are the biggest risks today?'],
  };
}

async function handleNewsToday(ticker?: string): Promise<AgentResponse> {
  if (ticker) {
    const ctx = await buildTickerContext(ticker);
    if (ctx.news.length === 0) {
      return {
        text: `I don't have any news tracked for ${ticker.toUpperCase()} right now. I can't confirm a catalyst either way — treat this as missing data, not a sign nothing is happening.`,
        action: 'fallback',
        suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
      };
    }
    const lines = ctx.news.map((n) => `${n.headline} (${n.sentiment}, ${n.source})${n.alreadyPricedIn ? ' — looks already priced in' : ''}`);
    return {
      text: `News on ${ticker.toUpperCase()}:\n\n${lines.join('\n')}\n\nThis is headline-level context only — it doesn't confirm price/volume or options activity by itself.`,
      action: 'explain_ticker',
      pickIds: ctx.pick ? [ctx.pick.id] : undefined,
      suggestedPrompts: tickerSuggestedPrompts(ticker.toUpperCase()),
    };
  }

  const { topPicks } = await buildTodayMarketContext();
  const allNews = await Promise.all(topPicks.map((p) => buildTickerContext(p.ticker)));
  const items = allNews.flatMap((ctx) => ctx.news);

  if (items.length === 0) {
    return {
      text: "I don't have specific news items tracked for today's picks — ask about a ticker from the watchlist for more detail.",
      action: 'fallback',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }

  const lines = items.map((n) => `${n.headline} (${n.sentiment}, ${n.source})`);
  return {
    text: `What's moving things today:\n\n${lines.join('\n')}`,
    action: 'fallback',
    pickIds: topPicks.map((p) => p.id),
    suggestedPrompts: ['What stocks look interesting today?', 'What should I avoid today?'],
  };
}

function summarizeResults(results: { pickId: string; return5d?: number; return20d?: number; return60d?: number; spyReturn5d?: number; qqqReturn5d?: number }[]): string {
  const withReturns = results.filter((r) => r.return5d !== undefined);
  if (withReturns.length === 0) return 'No closed-out results yet — all current picks are still being tracked.';
  const lines = withReturns.map(
    (r) =>
      `${r.pickId}: 5d ${formatPercent(r.return5d)} (SPY ${formatPercent(r.spyReturn5d)}, QQQ ${formatPercent(r.qqqReturn5d)}), ` +
      `20d ${formatPercent(r.return20d)}, 60d ${formatPercent(r.return60d)}`,
  );
  return lines.join('\n');
}

async function handleHistory(): Promise<AgentResponse> {
  const { picks, results } = await buildHistoryContext();
  const text = `Here's how recent picks have tracked:\n\n${summarizeResults(results)}`;
  return {
    text,
    action: 'show_history',
    pickIds: picks.map((p) => p.id),
    suggestedPrompts: ['Which signals have been working lately?', 'What stocks look interesting today?'],
  };
}

async function handleSignalPerformance(): Promise<AgentResponse> {
  const perf = await buildSignalPerformanceContext();
  if (perf.sampleSize === 0) {
    return {
      text: "I don't have enough closed-out picks yet to judge which signals are working. Check back once more picks have tracked through.",
      action: 'show_signal_performance',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }
  return {
    text:
      `Based on a small sample of ${perf.sampleSize} closed-out picks (not enough to be statistically confident yet): ` +
      `hit rate ${Math.round(perf.hitRate * 100)}%, average 5-day return ${formatPercent(perf.averageReturn5d)}. ` +
      `${perf.bestSignal ? `${perf.bestSignal.replace(/_/g, ' ')} has looked strongest so far.` : ''} ` +
      `${perf.worstSignal ? `${perf.worstSignal.replace(/_/g, ' ')} has been closer to noise.` : ''} ` +
      `Take this with caution — sample size is small and this isn't auto-adjusting weights yet.`,
    action: 'show_signal_performance',
    suggestedPrompts: ['Which picks from last week worked?', 'What stocks look interesting today?'],
  };
}

async function handleSectorContext(): Promise<AgentResponse> {
  const { sectors } = await buildSectorContext();
  if (sectors.length === 0) {
    return {
      text: "I don't have sector backdrop data connected yet.",
      action: 'show_signal_performance',
      suggestedPrompts: ['What stocks look interesting today?', 'What should I avoid today?'],
    };
  }
  const lines = sectors.map((s) => `${s.sector} (${s.etfTicker}): ${s.trend} — ${s.notes}`);
  return {
    text: `Sector backdrop:\n\n${lines.join('\n')}`,
    action: 'show_signal_performance',
    suggestedPrompts: ['What stocks look interesting today?', 'What should I avoid today?'],
  };
}

async function handleWatchlist(): Promise<AgentResponse> {
  const { highConvictionPicks, watchlistOnlyPicks } = await buildWatchlistContext();
  if (highConvictionPicks.length === 0 && watchlistOnlyPicks.length === 0) {
    return {
      text: "I don't have any picks tracked yet — nothing has been saved or generated.",
      action: 'show_watchlist',
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }
  if (highConvictionPicks.length === 0) {
    return {
      text: 'Nothing has crossed into higher-conviction territory today — everything is watchlist-only for now.',
      action: 'show_watchlist',
      pickIds: watchlistOnlyPicks.map((p) => p.id),
      suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
    };
  }
  const lines = highConvictionPicks.map((p) => `${p.ticker} (${p.score}): ${p.mainReason}`);
  return {
    text: `Highest-ranked ideas to review today (not a recommendation to act):\n\n${lines.join('\n')}\n\nEach still has its own bearish case and invalidation point — ask about a specific ticker for that.`,
    action: 'show_watchlist',
    pickIds: highConvictionPicks.map((p) => p.id),
    suggestedPrompts: ['What is the risk with the top pick?', 'What stocks look interesting today?'],
  };
}

async function handleTodayPicks(): Promise<AgentResponse> {
  const { report, topPicks, marketContext } = await buildTodayMarketContext();
  const intro = report ? report.summary : "Here's today's watchlist.";
  const regimeLine = `Market backdrop: ${marketContext.marketBias}, volatility ${marketContext.volatilityRegime}.`;

  return {
    text:
      topPicks.length > 0
        ? `${intro} ${regimeLine}\n\nWatchlist candidates: ${topPicks.map((p) => `${p.ticker} (${p.score})`).join(', ')}. These are watchlist ideas, not high-confidence calls — ask about a specific ticker for the bearish case and confidence level.`
        : `${intro} ${regimeLine}\n\nNo picks cleared the bar today. That's a valid outcome — no high-confidence setups found is better than forcing one.`,
    action: 'show_today_picks',
    pickIds: topPicks.map((p) => p.id),
    suggestedPrompts: [
      `Why is ${topPicks[0]?.ticker ?? 'the top pick'} on the list?`,
      'What options setups look interesting?',
      'What should I avoid today?',
    ],
  };
}

function fallbackResponse(): AgentResponse {
  return {
    text:
      "I'm not sure how to answer that yet. I can talk about today's watchlist, options setups, a specific ticker, " +
      'risk and invalidation points, comparisons, or how past picks have tracked.',
    action: 'fallback',
    suggestedPrompts: DEFAULT_SUGGESTED_PROMPTS,
  };
}

async function sendAgentMessageMock(message: string): Promise<AgentResponse> {
  const lower = message.toLowerCase();
  const mentionedTickers = await extractMentionedTickers(message);

  if (lower.includes('compare') && mentionedTickers.length >= 2) {
    return handleCompare(mentionedTickers.slice(0, 2));
  }

  if ((lower.includes('iv') || lower.includes('implied volatility')) && mentionedTickers.length >= 1) {
    return handleIvQuestion(mentionedTickers[0]);
  }

  if ((lower.includes('volume') || lower.includes('hype')) && mentionedTickers.length >= 1) {
    return handleVolumeOrHype(mentionedTickers[0]);
  }

  if (lower.includes('liquid')) {
    return handleLiquidContracts(mentionedTickers[0]);
  }

  if (lower.includes('option')) {
    return handleOptions(mentionedTickers[0]);
  }

  if (lower.includes('news') || lower.includes('catalyst') || lower.includes('moving') || lower.includes('hype')) {
    return handleNewsToday(mentionedTickers[0]);
  }

  if (lower.includes('avoid')) {
    return handleAvoidToday();
  }

  if (lower.includes('highest-confidence') || lower.includes('highest confidence') || lower.includes('high conviction')) {
    return handleWatchlist();
  }

  if (lower.includes('signal') && (lower.includes('working') || lower.includes('perform'))) {
    return handleSignalPerformance();
  }

  if (lower.includes('sector')) {
    return handleSectorContext();
  }

  if (mentionedTickers.length === 1 && !lower.includes('risk') && !lower.includes('wrong')) {
    return handleExplainTicker(mentionedTickers[0]);
  }

  if (lower.includes('risk') || lower.includes('wrong') || lower.includes('bearish') || lower.includes('invalidat')) {
    return handleRisk(mentionedTickers[0]);
  }

  if (lower.includes('history') || lower.includes('result') || lower.includes('track') || lower.includes('last week')) {
    return handleHistory();
  }

  if (
    lower.includes('interesting') ||
    lower.includes('today') ||
    lower.includes('pick') ||
    lower.includes('watchlist') ||
    lower.includes('opportunit')
  ) {
    return handleTodayPicks();
  }

  return fallbackResponse();
}

function inferActionFromCards(api: AgentChatApiResponse): AgentAction {
  if (api.riskWarnings.length > 0) return 'show_risk';
  if (api.cards.some((c) => c.type === 'option')) return 'show_options';
  if (api.cards.length > 0 && api.cards.every((c) => c.type === 'catalyst')) return 'show_catalysts';
  if (api.cards.length > 0) return 'show_today_picks';
  return 'fallback';
}

function mapApiResponseToAgentResponse(api: AgentChatApiResponse): AgentResponse {
  const pickIds = api.cards.filter((c: AgentCard) => c.type === 'pick').map((c) => c.id);
  const picks = api.cards.filter((c: AgentCard) => c.type === 'pick' && c.pick).map((c) => c.pick!);
  const optionsSignalIds = api.cards.filter((c: AgentCard) => c.type === 'option').map((c) => c.id);
  const catalysts = api.cards
    .filter((c: AgentCard) => c.type === 'catalyst' && c.catalyst)
    .map((c) => c.catalyst!);

  const mentionsConfidence = /data confidence/i.test(api.message);
  const withConfidence = mentionsConfidence
    ? api.message
    : `${api.message}\n\nData confidence: ${api.dataConfidence}`;

  const text =
    api.riskWarnings.length > 0
      ? `${withConfidence}\n\nRisk warnings:\n${api.riskWarnings.map((w) => `- ${w}`).join('\n')}`
      : withConfidence;

  return {
    text,
    action: inferActionFromCards(api),
    pickIds: pickIds.length > 0 ? pickIds : undefined,
    picks: picks.length > 0 ? picks : undefined,
    optionsSignalIds: optionsSignalIds.length > 0 ? optionsSignalIds : undefined,
    catalysts: catalysts.length > 0 ? catalysts : undefined,
    suggestedPrompts: api.suggestedPrompts,
    chatMessageId: api.chatMessageId,
    diagnostics: api.diagnostics,
  };
}

/**
 * Sends the user's message to the server-side /api/agent-chat route, which
 * gathers context and calls the real AI provider. If that call fails for
 * any reason (network issue, AI provider down, missing config), falls back
 * to the deterministic rule-based mock responder so the chat UI never
 * breaks.
 */
export async function sendAgentMessage(message: string, history: ChatMessage[] = []): Promise<AgentResponse> {
  try {
    const response = await fetch('/api/agent-chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        message,
        history: history.slice(-8).map((h) => ({ role: h.role, text: h.text })),
      }),
    });

    if (!response.ok) {
      throw new Error(`agent-chat request failed with status ${response.status}`);
    }

    const data = (await response.json()) as AgentChatApiResponse;
    return mapApiResponseToAgentResponse(data);
  } catch (err) {
    console.error('Falling back to mock agent response:', err);
    const mockResponse = await sendAgentMessageMock(message);
    mockResponse.diagnostics = {
      provider: 'client-fallback',
      usedFallback: true,
      dotnetApiAttempted: true,
      dotnetApiSucceeded: false,
      agentApiConfigured: false,
    };
    return mockResponse;
  }
}
