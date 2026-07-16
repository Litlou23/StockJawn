'use client';

import AppShell from '@/components/AppShell';
import React, { useEffect, useState, useCallback } from 'react';
import { useRouter } from 'next/navigation';

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

interface ProfileStats {
  profileId: string;
  totalPredictions: number;
  evaluatedPredictions: number;
}

// ---------------------------------------------------------------------------
// API helpers
// ---------------------------------------------------------------------------

async function fetchProfiles(): Promise<{ profiles: Profile[]; stats: ProfileStats[] } | null> {
  const res = await fetch('/api/profiles', { cache: 'no-store' });
  if (!res.ok) return null;
  return res.json();
}

async function toggleProfile(id: string, isEnabled: boolean) {
  return fetch(`/api/profiles/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ isEnabled }),
  });
}

async function deleteProfile(id: string) {
  return fetch(`/api/profiles/${id}`, { method: 'DELETE' });
}

async function createProfile(data: { name: string; description?: string; learningEnabled: boolean; hypothesis?: string }) {
  return fetch('/api/profiles', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
}

async function cloneProfile(id: string, name: string, description?: string, hypothesis?: string) {
  return fetch(`/api/profiles/${id}/clone`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, description, hypothesis }),
  });
}

async function promoteProfile(id: string) {
  return fetch(`/api/profiles/${id}/promote`, { method: 'POST' });
}

async function archiveProfile(id: string) {
  return fetch(`/api/profiles/${id}/archive`, { method: 'POST' });
}

async function startTestingProfile(id: string) {
  return fetch(`/api/profiles/${id}/start-testing`, { method: 'POST' });
}

async function completeExperiment(id: string) {
  return fetch(`/api/profiles/${id}/complete`, { method: 'POST' });
}

const STATUS_STYLES: Record<ExperimentStatus, { bg: string; text: string; label: string }> = {
  active: { bg: 'bg-green-900/50 border-green-700/50', text: 'text-green-300', label: 'Active' },
  draft: { bg: 'bg-zinc-800 border-zinc-700/50', text: 'text-zinc-400', label: 'Draft' },
  testing: { bg: 'bg-blue-900/50 border-blue-700/50', text: 'text-blue-300', label: 'Testing' },
  completed: { bg: 'bg-violet-900/50 border-violet-700/50', text: 'text-violet-300', label: 'Completed' },
  archived: { bg: 'bg-zinc-800/50 border-zinc-700/30', text: 'text-zinc-500', label: 'Archived' },
};

// ---------------------------------------------------------------------------
// Page
// ---------------------------------------------------------------------------

export default function ProfilesPage() {
  const router = useRouter();
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [stats, setStats] = useState<ProfileStats[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [toast, setToast] = useState<{ message: string; type: 'success' | 'error' } | null>(null);

  // modal state
  const [showCreate, setShowCreate] = useState(false);
  const [cloneSource, setCloneSource] = useState<Profile | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<Profile | null>(null);

  const [promoteTarget, setPromoteTarget] = useState<Profile | null>(null);
  const [showArchived, setShowArchived] = useState(false);
  const [roleFilter, setRoleFilter] = useState<'' | 'champion' | 'challenger'>('');
  const [statusFilter, setStatusFilter] = useState<'' | ExperimentStatus>('');
  const [showHelp, setShowHelp] = useState(false);

  // form state
  const [formName, setFormName] = useState('');
  const [formDesc, setFormDesc] = useState('');
  const [formHypothesis, setFormHypothesis] = useState('');
  const [formLearning, setFormLearning] = useState(true);
  const [actionLoading, setActionLoading] = useState(false);

  const load = useCallback(async () => {
    setLoading(true);
    const data = await fetchProfiles();
    if (data) {
      setProfiles(data.profiles);
      setStats(data.stats);
      setError(null);
    } else {
      setError('Failed to load profiles');
    }
    setLoading(false);
  }, []);

  useEffect(() => { load(); }, [load]);

  useEffect(() => {
    if (!toast) return;
    const t = setTimeout(() => setToast(null), 3000);
    return () => clearTimeout(t);
  }, [toast]);

  function getStat(profileId: string) {
    return stats.find(s => s.profileId === profileId);
  }

  async function handleToggle(p: Profile) {
    setActionLoading(true);
    const res = await toggleProfile(p.id, !p.isEnabled);
    if (res.ok) {
      setToast({ message: `${p.profileName} ${p.isEnabled ? 'disabled' : 'enabled'}`, type: 'success' });
      load();
    } else {
      setToast({ message: 'Failed to update profile', type: 'error' });
    }
    setActionLoading(false);
  }

  async function handleDelete() {
    if (!deleteTarget) return;
    setActionLoading(true);
    const res = await deleteProfile(deleteTarget.id);
    if (res.ok) {
      setToast({ message: `${deleteTarget.profileName} deleted`, type: 'success' });
      setDeleteTarget(null);
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Failed to delete profile', type: 'error' });
    }
    setActionLoading(false);
  }

  async function handleCreate() {
    if (!formName.trim()) return;
    setActionLoading(true);
    const res = await createProfile({ name: formName.trim(), description: formDesc.trim() || undefined, learningEnabled: formLearning, hypothesis: formHypothesis.trim() || undefined });
    if (res.ok) {
      setToast({ message: `${formName} created`, type: 'success' });
      setShowCreate(false);
      setFormName(''); setFormDesc(''); setFormHypothesis(''); setFormLearning(true);
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Failed to create profile', type: 'error' });
    }
    setActionLoading(false);
  }

  async function handleClone() {
    if (!cloneSource || !formName.trim()) return;
    setActionLoading(true);
    const res = await cloneProfile(cloneSource.id, formName.trim(), formDesc.trim() || undefined, formHypothesis.trim() || undefined);
    if (res.ok) {
      setToast({ message: `Cloned as ${formName}`, type: 'success' });
      setCloneSource(null);
      setFormName(''); setFormDesc(''); setFormHypothesis('');
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Failed to clone profile', type: 'error' });
    }
    setActionLoading(false);
  }

  function openClone(p: Profile) {
    setCloneSource(p);
    setFormName(`${p.profileName} (Copy)`);
    setFormDesc(p.description || '');
    setFormHypothesis('');
  }

  async function handlePromote() {
    if (!promoteTarget) return;
    setActionLoading(true);
    const res = await promoteProfile(promoteTarget.id);
    if (res.ok) {
      setToast({ message: `${promoteTarget.profileName} promoted to champion`, type: 'success' });
      setPromoteTarget(null);
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Promotion failed', type: 'error' });
    }
    setActionLoading(false);
  }

  async function handleArchive(p: Profile) {
    setActionLoading(true);
    const res = await archiveProfile(p.id);
    if (res.ok) {
      setToast({ message: `${p.profileName} archived`, type: 'success' });
      load();
    } else {
      setToast({ message: 'Failed to archive', type: 'error' });
    }
    setActionLoading(false);
  }

  async function handleStartTesting(p: Profile) {
    setActionLoading(true);
    const res = await startTestingProfile(p.id);
    if (res.ok) {
      setToast({ message: `${p.profileName} moved to testing`, type: 'success' });
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Failed to start testing', type: 'error' });
    }
    setActionLoading(false);
  }

  async function handleComplete(p: Profile) {
    setActionLoading(true);
    const res = await completeExperiment(p.id);
    if (res.ok) {
      setToast({ message: `${p.profileName} experiment completed`, type: 'success' });
      load();
    } else {
      const data = await res.json().catch(() => null);
      setToast({ message: data?.error || 'Failed to complete', type: 'error' });
    }
    setActionLoading(false);
  }

  // ---------------------------------------------------------------------------
  // Render
  // ---------------------------------------------------------------------------

  return (
    <AppShell>
      <div className="max-w-6xl mx-auto space-y-6">
        {/* Header */}
        <div className="flex items-center justify-between">
          <div>
            <h1 className="text-2xl font-bold text-zinc-100">Prediction Profiles</h1>
            <p className="text-sm text-zinc-400 mt-1">Manage champion and challenger weight configurations</p>
          </div>
          <div className="flex items-center gap-3">
            <select value={roleFilter} onChange={e => setRoleFilter(e.target.value as typeof roleFilter)} className="px-2.5 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200">
              <option value="">All roles</option>
              <option value="champion">Champion</option>
              <option value="challenger">Challenger</option>
            </select>
            <select value={statusFilter} onChange={e => setStatusFilter(e.target.value as typeof statusFilter)} className="px-2.5 py-1.5 bg-zinc-800 border border-zinc-700 rounded-lg text-xs text-zinc-200">
              <option value="">All statuses</option>
              <option value="active">Active</option>
              <option value="draft">Draft</option>
              <option value="testing">Testing</option>
              <option value="completed">Completed</option>
              <option value="archived">Archived</option>
            </select>
            <label className="flex items-center gap-1.5 text-xs text-zinc-500 cursor-pointer">
              <input type="checkbox" checked={showArchived} onChange={e => setShowArchived(e.target.checked)} className="rounded border-zinc-600 bg-zinc-800 text-violet-500" />
              Show archived
            </label>
            <button onClick={() => setShowHelp(h => !h)} className="px-3 py-2 text-sm text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800 rounded-lg transition-colors">
              {showHelp ? 'Hide Help' : '? Help'}
            </button>
            <button onClick={() => router.push('/profiles/analytics')} className="px-3 py-2 text-sm text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800 rounded-lg transition-colors">
              Analytics
            </button>
            <button
              onClick={() => { setShowCreate(true); setFormName(''); setFormDesc(''); setFormHypothesis(''); setFormLearning(true); }}
              className="px-4 py-2 bg-violet-600 hover:bg-violet-500 text-white text-sm font-medium rounded-lg transition-colors"
            >
              + New Profile
            </button>
          </div>
        </div>

        {/* Help panel */}
        {showHelp && (
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl p-5 space-y-4 text-sm">
            <div className="flex items-center justify-between">
              <h3 className="font-semibold text-zinc-100">How Profiles Work</h3>
              <button onClick={() => setShowHelp(false)} className="text-zinc-500 hover:text-zinc-300 text-xs">Close</button>
            </div>
            <div className="grid grid-cols-1 md:grid-cols-2 gap-4 text-zinc-400">
              <div>
                <h4 className="text-zinc-200 font-medium mb-1.5">Champion vs Challenger</h4>
                <p>The <span className="text-amber-300">champion</span> is your live production profile — its weights drive all real predictions. <span className="text-zinc-300">Challengers</span> are experiments that run alongside it with different weight configs so you can compare accuracy.</p>
              </div>
              <div>
                <h4 className="text-zinc-200 font-medium mb-1.5">Experiment Lifecycle</h4>
                <p className="space-y-1">
                  <span className="block"><span className="text-zinc-300">Draft</span> — created but not generating predictions yet.</span>
                  <span className="block"><span className="text-blue-300">Testing</span> — actively generating predictions each morning scan.</span>
                  <span className="block"><span className="text-violet-300">Completed</span> — experiment finished, results ready for review.</span>
                  <span className="block"><span className="text-zinc-500">Archived</span> — retired, hidden by default.</span>
                </p>
              </div>
              <div>
                <h4 className="text-zinc-200 font-medium mb-1.5">Actions</h4>
                <p className="space-y-1">
                  <span className="block"><span className="text-blue-400">Start Test</span> — moves a draft challenger to testing; it begins generating predictions.</span>
                  <span className="block"><span className="text-violet-400">Complete</span> — marks a testing experiment as finished.</span>
                  <span className="block"><span className="text-amber-400">Promote</span> — makes this challenger the new champion. The old champion is demoted.</span>
                  <span className="block"><span className="text-zinc-400">Clone</span> — copies a profile's weight config into a new challenger.</span>
                </p>
              </div>
              <div>
                <h4 className="text-zinc-200 font-medium mb-1.5">Weight Keys</h4>
                <p>Each profile can override 8 scoring weights: <span className="text-zinc-300">trend, momentum, volume, volatility, market_context, catalyst, learning, research_signal</span>. Unset keys default to 1.0. View a profile to see and edit its weights.</p>
              </div>
            </div>
          </div>
        )}

        {/* Toast */}
        {toast && (
          <div className={`px-4 py-3 rounded-lg text-sm ${toast.type === 'success' ? 'bg-green-900/50 text-green-300 border border-green-700' : 'bg-red-900/50 text-red-300 border border-red-700'}`}>
            {toast.message}
          </div>
        )}

        {/* Loading / Error */}
        {loading && <div className="text-zinc-400 text-center py-12">Loading profiles...</div>}
        {error && <div className="text-red-400 text-center py-12">{error}</div>}

        {/* Profiles Table */}
        {!loading && !error && (
          <div className="bg-zinc-900 border border-zinc-800 rounded-xl overflow-hidden">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b border-zinc-800 text-zinc-400 text-left">
                  <th className="px-4 py-3 font-medium">Name</th>
                  <th className="px-4 py-3 font-medium">Role</th>
                  <th className="px-4 py-3 font-medium">Experiment</th>
                  <th className="px-4 py-3 font-medium">Learning</th>
                  <th className="px-4 py-3 font-medium text-right">Predictions</th>
                  <th className="px-4 py-3 font-medium text-right">Evaluated</th>
                  <th className="px-4 py-3 font-medium text-right">Actions</th>
                </tr>
              </thead>
              <tbody>
                {profiles.filter(p => {
                    if (!showArchived && p.experimentStatus === 'archived') return false;
                    if (roleFilter && p.role !== roleFilter) return false;
                    if (statusFilter && p.experimentStatus !== statusFilter) return false;
                    return true;
                  }).map(p => {
                  const stat = getStat(p.id);
                  const st = STATUS_STYLES[p.experimentStatus] || STATUS_STYLES.draft;
                  return (
                    <tr key={p.id} className={`border-b border-zinc-800/50 hover:bg-zinc-800/30 transition-colors ${p.experimentStatus === 'archived' ? 'opacity-60' : ''}`}>
                      <td className="px-4 py-3">
                        <button onClick={() => router.push(`/profiles/${p.id}`)} className="text-violet-400 hover:text-violet-300 font-medium text-left">
                          {p.profileName}
                        </button>
                        {p.hypothesis && <p className="text-xs text-zinc-500 mt-0.5 truncate max-w-xs italic">{p.hypothesis}</p>}
                        {!p.hypothesis && p.description && <p className="text-xs text-zinc-500 mt-0.5 truncate max-w-xs">{p.description}</p>}
                      </td>
                      <td className="px-4 py-3">
                        <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium ${p.role === 'champion' ? 'bg-amber-900/50 text-amber-300 border border-amber-700/50' : 'bg-zinc-800 text-zinc-300 border border-zinc-700/50'}`}>
                          {p.role}
                        </span>
                      </td>
                      <td className="px-4 py-3">
                        <span className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${st.bg} ${st.text}`}>
                          {st.label}
                        </span>
                      </td>
                      <td className="px-4 py-3">
                        <span className={`text-xs ${p.learningEnabled ? 'text-blue-400' : 'text-zinc-500'}`}>
                          {p.learningEnabled ? 'Adaptive' : 'Static'}
                        </span>
                      </td>
                      <td className="px-4 py-3 text-right text-zinc-300 tabular-nums">{stat?.totalPredictions ?? '—'}</td>
                      <td className="px-4 py-3 text-right text-zinc-300 tabular-nums">{stat?.evaluatedPredictions ?? '—'}</td>
                      <td className="px-4 py-3 text-right">
                        <div className="flex items-center justify-end gap-1">
                          <button onClick={() => router.push(`/profiles/${p.id}`)} className="px-2 py-1 text-xs text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800 rounded transition-colors">
                            View
                          </button>
                          <button onClick={() => openClone(p)} className="px-2 py-1 text-xs text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800 rounded transition-colors">
                            Clone
                          </button>
                          {/* Lifecycle actions */}
                          {p.experimentStatus === 'draft' && (
                            <button onClick={() => handleStartTesting(p)} disabled={actionLoading} className="px-2 py-1 text-xs text-blue-400 hover:text-blue-300 hover:bg-blue-900/30 rounded transition-colors">
                              Start Test
                            </button>
                          )}
                          {p.experimentStatus === 'testing' && (
                            <button onClick={() => handleComplete(p)} disabled={actionLoading} className="px-2 py-1 text-xs text-violet-400 hover:text-violet-300 hover:bg-violet-900/30 rounded transition-colors">
                              Complete
                            </button>
                          )}
                          {(p.experimentStatus === 'testing' || p.experimentStatus === 'completed') && p.role !== 'champion' && (
                            <button onClick={() => setPromoteTarget(p)} disabled={actionLoading} className="px-2 py-1 text-xs text-amber-400 hover:text-amber-300 hover:bg-amber-900/30 rounded transition-colors">
                              Promote
                            </button>
                          )}
                          {p.role !== 'champion' && p.experimentStatus !== 'archived' && (
                            <button onClick={() => handleArchive(p)} disabled={actionLoading} className="px-2 py-1 text-xs text-zinc-500 hover:text-zinc-300 hover:bg-zinc-800 rounded transition-colors">
                              Archive
                            </button>
                          )}
                          {p.role !== 'champion' && p.experimentStatus !== 'archived' && (
                            <button onClick={() => setDeleteTarget(p)} className="px-2 py-1 text-xs text-red-400 hover:text-red-300 hover:bg-red-900/30 rounded transition-colors">
                              Delete
                            </button>
                          )}
                        </div>
                      </td>
                    </tr>
                  );
                })}
                {profiles.filter(p => {
                    if (!showArchived && p.experimentStatus === 'archived') return false;
                    if (roleFilter && p.role !== roleFilter) return false;
                    if (statusFilter && p.experimentStatus !== statusFilter) return false;
                    return true;
                  }).length === 0 && (
                  <tr><td colSpan={7} className="px-4 py-8 text-center text-zinc-500">No profiles found</td></tr>
                )}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {/* Create Modal */}
      {showCreate && (
        <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50" onClick={() => setShowCreate(false)}>
          <div className="bg-zinc-900 border border-zinc-700 rounded-xl p-6 w-full max-w-md space-y-4" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg font-semibold text-zinc-100">New Profile</h2>
            <div>
              <label className="block text-sm text-zinc-400 mb-1">Name</label>
              <input value={formName} onChange={e => setFormName(e.target.value)} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500" placeholder="e.g. Aggressive Momentum" />
            </div>
            <div>
              <label className="block text-sm text-zinc-400 mb-1">Description</label>
              <textarea value={formDesc} onChange={e => setFormDesc(e.target.value)} rows={2} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500 resize-none" placeholder="Optional description" />
            </div>
            <div>
              <label className="block text-sm text-zinc-400 mb-1">Hypothesis</label>
              <textarea value={formHypothesis} onChange={e => setFormHypothesis(e.target.value)} rows={2} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500 resize-none" placeholder="What are you testing? e.g. Higher momentum weight improves bull accuracy" />
            </div>
            <div className="flex items-center gap-2">
              <input type="checkbox" id="learning" checked={formLearning} onChange={e => setFormLearning(e.target.checked)} className="rounded border-zinc-600 bg-zinc-800 text-violet-500" />
              <label htmlFor="learning" className="text-sm text-zinc-300">Enable adaptive learning</label>
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <button onClick={() => setShowCreate(false)} className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors">Cancel</button>
              <button onClick={handleCreate} disabled={actionLoading || !formName.trim()} className="px-4 py-2 bg-violet-600 hover:bg-violet-500 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                {actionLoading ? 'Creating...' : 'Create'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Clone Modal */}
      {cloneSource && (
        <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50" onClick={() => setCloneSource(null)}>
          <div className="bg-zinc-900 border border-zinc-700 rounded-xl p-6 w-full max-w-md space-y-4" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg font-semibold text-zinc-100">Clone: {cloneSource.profileName}</h2>
            <p className="text-sm text-zinc-400">Creates a new challenger profile with the same weight configuration.</p>
            <div>
              <label className="block text-sm text-zinc-400 mb-1">New Name</label>
              <input value={formName} onChange={e => setFormName(e.target.value)} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500" />
            </div>
            <div>
              <label className="block text-sm text-zinc-400 mb-1">Description</label>
              <textarea value={formDesc} onChange={e => setFormDesc(e.target.value)} rows={2} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500 resize-none" />
            </div>
            <div>
              <label className="block text-sm text-zinc-400 mb-1">Hypothesis</label>
              <textarea value={formHypothesis} onChange={e => setFormHypothesis(e.target.value)} rows={2} className="w-full px-3 py-2 bg-zinc-800 border border-zinc-700 rounded-lg text-zinc-100 text-sm focus:outline-none focus:border-violet-500 resize-none" placeholder="What are you testing with this variant?" />
            </div>
            <div className="flex justify-end gap-2 pt-2">
              <button onClick={() => setCloneSource(null)} className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors">Cancel</button>
              <button onClick={handleClone} disabled={actionLoading || !formName.trim()} className="px-4 py-2 bg-violet-600 hover:bg-violet-500 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                {actionLoading ? 'Cloning...' : 'Clone'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Delete Confirmation */}
      {deleteTarget && (
        <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50" onClick={() => setDeleteTarget(null)}>
          <div className="bg-zinc-900 border border-zinc-700 rounded-xl p-6 w-full max-w-md space-y-4" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg font-semibold text-red-400">Delete Profile</h2>
            <p className="text-sm text-zinc-300">Are you sure you want to delete <strong>{deleteTarget.profileName}</strong>? This action cannot be undone.</p>
            <div className="flex justify-end gap-2 pt-2">
              <button onClick={() => setDeleteTarget(null)} className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors">Cancel</button>
              <button onClick={handleDelete} disabled={actionLoading} className="px-4 py-2 bg-red-600 hover:bg-red-500 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                {actionLoading ? 'Deleting...' : 'Delete'}
              </button>
            </div>
          </div>
        </div>
      )}
      {/* Promote Confirmation */}
      {promoteTarget && (
        <div className="fixed inset-0 bg-black/60 flex items-center justify-center z-50" onClick={() => setPromoteTarget(null)}>
          <div className="bg-zinc-900 border border-zinc-700 rounded-xl p-6 w-full max-w-md space-y-4" onClick={e => e.stopPropagation()}>
            <h2 className="text-lg font-semibold text-amber-300">Promote to Champion</h2>
            <p className="text-sm text-zinc-300">
              Promote <strong>{promoteTarget.profileName}</strong> to champion? The current champion will be demoted to a completed challenger.
            </p>
            <p className="text-xs text-zinc-500">This will change which weight configuration is used for all production predictions going forward.</p>
            <div className="flex justify-end gap-2 pt-2">
              <button onClick={() => setPromoteTarget(null)} className="px-4 py-2 text-sm text-zinc-400 hover:text-zinc-200 rounded-lg transition-colors">Cancel</button>
              <button onClick={handlePromote} disabled={actionLoading} className="px-4 py-2 bg-amber-600 hover:bg-amber-500 disabled:opacity-50 text-white text-sm font-medium rounded-lg transition-colors">
                {actionLoading ? 'Promoting...' : 'Promote'}
              </button>
            </div>
          </div>
        </div>
      )}
    </AppShell>
  );
}
