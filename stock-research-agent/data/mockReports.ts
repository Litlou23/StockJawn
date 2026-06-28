import { AgentReport } from '@/types/stockAgent';

export const mockReports: AgentReport[] = [
  {
    id: 'report-2026-06-22',
    date: '2026-06-22',
    summary:
      'Markets opened mixed with semiconductors leading on strong volume. Two new picks surfaced from volume and earnings signals; energy and defense positions from prior days continue to track ahead of benchmarks.',
    pickIds: ['pick-1', 'pick-2'],
  },
  {
    id: 'report-2026-06-21',
    date: '2026-06-21',
    summary:
      'Defense sector saw renewed attention after disclosed congressional and insider buying. Broader market was flat; no major macro catalysts.',
    pickIds: ['pick-3'],
  },
  {
    id: 'report-2026-06-20',
    date: '2026-06-20',
    summary:
      'Retail-adjacent names showed modest momentum on positive product news, though conviction was lower given upcoming consumer spending data.',
    pickIds: ['pick-4'],
  },
  {
    id: 'report-2026-06-19',
    date: '2026-06-19',
    summary:
      'Energy was the strongest sector this week. Analyst target raises added confidence to an already favorable relative-strength signal.',
    pickIds: ['pick-5'],
  },
];
