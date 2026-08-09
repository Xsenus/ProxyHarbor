import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'

const stats = {
  alive: 0,
  staleAlive: 0,
  pending: 1,
  dead: 0,
  dueForCheck: 1,
  scheduledChecks: 0,
  averageLatencyMs: null,
  sources: 81,
  failingSources: 0,
  repeatedlyFailingSources: 0,
  byProtocol: [],
}

describe('ProxyHarbor UI', () => {
  beforeEach(() => {
    sessionStorage.clear()
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0 })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({ title: 'Неверный ключ администратора' }, 401)
      return jsonResponse({ title: 'Unexpected request' }, 500)
    }))
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('keeps an authenticated in-memory session when browser storage is unavailable', async () => {
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new DOMException('Storage disabled', 'SecurityError')
    })
    vi.spyOn(Storage.prototype, 'removeItem').mockImplementation(() => {
      throw new DOMException('Storage disabled', 'SecurityError')
    })
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0 })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([])
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: '2026-08-09T10:00:00Z', databaseBytes: 0,
        validationQueue: { total: 0, leased: 0, neverChecked: 0, due: 0, scheduled: 0, repeatedlyFailing: 0 },
        recentRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await screen.findByText('система активна')
    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    fireEvent.change(screen.getByLabelText('Ключ администратора'), { target: { value: 'valid-admin-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))

    expect(await screen.findByLabelText('Диагностика сервиса')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Создать backup' })).toBeEnabled()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }))
    expect(screen.getByLabelText('Ключ администратора')).toHaveValue('')
    expect(screen.queryByLabelText('Диагностика сервиса')).not.toBeInTheDocument()
  })

  it('keeps public API healthy when admin authentication fails', async () => {
    render(<App />)
    expect(await screen.findByText('система активна')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    fireEvent.change(screen.getByLabelText('Ключ администратора'), { target: { value: 'wrong-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Неверный ключ администратора')
    expect(screen.getByText('система активна')).toBeInTheDocument()
    expect(screen.queryByText('API недоступен')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Запустить сбор' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Проверить пакет' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Создать backup' })).toBeDisabled()
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/api/v1/admin/sources'))).toHaveLength(1)
  })

  it('closes the admin dialog with Escape and restores focus', async () => {
    render(<App />)
    await screen.findByText('система активна')
    const trigger = screen.getByRole('button', { name: /^Управление$/ })
    fireEvent.click(trigger)

    await waitFor(() => expect(screen.getByLabelText('Ключ администратора')).toHaveFocus())
    fireEvent.keyDown(document, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(trigger).toHaveFocus()
  })

  it('keeps export links synchronized with public filters', async () => {
    render(<App />)
    await screen.findByText('система активна')
    const jsonExport = screen.getByRole('link', { name: '.json' })
    expect(jsonExport).toHaveAttribute('href', '/api/v1/export/json?maxLatencyMs=2000')

    fireEvent.click(screen.getByRole('button', { name: 'SOCKS5' }))
    fireEvent.change(screen.getByRole('slider'), { target: { value: '1000' } })

    await waitFor(() => expect(jsonExport).toHaveAttribute(
      'href', '/api/v1/export/json?maxLatencyMs=1000&protocol=Socks5'))
  })

  it('labels built-in sources with canonical provider metadata', async () => {
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0 })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([{
        id: 'source-1', name: 'ProxyScrape HTTP', url: 'https://example.test/list.txt',
        defaultProtocol: 'Http', enabled: true, priority: 1, lastItemCount: 100,
        consecutiveFailures: 0, isBuiltIn: true, provider: 'ProxyScrape', catalogRank: 2,
      }])
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: '2026-08-09T07:00:00Z', databaseBytes: 0,
        validationQueue: { total: 0, leased: 0, neverChecked: 0, due: 0, scheduled: 0, repeatedlyFailing: 0 },
        recentRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await screen.findByText('система активна')
    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    fireEvent.change(screen.getByLabelText('Ключ администратора'), { target: { value: 'valid-admin-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))

    expect(await screen.findByTitle('Встроенный источник · ProxyScrape · ранг 2')).toHaveTextContent('ProxyScrape')
  })

  it('shows operational diagnostics and refreshes them after a backup', async () => {
    let failDiagnostics = false
    const diagnostics = {
      serverTime: '2026-08-09T07:00:00Z',
      databaseBytes: 25 * 1024 * 1024,
      validationQueue: { total: 1000, leased: 12, neverChecked: 40, neverAttempted: 38, due: 75, scheduled: 925, repeatedlyFailing: 3, attemptsLastFiveMinutes: 1000, checkedLastFiveMinutes: 994, aliveLastFiveMinutes: 6, deferredLastFiveMinutes: 6, lastAttemptAt: '2026-08-09T06:59:55Z' },
      sourceCatalog: { expectedSources: 81, presentSources: 81, enabledSources: 81, healthySources: 79, failingSources: 0, neverAuditedSources: 0, staleSources: 1, truncatedSources: 1, expectedProviders: 50, presentProviders: 50, enabledProviders: 50, isComplete: true, isHealthy: false },
      recentRuns: [{
        id: 'collection-1', startedAt: '2026-08-09T06:59:00Z', finishedAt: '2026-08-09T06:59:05Z',
        sourcesProcessed: 81, sourcesSucceeded: 81, sourcesFailed: 0, sourcesSkipped: 0,
        sourcesTruncated: 1, candidatesFound: 184005, candidateLimitReached: true,
        newProxies: 6377, status: 'completed',
      }],
      recentBackups: [{
        id: 'backup-1', startedAt: '2026-08-09T06:58:00Z', finishedAt: '2026-08-09T06:58:10Z',
        status: 'completed', fileName: 'proxyharbor.phbackup', sizeBytes: 1024 * 1024,
        telegramConfigured: true, sentToTelegram: true,
      }],
    }
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0 })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([])
      if (url.includes('/api/v1/admin/diagnostics')) return failDiagnostics
        ? jsonResponse({ title: 'Диагностика временно недоступна' }, 503)
        : jsonResponse(diagnostics)
      if (url.endsWith('/api/v1/admin/backup') && init?.method === 'POST') return jsonResponse({ created: 'proxyharbor.phbackup', sentToTelegram: true })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await screen.findByText('система активна')
    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    fireEvent.change(screen.getByLabelText('Ключ администратора'), { target: { value: 'valid-admin-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))

    expect(await screen.findByLabelText('Диагностика сервиса')).toBeInTheDocument()
    expect(screen.getByText('25 МБ')).toBeInTheDocument()
    expect(screen.getAllByText(/доставлен в Telegram/)).not.toHaveLength(0)
    expect(screen.getByText('184 005 кандидатов', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('1 000 попыток за 5 мин', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('6 живых', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('6 отложено', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('достигнут лимит')).toBeInTheDocument()
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('81/81')
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('50/50 провайдеров')
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('1 устарело')
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('1 усечено')
    expect(screen.getByText('общий лимит', { exact: false })).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Создать backup' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input, init]) =>
      String(input).endsWith('/api/v1/admin/backup') && init?.method === 'POST')).toBe(true))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([input]) =>
      String(input).includes('/api/v1/admin/diagnostics')).length).toBeGreaterThanOrEqual(2))

    // Временная ошибка diagnostics не должна ошибочно инвалидировать корректный admin key.
    failDiagnostics = true
    fireEvent.click(screen.getByRole('button', { name: 'Обновить диагностику' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Диагностика временно недоступна')
    expect(screen.getByLabelText('Диагностика сервиса')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Создать backup' })).toBeEnabled()
    expect(sessionStorage.getItem('proxyharbor-admin-key')).toBe('valid-admin-key')

    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }))
    expect(sessionStorage.getItem('proxyharbor-admin-key')).toBeNull()
    expect(screen.getByLabelText('Ключ администратора')).toHaveValue('')
    expect(screen.queryByLabelText('Диагностика сервиса')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Создать backup' })).toBeDisabled()
  })
})

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}
