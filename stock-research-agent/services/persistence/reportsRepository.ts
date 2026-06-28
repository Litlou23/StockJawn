import { AgentReport } from '@/types/stockAgent';
import { DailyReport, NotificationRecord } from '@/services/agentPipeline/agentPipeline.types';
import { getSupabaseClient, isSupabaseConfigured } from '@/lib/supabase/serverClient';
import { NOT_CONFIGURED, PersistenceResult } from './persistenceTypes';

export async function saveAgentReport(report: AgentReport): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('agent_reports').insert({
      report_date: report.date,
      summary: report.summary,
      pick_ids: report.pickIds,
      raw_json: report,
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function saveDailyReport(report: DailyReport): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('daily_reports').upsert(
      {
        report_date: report.reportDate,
        generated_at: report.generatedAt,
        top_candidates: report.topCandidates,
        summary: report.summary,
        missing_data_warnings: report.missingDataWarnings,
        suggested_questions: report.suggestedQuestions,
      },
      { onConflict: 'report_date' },
    );
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function getLatestAgentReportFromDb(): Promise<AgentReport | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('agent_reports')
      .select('*')
      .order('report_date', { ascending: false })
      .limit(1)
      .maybeSingle();
    if (error || !data) return null;
    return {
      id: data.id,
      date: data.report_date,
      summary: data.summary,
      pickIds: data.pick_ids ?? [],
    };
  } catch {
    return null;
  }
}

export async function getLatestDailyReport(): Promise<DailyReport | null> {
  if (!isSupabaseConfigured()) return null;
  try {
    const client = getSupabaseClient();
    const { data, error } = await client
      .from('daily_reports')
      .select('*')
      .order('report_date', { ascending: false })
      .limit(1)
      .maybeSingle();
    if (error || !data) return null;
    return {
      reportDate: data.report_date,
      generatedAt: data.generated_at,
      topCandidates: data.top_candidates ?? [],
      summary: data.summary,
      missingDataWarnings: data.missing_data_warnings ?? [],
      suggestedQuestions: data.suggested_questions ?? [],
    };
  } catch {
    return null;
  }
}

export async function createNotification(notification: NotificationRecord): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('notifications').insert({
      type: notification.type,
      title: notification.title,
      body: notification.body,
      status: 'pending',
    });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}

export async function saveAgentSnapshot(
  snapshotType: 'hourly_intake' | 'scoring' | 'morning_report',
  payload: unknown,
): Promise<PersistenceResult> {
  if (!isSupabaseConfigured()) return NOT_CONFIGURED;
  try {
    const client = getSupabaseClient();
    const { error } = await client.from('agent_snapshots').insert({ snapshot_type: snapshotType, payload });
    if (error) return { persisted: false, reason: error.message };
    return { persisted: true, count: 1 };
  } catch (err) {
    return { persisted: false, reason: err instanceof Error ? err.message : 'unknown error' };
  }
}
