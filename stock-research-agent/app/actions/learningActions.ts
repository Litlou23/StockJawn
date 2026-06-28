'use server';

/**
 * Server actions backing the learning foundation's mutating UI (feedback
 * buttons, manual outcome entry, manual thesis entry). Deliberately not new
 * API routes — these are Next.js server actions, called directly from
 * client components, so no new app/api/* surface is added beyond the
 * explicitly requested /api/jobs/analyze-learning.
 */

import { revalidatePath } from 'next/cache';
import { saveFeedback, saveOutcome, saveThesis } from '@/services/persistence/learningRepository';
import { FeedbackRating, ExpectedTimeframe } from '@/types/learning';
import { PersistenceResult } from '@/services/persistence/persistenceTypes';

export async function submitFeedbackAction(input: {
  chatMessageId?: string;
  rating: FeedbackRating;
  notes?: string;
}): Promise<PersistenceResult> {
  return saveFeedback({
    chatMessageId: input.chatMessageId,
    rating: input.rating,
    notes: input.notes,
  });
}

export async function submitOutcomeAction(formData: FormData): Promise<PersistenceResult> {
  const pickId = String(formData.get('pickId') ?? '').trim();
  const evaluationWindow = String(formData.get('evaluationWindow') ?? '5d') as ExpectedTimeframe;
  if (!pickId) return { persisted: false, reason: 'pickId is required.' };

  const num = (key: string): number | undefined => {
    const raw = formData.get(key);
    if (raw === null || raw === '') return undefined;
    const parsed = Number(raw);
    return Number.isFinite(parsed) ? parsed : undefined;
  };
  const bool = (key: string): boolean | undefined => {
    const raw = formData.get(key);
    if (raw === null || raw === '') return undefined;
    return raw === 'true';
  };

  const startPrice = num('startPrice');
  const endPrice = num('endPrice');
  const returnPercent =
    num('returnPercent') ?? (startPrice !== undefined && endPrice !== undefined && startPrice !== 0
      ? ((endPrice - startPrice) / startPrice) * 100
      : undefined);

  const result = await saveOutcome({
    pickId,
    ticker: String(formData.get('ticker') ?? '').trim() || undefined,
    evaluationWindow,
    startPrice,
    endPrice,
    returnPercent,
    spyReturnPercent: num('spyReturnPercent'),
    qqqReturnPercent: num('qqqReturnPercent'),
    thesisCorrect: bool('thesisCorrect'),
    catalystPlayedOut: bool('catalystPlayedOut'),
    optionsSetupWorked: bool('optionsSetupWorked'),
    maxFavorableMove: num('maxFavorableMove'),
    maxAdverseMove: num('maxAdverseMove'),
    notes: String(formData.get('notes') ?? '').trim() || undefined,
  });

  if (result.persisted) revalidatePath('/learning');
  return result;
}

export async function submitThesisAction(formData: FormData): Promise<PersistenceResult> {
  const ticker = String(formData.get('ticker') ?? '').trim();
  const thesisSummary = String(formData.get('thesisSummary') ?? '').trim();
  if (!ticker || !thesisSummary) {
    return { persisted: false, reason: 'ticker and thesisSummary are required.' };
  }

  const str = (key: string): string | undefined => String(formData.get(key) ?? '').trim() || undefined;

  const result = await saveThesis({
    ticker,
    thesisSummary,
    pickId: str('pickId'),
    setupType: str('setupType'),
    bullishCase: str('bullishCase'),
    bearishCase: str('bearishCase'),
    invalidationPoint: str('invalidationPoint'),
    expectedTimeframe: (str('expectedTimeframe') as ExpectedTimeframe | undefined) ?? undefined,
    confidenceAtCreation: str('confidenceAtCreation') as 'low' | 'medium' | 'high' | undefined,
    dataConfidenceAtCreation: str('dataConfidenceAtCreation') as 'low' | 'medium' | 'high' | undefined,
  });

  if (result.persisted) revalidatePath('/learning');
  return result;
}
