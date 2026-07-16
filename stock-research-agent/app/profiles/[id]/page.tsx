'use client';

import AppShell from '@/components/AppShell';
import React, { useEffect, useState, useCallback } from 'react';
import { useParams, useRouter } from 'next/navigation';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

type ExperimentStatus = 'active' | 'draft' | 'testing' | 'completed' | 'archived';

interface Profile {
  id: string;
  profileName: string;
  description: string | null;
  role: 'champion' | 'challenger';
  isEnabled: boolean;
  learningEnabled: boolean;
  experimentStatus: ExperimentStatus;
  hypothesis: string | null;
  createdAt: string;
  updatedAt: string;
}

const STATUS_STYLES: Record<ExperimentStatus, { bg: string; text: string; label: string }> = {
  active: { bg: 'bg-green-900/50 border-green-700/50', text: 'text-green-300', label: 'Active' },
  draft: { bg: 'bg-zinc-800 border-zinc-700/50', text: 'text-zinc-400', label: 'Draft' },
  testing: { bg: 'bg-blue-900/50 border-blue-700/50', text: 'text-blue-300', label: 'Testing' },
  completed: { bg: 'bg-violet-900/50 border-violet-700/50', text: 'text-violet-300', label: 'Completed' },
  archived: { bg: 'bg-zinc-800/50 border-zinc-700/30', text: 'text-zinc-500', label: 'Archived' },
};

interface ProfileConfig {
  id: string;
  profileId: string;
  configKey: string;
  configValue: number;
  description: string | null;
  createdAt: string;
  updatedAt: string;
}

// ---------------------------------------------------------------------------
// Weight categories for display grouping
// ---------------------------------------------------------------------------

const WEIGHT_CATEGORIES: Record<string, string[]> = {
  'Signal Weights': [
    'trend_alignment', 'momentum_strength', 'volume_confirmation', 'volatility_regime',
    'support_resistance', 'rsi_signal', 'macd_signal', 'bollinger_signal',
    'moving_average_cross', 'vwap_signal', 'market_sentiment', 'sector_momentum',
    'earnings_proximity', 'options_flow', 'institutional_activity',
    'insider_trading', 'short_interest', 'catalyst_strength',
    'relative_strength', 'breadth_signal',
  ],
  'Scoring Thresholds': [
    'confidence_threshold', 'risk_threshold', 'actionability_threshold',
    'min_expected_value', 'max_position_risk',
  ],
  'Calibration': [
    'calibration_adjustment', 'overconfidence_penalty', 'underconfidence_boost',
    'prediction_decay_rate',
  ],
  'Research Weights': [
    'technical_weight', 'fundamental_weight', 'sentiment_weight',
    'catalyst_weight', 'risk_weight',
  ],
};

function categorize(key: string): string {
  for (const [cat, keys] of Object.entries(WEIGHT_CATEGORIES)) {
    if (keys.includes(key)) return cat;
  }
  return 'Other';
}

// ---------------------------------------------------------------------------
// API helpers
// ---------------------------------------------------------------------------

async function fetchProfile(id: string): Promise<{ profile: Profile; configs: ProfileConfig[] } | null> {
  const res = await fetch(`/api/profiles/${id}`, { cache: 'no-store' });
  if (!res.ok) return null;
  return res.json();
}

async function fetchChampionWeights(): Promise<Record<string, number>> {
  const res = await fetch('/api/profiles/champion-weights', { cache: 'no-store' });
  if (!res.ok) return {};
  return res.json();
}

