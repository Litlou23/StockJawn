import AppShell from '@/components/AppShell';
import MarketSummaryCard from '@/components/MarketSummaryCard';
import TopPicksPanel from '@/components/TopPicksPanel';
import ChatWindow from '@/components/chat/ChatWindow';
import { buildTodayMarketContext } from '@/services/contextBuilder';

export default async function ChatPage() {
  const { report, topPicks, marketContext } = await buildTodayMarketContext();

  return (
    <AppShell
      rightPanel={
        <>
          <TopPicksPanel picks={topPicks} />
          <MarketSummaryCard report={report} marketContext={marketContext} />
        </>
      }
    >
      {/*
        Deliberately not loading saved chat history here anymore — every
        page load/refresh starts a fresh conversation. Messages are still
        saved to Supabase (chat_messages) for feedback/learning purposes,
        just never re-loaded into the UI automatically.
      */}
      <ChatWindow />
    </AppShell>
  );
}
