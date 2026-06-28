import { NextResponse } from 'next/server';
import { getLatestIntakeItems, getInformationProviderHealth } from '@/services/informationIntake/informationIntakeService';
import { saveCatalystItems } from '@/services/persistence/catalystRepository';
import { saveAgentSnapshot } from '@/services/persistence/reportsRepository';

export const runtime = 'nodejs';

/**
 * Manually-triggerable intake job. Not a real cron — call this yourself
 * (curl/Postman/browser) whenever you want fresh catalyst data pulled and
 * saved. Safe to call repeatedly: catalyst_items upserts on (source_id, url).
 */
export async function POST() {
  try {
    const [items, providerHealth] = await Promise.all([getLatestIntakeItems(50), getInformationProviderHealth()]);

    const persistence = await saveCatalystItems(items);
    await saveAgentSnapshot('hourly_intake', { itemCount: items.length, providerHealth });

    return NextResponse.json({
      success: true,
      itemsFetched: items.length,
      providerHealth,
      persistence,
    });
  } catch (err) {
    console.error('jobs/intake-catalysts failed', err);
    return NextResponse.json(
      { success: false, error: err instanceof Error ? err.message : 'Unknown error' },
      { status: 500 },
    );
  }
}
