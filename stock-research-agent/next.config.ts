import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  experimental: {
    staleTimes: {
      // Disable client-side router cache — navigating to a page always re-fetches
      dynamic: 0,
      static: 0,
    },
  },
};

export default nextConfig;
