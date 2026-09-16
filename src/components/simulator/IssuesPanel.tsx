import { useMemo } from 'react';
import type { NetworkIssuePayload, NetworkStatsPayload, RoadNetworkUpdateMessage } from '../../lib/types';
import { cn } from '../../utils/cn';
import { Badge } from '../ui';

const severityOrder: Record<string, number> = { error: 3, warning: 2, info: 1 };

const severityTone = (s: string): 'rose' | 'amber' | 'slate' | 'emerald' => {
  if (s === 'error') return 'rose';
  if (s === 'warning') return 'amber';
  if (s === 'info') return 'slate';
  return 'emerald';
};

export function IssuesPanel({
  network,
}: {
  network: RoadNetworkUpdateMessage | null;
}) {
  const issues = useMemo(() => {
    if (!network?.issues) return [];
    return [...network.issues].sort(
      (a, b) => (severityOrder[b.severity] ?? 0) - (severityOrder[a.severity] ?? 0),
    );
  }, [network]);

  const stats: NetworkStatsPayload | null = network?.stats ?? null;

  return (
    <div className="flex h-full flex-col">
      <div className="border-b border-slate-800 px-3 py-2">
        <div className="text-xs font-medium text-white">Road Network</div>
        {stats ? (
          <div className="mt-1.5 grid grid-cols-2 gap-x-3 gap-y-0.5 font-mono text-[11px] text-slate-400">
            <span>length</span>
            <span className="text-right text-slate-200">{stats.total_length_m.toFixed(1)} m</span>
            <span>intersections</span>
            <span className="text-right text-slate-200">{stats.intersection_count}</span>
            <span>dead ends</span>
            <span className="text-right text-slate-200">{stats.dead_end_count}</span>
            <span>components</span>
            <span className="text-right text-slate-200">{stats.component_count}</span>
          </div>
        ) : (
          <div className="mt-1 text-[11px] text-slate-600">Waiting for road_network_update…</div>
        )}
        {stats && Object.keys(stats.length_by_class).length > 0 && (
          <div className="mt-1.5 flex flex-wrap gap-1">
            {Object.entries(stats.length_by_class).map(([cls, len]) => (
              <Badge key={cls} tone="slate">
                {cls}: {len.toFixed(0)} m
              </Badge>
            ))}
          </div>
        )}
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto">
        {issues.length === 0 && (
          <div className="p-4 text-center text-[11px] text-slate-600">
            {network ? 'No issues' : 'No data yet'}
          </div>
        )}
        {issues.map((issue: NetworkIssuePayload, idx: number) => (
          <div
            key={`${issue.type}-${idx}`}
            className={cn(
              'border-b border-slate-800/60 px-3 py-2 text-[11.5px]',
              issue.severity === 'error' && 'bg-rose-950/20',
              issue.severity === 'warning' && 'bg-amber-950/10',
            )}
          >
            <div className="flex items-center gap-2">
              <Badge tone={severityTone(issue.severity)}>{issue.severity}</Badge>
              <code className="font-mono text-xs text-white">{issue.type}</code>
            </div>
            <div className="mt-1 text-slate-300">{issue.message}</div>
            {(issue.related_edge_ids?.length || issue.related_node_id) && (
              <div className="mt-1 font-mono text-[10px] text-slate-500">
                {issue.related_node_id && <span>node {issue.related_node_id} </span>}
                {issue.related_edge_ids && issue.related_edge_ids.length > 0 && (
                  <span>
                    edges {issue.related_edge_ids.map((id) => id.slice(0, 8)).join(', ')}
                  </span>
                )}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}
