import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import {
  getJobs, getLastPotdSends, getPipelineStatus, retryFailedJobs, retryJob,
  sendPhotoOfTheDay, triggerScan,
} from '../api/admin'
import { thumbnailUrl } from '../api/media'
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

/**
 * The POTD endpoint takes a month and a day, never a year — it looks at that calendar date
 * across every year — so this is a `dd.mm` field, not a date picker. Blank means today in
 * `Potd:TimeZone`, which is what the scheduler would use.
 */
function parseDayMonth(value: string): { month: number; day: number } | null {
  const m = /^\s*(\d{1,2})[.\-/](\d{1,2})\s*$/.exec(value)
  if (!m) return null
  const [day, month] = [Number(m[1]), Number(m[2])]
  if (month < 1 || month > 12 || day < 1 || day > 31) return null
  return { month, day }
}

/** `2026-08-17` (a .NET DateOnly on the wire) → `17.08.2026`, without going through Date. */
function formatDateOnly(value: string): string {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value)
  return m ? `${m[3]}.${m[2]}.${m[1]}` : value
}

export function AdminPage() {
  const queryClient = useQueryClient()
  const [scanPath, setScanPath] = useState('')
  const [scanResult, setScanResult] = useState<string | null>(null)
  const [jobsPage, setJobsPage] = useState(1)
  const [potdDate, setPotdDate] = useState('')
  const [potdDryRun, setPotdDryRun] = useState(true)

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

  const { data: potdSends } = useQuery({
    queryKey: ['potd-sends'],
    queryFn: () => getLastPotdSends(5),
    staleTime: 60_000,
  })

  const potdDateParsed = potdDate.trim() === '' ? null : parseDayMonth(potdDate)
  const potdDateInvalid = potdDate.trim() !== '' && potdDateParsed === null

  const {
    mutate: doSendPotd, isPending: sendingPotd, data: potdResult, error: potdError,
  } = useMutation({
    mutationFn: () => sendPhotoOfTheDay({ ...potdDateParsed, dryRun: potdDryRun }),
    // A real send does not write potd_sends either (only the scheduler does), but refreshing
    // costs nothing and keeps the card honest if the scheduler fired while this page was open.
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['potd-sends'] }),
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

        {/* Photo of the Day */}
        <div className="card">
          <div className="card-head">
            <span className="card-title">Photo of the Day</span>
            <span className="card-action" style={{ cursor: 'default', color: 'var(--label3)' }}>
              {potdSends && potdSends.length > 0
                ? `last ${formatDateOnly(potdSends[0].sentOn)}`
                : 'never sent'}
            </span>
          </div>
          <div className="admin-card-body">
            <div className="form-field" style={{ marginBottom: 0 }}>
              <label className="form-label">Date (optional) — dd.mm, any year</label>
              <input
                className="form-input"
                type="text"
                placeholder="today"
                value={potdDate}
                onChange={e => setPotdDate(e.target.value)}
              />
            </div>

            {/* Defaults to checked: the point of this card is to exercise the pick and the
                caption without posting to the family channel. */}
            <label className="admin-check">
              <input
                type="checkbox"
                checked={potdDryRun}
                onChange={e => setPotdDryRun(e.target.checked)}
              />
              Dry run — pick and report, don&apos;t post
            </label>

            <button
              className="btn-primary"
              onClick={() => doSendPotd()}
              disabled={sendingPotd || potdDateInvalid}
            >
              {sendingPotd ? 'Working…' : potdDryRun ? 'Preview Pick' : 'Send Now'}
            </button>

            {potdDateInvalid && (
              <div className="admin-scan-result" style={{ color: 'var(--danger)' }}>
                Enter a date as dd.mm — or leave it blank for today
              </div>
            )}

            {potdError && (
              <div className="admin-scan-result" style={{ color: 'var(--danger)' }}>
                {(potdError as Error).message}
              </div>
            )}

            {potdResult && (
              <div className="potd-admin-result">
                {potdResult.mediaFileId && (
                  <img src={thumbnailUrl(potdResult.mediaFileId)} alt="" />
                )}
                <div className="potd-admin-result-text">
                  <div className={potdResult.sent ? 'potd-admin-ok' : 'potd-admin-warn'}>
                    {potdResult.sent
                      ? 'Sent to Telegram'
                      : potdResult.skippedReason === 'dryRun'
                        ? 'Dry run — nothing posted'
                        : potdResult.skippedReason}
                  </div>
                  {potdResult.tier && <div>tier: {potdResult.tier}</div>}
                  {potdResult.caption && <div>“{potdResult.caption}”</div>}
                </div>
              </div>
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
