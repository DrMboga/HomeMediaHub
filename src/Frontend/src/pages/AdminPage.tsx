import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { getJobs, getPipelineStatus, retryFailedJobs, retryJob, triggerScan } from '../api/admin'
import type { JobDto } from '../types/api'

const JOBS_PAGE_SIZE = 20

const STATUS_LABELS: Record<keyof import('../types/api').JobStatusCounts, string> = {
  queued: 'Queued',
  processing: 'Processing',
  enriching: 'Enriching',
  completed: 'Completed',
  failed: 'Failed',
  skippedDuplicate: 'Duplicates',
}

function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleString('en-GB', {
    day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit',
  })
}

export function AdminPage() {
  const queryClient = useQueryClient()
  const [scanPath, setScanPath] = useState('')
  const [scanResult, setScanResult] = useState<string | null>(null)
  const [jobsPage, setJobsPage] = useState(1)

  const { data: status } = useQuery({
    queryKey: ['pipeline-status'],
    queryFn: getPipelineStatus,
    refetchInterval: 5000,
  })

  const { data: failedJobs, isLoading: jobsLoading } = useQuery({
    queryKey: ['jobs', 'failed', jobsPage],
    queryFn: () => getJobs({ status: 'failed', page: jobsPage, pageSize: JOBS_PAGE_SIZE }),
    refetchInterval: 5000,
  })

  const invalidateAll = () => {
    queryClient.invalidateQueries({ queryKey: ['pipeline-status'] })
    queryClient.invalidateQueries({ queryKey: ['jobs'] })
  }

  const { mutate: doScan, isPending: scanning } = useMutation({
    mutationFn: () => triggerScan(scanPath.trim() || undefined),
    onSuccess: ({ scanId }) => {
      setScanResult(scanId)
      invalidateAll()
    },
  })

  const { mutate: doRetry, isPending: retrying } = useMutation({
    mutationFn: (id: string) => retryJob(id),
    onSuccess: invalidateAll,
  })

  const { mutate: doRetryAll, isPending: retryingAll } = useMutation({
    mutationFn: retryFailedJobs,
    onSuccess: invalidateAll,
  })

  const counts = status?.counts
  const total = status?.total ?? 0
  const doneCount = (counts?.completed ?? 0) + (counts?.skippedDuplicate ?? 0)
  const progressPct = total > 0 ? Math.round((doneCount / total) * 100) : 0

  return (
    <div className="page">
      <div className="sec-head">
        <span className="sec-title">Admin</span>
      </div>

      <div className="admin-grid">
        {/* Scan trigger */}
        <div className="card">
          <div className="card-head">
            <span className="card-title">Library Scan</span>
          </div>
          <div className="admin-card-body">
            <div className="form-field">
              <label className="form-label">Path (optional)</label>
              <input
                className="form-input"
                type="text"
                placeholder="/photo"
                value={scanPath}
                onChange={e => setScanPath(e.target.value)}
              />
            </div>
            <button className="btn-primary" onClick={() => doScan()} disabled={scanning}>
              {scanning ? 'Starting…' : 'Trigger Scan'}
            </button>
            {scanResult && (
              <div className="admin-scan-result">Scan started — id {scanResult}</div>
            )}
          </div>
        </div>

        {/* Pipeline progress */}
        <div className="card">
          <div className="card-head">
            <span className="card-title">Pipeline Progress</span>
            <span className="card-action" style={{ cursor: 'default', color: 'var(--label3)' }}>
              {status?.completedLastHour ?? 0}/hr
            </span>
          </div>
          <div className="admin-card-body">
            {total > 0 ? (
              <>
                <div className="progress-bar">
                  <div className="progress-bar-fill" style={{ width: `${progressPct}%` }} />
                </div>
                <div className="admin-status-counts">
                  {counts && (Object.keys(STATUS_LABELS) as (keyof typeof STATUS_LABELS)[]).map(key => (
                    <div key={key} className="admin-status-item">
                      <span className="stat-num" style={{ fontSize: 18 }}>{counts[key]}</span>
                      <span className="stat-lbl">{STATUS_LABELS[key]}</span>
                    </div>
                  ))}
                </div>
                {status && status.staleCount > 0 && (
                  <div className="admin-stale-warning">
                    <span className="mi">warning</span>
                    {status.staleCount} job{status.staleCount === 1 ? '' : 's'} stuck enriching &gt; 24h
                  </div>
                )}
              </>
            ) : (
              <div className="loading">No scans yet</div>
            )}
          </div>
        </div>
      </div>

      {/* Failed jobs */}
      <div className="sec-head" style={{ marginTop: 8 }}>
        <span className="sec-title" style={{ fontSize: 18 }}>Failed Jobs</span>
        <button
          className="btn-primary"
          style={{ width: 'auto', padding: '6px 14px', fontSize: 12, marginTop: 0 }}
          onClick={() => doRetryAll()}
          disabled={retryingAll || (failedJobs?.length ?? 0) === 0}
        >
          {retryingAll ? 'Retrying…' : 'Retry All Failed'}
        </button>
      </div>

      {jobsLoading && <div className="loading">Loading…</div>}

      {!jobsLoading && (failedJobs?.length ?? 0) === 0 && jobsPage === 1 && (
        <div className="empty-state">
          <span className="mi">check_circle</span>
          <span>No failed jobs</span>
        </div>
      )}

      {failedJobs && failedJobs.length > 0 && (
        <div className="admin-table-wrap">
          <table className="admin-table">
            <thead>
              <tr>
                <th>File</th>
                <th>Failed Step</th>
                <th>Error</th>
                <th>Started</th>
                <th>Retries</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              {failedJobs.map((job: JobDto) => (
                <tr key={job.id}>
                  <td className="admin-table-path">{job.filePath}</td>
                  <td>{job.failedStep ?? '—'}</td>
                  <td className="admin-table-error">{job.errorMessage ?? '—'}</td>
                  <td>{formatDate(job.startedAt)}</td>
                  <td>{job.retryCount}</td>
                  <td>
                    <button
                      className="pager-btn"
                      style={{ padding: '4px 10px' }}
                      onClick={() => doRetry(job.id)}
                      disabled={retrying}
                    >
                      Retry
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {(jobsPage > 1 || (failedJobs?.length ?? 0) === JOBS_PAGE_SIZE) && (
        <div className="pagination">
          <button
            className="pager-btn"
            disabled={jobsPage <= 1}
            onClick={() => setJobsPage(p => p - 1)}
          >
            <span className="mi" style={{ fontSize: 16 }}>chevron_left</span>Previous
          </button>
          <span className="pager-info">Page {jobsPage}</span>
          <button
            className="pager-btn"
            disabled={(failedJobs?.length ?? 0) < JOBS_PAGE_SIZE}
            onClick={() => setJobsPage(p => p + 1)}
          >
            Next<span className="mi" style={{ fontSize: 16 }}>chevron_right</span>
          </button>
        </div>
      )}
    </div>
  )
}
