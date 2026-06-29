'use client';

import { useEffect, useState } from 'react';

interface FullScreenLoaderProps {
  /** Whether the loader is visible */
  loading: boolean;
  /** Primary message (e.g. "Running Morning Scan...") */
  message?: string;
  /** Secondary detail text (e.g. "Gathering market data for 8 tickers") */
  detail?: string;
  /** Optional progress 0-100. Shows a progress bar when provided. */
  progress?: number;
  /** Rotating status messages shown one at a time */
  steps?: string[];
}

export default function FullScreenLoader({
  loading,
  message = 'Loading...',
  detail,
  progress,
  steps,
}: FullScreenLoaderProps) {
  const [stepIndex, setStepIndex] = useState(0);
  const [visible, setVisible] = useState(false);
  const [fadeOut, setFadeOut] = useState(false);

  // Rotate through steps
  useEffect(() => {
    if (!steps || steps.length <= 1) return;
    const interval = setInterval(() => {
      setStepIndex((i) => (i + 1) % steps.length);
    }, 2500);
    return () => clearInterval(interval);
  }, [steps]);

  // Fade in/out
  useEffect(() => {
    if (loading) {
      setFadeOut(false);
      setVisible(true);
      setStepIndex(0);
    } else if (visible) {
      setFadeOut(true);
      const timer = setTimeout(() => setVisible(false), 300);
      return () => clearTimeout(timer);
    }
  }, [loading, visible]);

  if (!visible) return null;

  return (
    <div
      className={`fixed inset-0 z-50 flex items-center justify-center bg-zinc-950/90 backdrop-blur-sm transition-opacity duration-300 ${
        fadeOut ? 'opacity-0' : 'opacity-100'
      }`}
    >
      <div className="flex flex-col items-center gap-6 px-8 text-center">
        {/* Animated rings */}
        <div className="relative h-20 w-20">
          <div className="absolute inset-0 animate-spin rounded-full border-2 border-transparent border-t-violet-500" style={{ animationDuration: '1.2s' }} />
          <div className="absolute inset-2 animate-spin rounded-full border-2 border-transparent border-t-violet-400" style={{ animationDuration: '1.8s', animationDirection: 'reverse' }} />
          <div className="absolute inset-4 animate-spin rounded-full border-2 border-transparent border-t-violet-300" style={{ animationDuration: '2.4s' }} />
          <div className="absolute inset-0 flex items-center justify-center">
            <span className="text-2xl">📈</span>
          </div>
        </div>

        {/* Message */}
        <div>
          <p className="text-lg font-semibold text-zinc-100">{message}</p>
          {detail && <p className="mt-1 text-sm text-zinc-400">{detail}</p>}
        </div>

        {/* Progress bar */}
        {progress !== undefined && (
          <div className="w-64">
            <div className="h-1.5 rounded-full bg-zinc-800">
              <div
                className="h-full rounded-full bg-violet-500 transition-all duration-500"
                style={{ width: `${Math.min(100, Math.max(0, progress))}%` }}
              />
            </div>
            <p className="mt-1 text-xs text-zinc-500">{Math.round(progress)}%</p>
          </div>
        )}

        {/* Rotating steps */}
        {steps && steps.length > 0 && (
          <div className="h-5">
            <p className="animate-pulse text-sm text-zinc-400" key={stepIndex}>
              {steps[stepIndex]}
            </p>
          </div>
        )}
      </div>
    </div>
  );
}
