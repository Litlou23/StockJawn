import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  experimental: {
    staleTimes: {
      // Disable client-side router cache — navigating to a page always re-fetches
      dynamic: 0,
      static: 0,
    },
  },
  async redirects() {
    // Old pages from pre-Robinhood system — all dead, redirect to home
    const deadRoutes = [
      '/broker', '/connectivity', '/portfolio', '/dashboard',
      '/stock-lab', '/congress-trades', '/demo', '/chat',
      '/options-lab', '/options-research', '/paper-options',
      '/results', '/pipeline-health', '/predictions', '/watchlist',
      '/settings', '/learning', '/profiles', '/meta-labeler', '/backtest',
    ];
    return deadRoutes.map(source => ({
      source,
      destination: '/',
      permanent: false,
    }));
  },
};

export default nextConfig;
