import { useState } from 'react'
import { Download, RefreshCw } from 'lucide-react'
import { useI18n } from '../i18n'
import { clashDownloadMessages } from '../clashDownloadMessages'

type Props = { api: string; onError: (message: string) => void } &
  ({ endpointId: string; exportQuery?: never } | { endpointId?: never; exportQuery: string })

/** Downloads complete saved YAML using the current session and access policy. */
export function ClashDownloadButton({ endpointId, exportQuery, api, onError }: Props) {
  const { language } = useI18n()
  const publicExport = exportQuery !== undefined
  const messages = clashDownloadMessages[publicExport ? language : 'ru']
  const [busy, setBusy] = useState(false)
  const download = async () => {
    setBusy(true)
    onError('')
    try {
      const path = publicExport ? `/api/v1/vpn/export/clash${exportQuery ? `?${exportQuery}` : ''}`
        : `/api/v1/admin/vpn/endpoints/${encodeURIComponent(endpointId!)}/clash`
      const response = await fetch(`${api}${path}`, { credentials: 'include' })
      if (!response.ok) throw new Error(response.status === 401 || response.status === 403
        ? publicExport ? messages.access : 'Нет доступа к конфигурации. Войдите в админку повторно.'
        : response.status === 404 ? publicExport ? messages.unavailable : 'Сохранённая конфигурация уже недоступна.'
        : response.status === 409 ? publicExport ? messages.invalid : 'Сохранённую конфигурацию нельзя безопасно экспортировать.'
        : publicExport && (response.status === 429 || response.status === 503) ? messages.busy
        : messages.failed)
      if (response.headers.get('content-type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/yaml')
        throw new Error(messages.format)
      const blob = await response.blob()
      if (!blob.size) throw new Error(messages.empty)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = publicExport ? 'vpn-configurations.yaml' : `proxyharbor-${endpointId!.replace(/[^a-zA-Z0-9-]/g, '')}.yaml`
      document.body.append(link)
      try { link.click() }
      finally {
        link.remove()
        // Give the browser time to start reading the object URL before releasing it.
        window.setTimeout(() => URL.revokeObjectURL(url), 10_000)
      }
    } catch (reason) {
      onError(reason instanceof Error && reason.name !== 'TypeError'
        ? reason.message : messages.failed)
    } finally { setBusy(false) }
  }
  return <button type="button" className={publicExport ? 'secondary-admin-button' : 'icon-button'} disabled={busy} aria-busy={busy}
    aria-label={busy ? messages.downloading : messages.download} data-tooltip={messages.download}
    onClick={() => void download()}>
    {busy ? <RefreshCw className="spin" width={16} height={16} style={{ flexShrink: 0 }} aria-hidden="true"/>
      : <Download width={16} height={16} style={{ flexShrink: 0 }} aria-hidden="true"/>}
    {publicExport && <span>{busy ? messages.downloading : messages.download}</span>}
  </button>
}
