import { useEffect, useState } from 'react'
import { RefreshCw, ShieldCheck } from 'lucide-react'

type CopyStatus = {
  id: string
  destinationName: string
  destinationKind: string
  state: string
  errorCode?: string | null
  verifiedAt?: string | null
  hasNativeLocator: boolean
  routeRole?: string | null
  routeEnabled: boolean
  routeDraining: boolean
  latestJobState?: string | null
  catalogState?: string | null
}

type ProtectionDetail = {
  assessment: string
  state?: string | null
  verifiedIndependentCopies?: number | null
  requiredVerifiedCopies?: number | null
  desiredVerifiedCopies?: number | null
  requiredCopyDebt?: number | null
  desiredCopyDebt?: number | null
  copies: CopyStatus[]
}

const protectionLabels: Record<string, string> = {
  protected: 'Защищён',
  degraded: 'Защита снижена',
  pending: 'Доставка ожидается',
  unavailable: 'Защита недоступна',
}

const copyLabels: Record<string, string> = {
  planned: 'Запланирована',
  uploading: 'Загружается',
  verified: 'Подтверждена',
  unknown: 'Исход неизвестен',
  retryable_failed: 'Ожидает повторной попытки',
  permanent_failed: 'Доставка не удалась',
  manual_review: 'Нужна ручная проверка',
  quarantined: 'Карантин',
  missing: 'Не найдена',
}

export function BackupProtectionDetail({ backupId, apiBaseUrl }: { backupId: string; apiBaseUrl: string }) {
  const [detail, setDetail] = useState<ProtectionDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  useEffect(() => {
    const controller = new AbortController()
    void (async () => {
      try {
        const response = await fetch(`${apiBaseUrl}/api/v1/admin/backups/${backupId}/protection`, {
          credentials: 'include', signal: controller.signal,
        })
        if (!response.ok) throw new Error('Не удалось получить состояние защиты.')
        const result = await response.json() as ProtectionDetail
        if (!controller.signal.aborted) setDetail(result)
      } catch (reason) {
        if (!controller.signal.aborted && !(reason instanceof DOMException && reason.name === 'AbortError'))
          setError('Не удалось получить состояние защиты. Обновите страницу и попробуйте ещё раз.')
      } finally {
        if (!controller.signal.aborted) setLoading(false)
      }
    })()
    return () => controller.abort()
  }, [backupId, apiBaseUrl])

  return <section id={`backup-protection-${backupId}`} className="backup-protection-detail" aria-label="Состояние защиты резервной копии">
    {loading && <p className="backup-protection-loading" role="status"><RefreshCw aria-hidden="true" className="spin" width={16} height={16}/>Проверяем состояние копий…</p>}
    {error && <p className="backup-protection-error" role="alert">{error}</p>}
    {detail && <>
      {detail.assessment === 'evaluated' && detail.state ? <div className="backup-protection-summary">
        <ShieldCheck aria-hidden="true" width={19} height={19}/>
        <div><strong>{protectionLabels[detail.state] ?? 'Состояние неизвестно'}</strong><p>
          Подтверждено независимых копий: {detail.verifiedIndependentCopies ?? '—'} / обязательных {detail.requiredVerifiedCopies ?? '—'} / желаемых {detail.desiredVerifiedCopies ?? '—'}.
          Долг: {detail.requiredCopyDebt ?? '—'} обязательных, {detail.desiredCopyDebt ?? '—'} желаемых.
        </p></div>
      </div> : <p className="backup-protection-warning" role="status">
        Защита не подтверждена: {detail.assessment === 'legacy_unassessed' ? 'это прежняя запись без снимка политики.' : 'данные маршрута или политики требуют проверки.'}
      </p>}
      {detail.copies.length === 0 ? <p className="backup-protection-empty">Внешние копии для этого backup не зарегистрированы.</p> :
        <ul className="backup-protection-copies">{detail.copies.map(copy => <li key={copy.id}>
          <div><strong>{copy.destinationName}</strong><span>{copy.destinationKind.toUpperCase()} · {copy.routeRole ?? 'маршрут отсутствует'}</span></div>
          <div><b>{copyLabels[copy.state] ?? 'Неизвестное состояние'}</b><span>{copy.errorCode ? `Код: ${copy.errorCode}` : copy.latestJobState ? `Job: ${copy.latestJobState}` : 'Без ошибки'}</span></div>
          <div><span>{copy.verifiedAt ? `Проверена ${new Date(copy.verifiedAt).toLocaleString('ru-RU')}` : 'Независимая проверка отсутствует'}</span><span>{copy.hasNativeLocator ? 'Ссылка сохранена' : 'Ссылка не подтверждена'}{copy.routeDraining ? ' · маршрут выводится' : !copy.routeEnabled ? ' · маршрут выключен' : ''}</span></div>
        </li>)}</ul>}
    </>}
  </section>
}
