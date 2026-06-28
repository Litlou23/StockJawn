'use client';

import { useState } from 'react';
import { submitFeedbackAction } from '@/app/actions/learningActions';
import { FeedbackRating } from '@/types/learning';

const RATING_OPTIONS: { rating: FeedbackRating; label: string }[] = [
  { rating: 'useful', label: '👍 Useful' },
  { rating: 'not_useful', label: '👎 Not useful' },
  { rating: 'too_confident', label: '⚠️ Too confident' },
  { rating: 'missed_risk', label: '🚩 Missed a risk' },
  { rating: 'good_risk_call', label: '✅ Good risk call' },
  { rating: 'wrong', label: '❌ Wrong' },
  { rating: 'unclear', label: '❓ Unclear' },
];

/**
 * Small feedback strip under an agent reply. Without a saved chat_messages
 * row (Supabase not configured yet) there's nothing to attach feedback to,
 * so this renders a disabled note instead of silently doing nothing.
 */
export default function FeedbackButtons({ chatMessageId }: { chatMessageId?: string }) {
  const [submitted, setSubmitted] = useState<FeedbackRating | null>(null);
  const [pending, setPending] = useState(false);

  if (!chatMessageId) {
    return <p className="text-[10px] text-zinc-600">Feedback unavailable — this reply wasn&apos;t saved (Supabase not connected).</p>;
  }

  if (submitted) {
    return <p className="text-[10px] text-zinc-500">Feedback saved: {submitted.replace(/_/g, ' ')}. Thanks.</p>;
  }

  const handleClick = async (rating: FeedbackRating) => {
    setPending(true);
    try {
      await submitFeedbackAction({ chatMessageId, rating });
      setSubmitted(rating);
    } finally {
      setPending(false);
    }
  };

  return (
    <div className="flex flex-wrap gap-1.5">
      {RATING_OPTIONS.map((opt) => (
        <button
          key={opt.rating}
          type="button"
          disabled={pending}
          onClick={() => handleClick(opt.rating)}
          className="rounded-full border border-zinc-800 px-2 py-0.5 text-[10px] text-zinc-400 transition hover:border-zinc-600 hover:text-zinc-200 disabled:opacity-50"
        >
          {opt.label}
        </button>
      ))}
    </div>
  );
}