async function updateProfile(id: string, data: { name?: string; description?: string; isEnabled?: boolean; learningEnabled?: boolean; hypothesis?: string }) {
  return fetch(`/api/profiles/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
}

async function updateConfig(id: string, weights: Record<string, number>) {
  return fetch(`/api/profiles/${id}/config`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(weights),
  });
}

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function ProfileDetailPage() {
  const params = useParams();
  const router = useRouter();
  const id = params.id as string;

  const [profile, setProfile] = useState<Profile | null>(null);
  const [configs, setConfigs] = useState<ProfileConfig[]>([]);
  const [championWeights, setChampionWeights] = useState<Record<string, number>>({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [toast, setToast] = useState<{ message: string; type: 'success' | 'error' } | null>(null);

  // edit profile metadata
  const [editing, setEditing] = useState(false);
  const [editName, setEditName] = useState('');
  const [editDesc, setEditDesc] = useState('');
  const [editHypothesis, setEditHypothesis] = useState('');
  const [editLearning, setEditLearning] = useState(false);

  // edit weights
  const [editingWeights, setEditingWeights] = useState(false);
  const [draftWeights, setDraftWeights] = useState<Record<string, number>>({});
  const [actionLoading, setActionLoading] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    const [data, cw] = await Promise.all([fetchProfile(id), fetchChampionWeights()]);
    if (data) {
      setProfile(data.profile);
      setConfigs(data.configs);
      setChampionWeights(cw);
      setError(null);
    } else {
      setError('Profile not found');
    }
    setLoading(false);
  }, [id]);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), 3000);
    return () => clearTimeout(t);
  }, [toast]);

  function startEdit() {
    if (!profile) return;
    setEditName(profile.profileName);
    setEditDesc(profile.description || '');
    setEditHypothesis(profile.hypothesis || '');
    setEditLearning(profile.learningEnabled);
    setEditing(true);
  }

  async function saveEdit() {
    if (!profile) return;
    setActionLoading(true);
    const res = await updateProfile(id, { name: editName.trim(), description: editDesc.trim(), learningEnabled: editLearning, hypothesis: editHypothesis.trim() });
    if (res.ok) {
      setToast({ message: 'Profile updated', type: 'success' });
      setEditing(false);
      load();
    } else {
      setToast({ message: 'Failed to update profile', type: 'error' });
    }
    setActionLoading(false);
  }

  // Compute effective weights: champion base + profile overrides
  function getEffectiveWeights(): Record<string, number> {
    const effective = { ...championWeights };
    for (const c of configs) {
      effective[c.configKey] = c.configValue;
    }
    return effective;
  }

  function startWeightEdit() {
    setDraftWeights(getEffectiveWeights());
    setEditingWeights(true);
  }

  async function saveWeights() {
    if (!profile) return;
    setActionLoading(true);
    const res = await updateConfig(id, draftWeights);
    if (res.ok) {
      setToast({ message: 'Configuration saved', type: 'success' });
      setEditingWeights(false);
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Failed to save configuration', type: 'error' });
    }
    setActionLoading(false);
  }

  // Group weights by category
  function groupedWeights(weights: Record<string, number>): Record<string, [string, number][]> {
    const groups: Record<string, [string, number][]> = {};
    for (const [key, val] of Object.entries(weights).sort((a, b) => a[0].localeCompare(b[0]))) {
      const cat = categorize(key);
      if (!groups[cat]) groups[cat] = [];
      groups[cat].push([key, val]);
    }
    return groups;
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  if (loading) return <AppShell><div className="text-zinc-400 text-center py-12">Loading...</div></AppShell>;
  if (error || !profile) return <AppShell><div className="text-red-400 text-center py-12">{error || 'Not found'}</div></AppShell>;

  const isChampion = profile.role === 'champion';
  const effectiveWeights = getEffectiveWeights();
  const grouped = groupedWeights(effectiveWeights);

  return (
    <AppShell>
      <div className="max-w-4xl mx-auto space-y-6">
        {/* Back + Header */}
        <button onClick={() => router.push('/profiles')} className="text-sm text-zinc-400 hover:text-zinc-200 transition-colors">&larr; Back to Profiles</button>

        {/* Toast */}
        {toast && (
          <div className={`px-4 py-3 rounded-lg text-sm ${toast.type === 'success' ? 'bg-green-900/50 text-green-300 border border-green-700' : 'bg-red-900/50 text-red-300 border border-red-700'}`}>
            {toast.message}
          </div>
        )}

        {/* Profile Header Card */}
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-6">
          {!editing ? (
            <div className="flex items-start justify-between">
              <div>
                <div className="flex items-center gap-3">
                  <h1 className="text-xl font-bold text-zinc-100">{profile.profileName}</h1>
                  <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${isChampion ? 'bg-amber-900/50 text-amber-300 border border-amber-700/50' : 'bg-zinc-800 text-zinc-300 border border-zinc-700/50'}`}>
                    {profile.role}
                  </span>
                  {(() => { const st = STATUS_STYLES[profile.experimentStatus] || STATUS_STYLES.draft; return (
                    <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${st.bg} ${st.text}`}>{st.label}</span>
                  ); })()}
                </div>
                {profile.hypothesis && (
                  <div className="mt-2 px-3 py-2 bg-zinc-800/50 border border-zinc-700/30 rounded-lg">
                    <span className="text-xs text-zinc-500">Hypothesis: </span>
                    <span className="text-sm text-zinc-300 italic">{profile.hypothesis}</span>
                  </div>
                )}
                {profile.description && <p className="text-sm text-zinc-400 mt-2">{profile.description}</p>}
                <div className="flex gap-4 mt-3 text-xs text-zinc-500">
                  <span>Learning: <span className={profile.learningEnabled ? 'text-blue-400' : 'text-zinc-400'}>{profile.learningEnabled ? 'Adaptive' : 'Static'}</span></span>
                  <span>Created: {new Date(profile.createdAt).toLocaleDateString()}</span>
                  <span>Updated: {new Date(profile.updatedAt).toLocaleDateString()}</span>
                </div>
              </div>
              {!isChampion && (
                <button onClick={startEdit} className="px-3 py-1.5 text-sm text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800 rounded-lg transition-colors">
                  Edit
                </button>
              )}
            </div>
          ) : (
            <div className="space-y-4">
              <h2 className="text-lg font-semibold text-zinc-100">Edit Profile</h2>
              <div>
                <label className="block text-sm text-zinc-400 mb-1">Name</label>
                <input value={editName} onChange={e => setEditName(e.target.value)} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500" />
              </div>
              <div>
                <label className="block text-sm text-zinc-400 mb-1">Description</label>
                <textarea value={editDesc} onChange={e => setEditDesc(e.target.value)} rows={2} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500 resize-none" />
              </div>
              <div>
                <label className="block text-sm text-zinc-400 mb-1">Hypothesis</label>
                <textarea value={editHypothesis} onChange={e => setEditHypothesis(e.target.value)} rows={2} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500 resize-none" placeholder="What are you testing with this configuration?" />
              </div>
              <div className="flex items-center gap-2">
                <input type="checkbox" id="edit-learning" checked={editLearning} onChange={e => setEditLearning(e.target.checked)} className="rounded border-zinc-600 bg-zinc-800 text-violet-500" />
                <label htmlFor="edit-learning" className="text-sm text-zinc-300">Enable adaptive learning</label>
              </div>
              <div className="flex justify-end gap-2">
                <button onClick={() => setEditing(false)} className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors">Cancel</button>
                <button onClick={saveEdit} disabled={actionLoading} className="px-4 py-2 bg-violet-600 hover:bg-violet-500 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                  {actionLoading ? 'Saving...' : 'Save'}
                </button>
              </div>
            </div>
          )}
        </div>

        {/* Configuration Section */}
        <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-6">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-lg font-semibold text-zinc-100">Configuration</h2>
            {!isChampion && !editingWeights && (
              <button onClick={startWeightEdit} className="px-3 py-1.5 text-sm text-violet-400 hover:text-violet-300 hover:bg-violet-900/30 rounded-lg transition-colors">
                Edit Weights
              </button>
            )}
            {isChampion && (
              <span className="text-xs text-zinc-500">Champion weights are managed by the learning engine</span>
            )}
          </div>

          {editingWeights ? (
            <div className="space-y-6">
              {Object.entries(groupedWeights(draftWeights)).map(([category, entries]) => (
                <div key={category}>
                  <h3 className="text-sm font-medium text-zinc-300 mb-2 border-b border-zinc-800 pb-1">{category}</h3>
                  <div className="grid grid-cols-1 md:grid-cols-2 gap-2">
                    {entries.map(([key, val]) => {
                      const champVal = championWeights[key];
                      const isOverridden = configs.some(c => c.configKey === key);
                      return (
                        <div key={key} className="flex items-center gap-2 px-3 py-1.5 rounded bg-zinc-800/50">
                          <span className="text-xs text-zinc-400 flex-1 truncate" title={key}>{key.replace(/_/g, ' ')}</span>
                          <input
                            type="number"
                            step="0.01"
                            value={val}
                            onChange={e => setDraftWeights(prev => ({ ...prev, [key]: parseFloat(e.target.value) || 0 }))}
                            className="w-20 px-2 py-1 bg-zinc-700 border border-zinc-600 rounded text-xs text-zinc-100 text-right focus:outline-none focus:border-violet-500"
                          />
                          {champVal !== undefined && champVal !== val && (
                            <span className="text-xs text-amber-400" title={`Champion: ${champVal}`}>*</span>
                          )}
                          {isOverridden && champVal !== undefined && (
                            <button
                              onClick={() => setDraftWeights(prev => ({ ...prev, [key]: champVal }))}
                              className="text-xs text-zinc-500 hover:text-zinc-300"
                              title="Reset to champion value"
                            >
                              reset
                            </button>
                          )}
                        </div>
                      );
                    })}
                  </div>
                </div>
              ))}
              <div className="flex justify-end gap-2 pt-2 border-t border-zinc-800">
                <button onClick={() => setEditingWeights(false)} className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors">Cancel</button>
                <button onClick={saveWeights} disabled={actionLoading} className="px-4 py-2 bg-violet-600 hover:bg-violet-500 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                  {actionLoading ? 'Saving...' : 'Save Configuration'}
                </button>
              </div>
            </div>
          ) : (
            <div className="space-y-6">
              {Object.keys(grouped).length === 0 && (
                <p className="text-sm text-zinc-500">No weights configured. {isChampion ? 'Weights will appear after the learning engine runs.' : 'Click "Edit Weights" to configure.'}</p>
              )}
              {Object.entries(grouped).map(([category, entries]) => (
                <div key={category}>
                  <h3 className="text-sm font-medium text-zinc-300 mb-2 border-b border-zinc-800 pb-1">{category}</h3>
                  <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-1">
                    {entries.map(([key, val]) => {
                      const isOverridden = !isChampion && configs.some(c => c.configKey === key);
                      return (
                        <div key={key} className="flex items-center justify-between px-3 py-1.5 rounded hover:bg-zinc-800/30">
                          <span className="text-xs text-zinc-400 truncate" title={key}>{key.replace(/_/g, ' ')}</span>
                          <span className={`text-xs tabular-nums ml-2 ${isOverridden ? 'text-violet-400 font-medium' : 'text-zinc-300'}`}>
                            {val.toFixed(2)}
                            {isOverridden && <span className="ml-1 text-violet-500">*</span>}
                          </span>
                        </div>
                      );
                    })}
                  </div>
                </div>
              ))}
              {!isChampion && configs.length > 0 && (
                <p className="text-xs text-zinc-500 mt-2">* Overridden from champion base values</p>
              )}
            </div>
          )}
        </div>
      </div>
    </AppShell>
  );
}
