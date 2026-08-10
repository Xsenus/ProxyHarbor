import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
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

const publicSourceCatalog = {
  lastAuditedOn: '2026-08-10',
  feedCount: 81,
  providerCount: 50,
  providers: [
    {
      rank: 1, name: 'ProxyScrape', protocols: ['Http', 'Socks5'],
      feeds: [{ rank: 1, name: 'ProxyScrape Mixed', url: 'https://api.proxyscrape.com/v4/free-proxy-list/get', protocol: 'Http' }],
    },
    {
      rank: 3, name: 'OpenProxyList', protocols: ['Http'],
      feeds: [{ rank: 3, name: 'OpenProxyList HTTP', url: 'https://openproxylist.xyz/http.txt', protocol: 'Http' }],
    },
  ],
}

describe('ProxyHarbor UI', () => {
  beforeEach(() => {
    sessionStorage.clear()
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/sources')) return jsonResponse(publicSourceCatalog)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({ title: 'Неверный ключ администратора' }, 401)
      return jsonResponse({ title: 'Unexpected request' }, 500)
    }))
  })

  afterEach(() => {
    cleanup()
    vi.useRealTimers()
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
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
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
    await waitFor(() => expect(screen.getByRole('button', { name: 'Запустить сбор' })).toHaveFocus())

    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }))
    expect(screen.getByLabelText('Ключ администратора')).toHaveValue('')
    expect(screen.queryByLabelText('Диагностика сервиса')).not.toBeInTheDocument()
  })

  it('keeps public API healthy when admin authentication fails', async () => {
    render(<App />)
    expect(await screen.findByText('система активна')).toBeInTheDocument()
    expect(screen.getByText(/v0\.0\.0-local · ©/)).toBeInTheDocument()

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

  it('shows the public provider catalog without an admin session', async () => {
    render(<App />)

    expect(await screen.findByRole('heading', { name: '50 независимых провайдеров' })).toBeInTheDocument()
    expect(screen.getByText('81 HTTPS feed · аудит 2026-08-10')).toBeInTheDocument()
    expect(screen.getByText('ProxyScrape')).toBeInTheDocument()
    expect(screen.getByText('OpenProxyList')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'ProxyScrape: открыть исходный feed' })).toHaveAttribute(
      'href', 'https://api.proxyscrape.com/v4/free-proxy-list/get')
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/api/v1/sources'))).toHaveLength(1)
  })

  it('recovers the public provider catalog after a temporary failure', async () => {
    let sourceAttempts = 0
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/sources')) {
        sourceAttempts++
        return sourceAttempts === 1
          ? jsonResponse({ title: 'Temporary failure' }, 503)
          : jsonResponse(publicSourceCatalog)
      }
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    expect(await screen.findByRole('status', { name: 'Состояние каталога источников' })).toHaveTextContent('Каталог источников временно недоступен')
    fireEvent.click(screen.getByRole('button', { name: 'повторить' }))

    expect(await screen.findByText('ProxyScrape')).toBeInTheDocument()
    expect(screen.queryByRole('status', { name: 'Состояние каталога источников' })).not.toBeInTheDocument()
    expect(sourceAttempts).toBe(2)
  })

  it('keeps a truncated-source warning visible after provider metadata loads', async () => {
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse({ ...stats, truncatedSources: 2 })
      if (url.includes('/api/v1/sources')) return jsonResponse(publicSourceCatalog)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    expect(await screen.findByText('2 упёрлись в лимит')).toBeInTheDocument()
    expect(screen.getByRole('heading', { name: '50 независимых провайдеров' })).toBeInTheDocument()
  })

  it('cannot restore an admin session from a response owned by an old key', async () => {
    let resolveSources!: (response: Response) => void
    let resolveDiagnostics!: (response: Response) => void
    const pendingSources = new Promise<Response>(resolve => { resolveSources = resolve })
    const pendingDiagnostics = new Promise<Response>(resolve => { resolveDiagnostics = resolve })
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      if (url.includes('/api/v1/admin/sources')) return pendingSources
      if (url.includes('/api/v1/admin/diagnostics')) return pendingDiagnostics
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await screen.findByText('система активна')
    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    const keyInput = screen.getByLabelText('Ключ администратора')
    fireEvent.change(keyInput, { target: { value: 'old-admin-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.filter(([input]) =>
      String(input).includes('/api/v1/admin/')).length).toBe(2))

    // Fetch mock намеренно игнорирует AbortSignal: generation ownership остаётся
    // последней защитой от позднего ответа уже недействительного ключа.
    fireEvent.change(keyInput, { target: { value: 'new-admin-key' } })
    resolveSources(jsonResponse([]))
    resolveDiagnostics(jsonResponse({
      serverTime: '2026-08-09T10:00:00Z', databaseBytes: 0,
      validationQueue: { total: 0, leased: 0, neverChecked: 0, due: 0, scheduled: 0, repeatedlyFailing: 0 },
      recentRuns: [], recentBackups: [],
    }))

    await waitFor(() => expect(screen.getByRole('button', { name: 'Войти' })).toBeEnabled())
    expect(keyInput).toHaveValue('new-admin-key')
    expect(screen.queryByLabelText('Диагностика сервиса')).not.toBeInTheDocument()
    expect(sessionStorage.getItem('proxyharbor-admin-key')).toBeNull()
  })

  it('does not restore a logged-out session after a stale admin mutation completes', async () => {
    let resolveBackup!: (response: Response) => void
    const pendingBackup = new Promise<Response>(resolve => { resolveBackup = resolve })
    const backupRequest: { signal?: AbortSignal | null } = {}
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/sources') && !url.includes('/admin/')) return jsonResponse(publicSourceCatalog)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([])
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: '2026-08-09T10:00:00Z', databaseBytes: 0,
        validationQueue: { total: 0, leased: 0, neverChecked: 0, due: 0, scheduled: 0, repeatedlyFailing: 0 },
        recentRuns: [], recentBackups: [],
      })
      if (url.endsWith('/api/v1/admin/backup') && init?.method === 'POST') {
        backupRequest.signal = init.signal ?? null
        return pendingBackup
      }
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await screen.findByText('система активна')
    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    fireEvent.change(screen.getByLabelText('Ключ администратора'), { target: { value: 'valid-admin-key' } })
    // Проверяем и нативную form-семантику: такой submit браузер выполняет по Enter в поле ключа.
    fireEvent.submit(screen.getByRole('form', { name: 'Вход администратора' }))
    expect(await screen.findByLabelText('Диагностика сервиса')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: 'Создать backup' }))
    await waitFor(() => expect(backupRequest.signal).toBeDefined())
    const adminReadsBeforeLogout = vi.mocked(fetch).mock.calls.filter(([input]) =>
      /\/api\/v1\/admin\/(sources|diagnostics)/.test(String(input))).length
    fireEvent.click(screen.getByRole('button', { name: 'Выйти' }))

    expect(backupRequest.signal?.aborted).toBe(true)
    resolveBackup(jsonResponse({ created: 'stale.phbackup', sentToTelegram: true }))
    await act(async () => { await Promise.resolve(); await Promise.resolve() })

    expect(sessionStorage.getItem('proxyharbor-admin-key')).toBeNull()
    expect(screen.getByLabelText('Ключ администратора')).toHaveValue('')
    expect(screen.queryByLabelText('Диагностика сервиса')).not.toBeInTheDocument()
    expect(vi.mocked(fetch).mock.calls.filter(([input]) =>
      /\/api\/v1\/admin\/(sources|diagnostics)/.test(String(input)))).toHaveLength(adminReadsBeforeLogout)
  })

  it('does not overlap periodic public polling while the current request is pending', async () => {
    vi.useFakeTimers()
    let resolveStats!: (response: Response) => void
    const pendingStats = new Promise<Response>(resolve => { resolveStats = resolve })
    let statsRequests = 0
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) {
        statsRequests++
        return statsRequests === 1 ? pendingStats : jsonResponse(stats)
      }
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await act(async () => { await vi.advanceTimersByTimeAsync(0) })
    expect(statsRequests).toBe(1)

    // Старый setInterval создал бы ещё четыре запроса за эту минуту. Новый цикл
    // начинает отсчёт 15 секунд только после settle текущего Promise.
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000) })
    expect(statsRequests).toBe(1)

    await act(async () => {
      resolveStats(jsonResponse(stats))
      await Promise.resolve()
    })
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

  it('keeps keyboard focus inside the admin dialog', async () => {
    render(<App />)
    await screen.findByText('система активна')
    const trigger = screen.getByRole('button', { name: /^Управление$/ })
    fireEvent.click(trigger)

    const keyInput = screen.getByLabelText('Ключ администратора')
    await waitFor(() => expect(keyInput).toHaveFocus())
    expect(trigger.closest('header')).toHaveAttribute('inert')

    // Имитируем расширение/скрипт, программно вынесший focus за modal.
    trigger.focus()
    fireEvent.keyDown(document, { key: 'Tab' })
    expect(screen.getByRole('button', { name: 'Закрыть' })).toHaveFocus()

    trigger.focus()
    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true })
    expect(screen.getByLabelText('Приоритет источника')).toHaveFocus()
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

  it('loads and de-duplicates cursor pages without replacing the first page', async () => {
    const firstProxy = {
      host: '192.0.2.10', port: 8080, protocol: 'Http', url: 'http://192.0.2.10:8080',
      latencyMs: 120, successRate: 98, lastCheckedAt: '2026-08-09T10:00:00Z',
    }
    const secondProxy = {
      host: '198.51.100.20', port: 1080, protocol: 'Socks5', url: 'socks5://198.51.100.20:1080',
      latencyMs: 240, successRate: 96, lastCheckedAt: '2026-08-09T10:00:00Z',
    }
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies/seek') && url.includes('after=cursor-token')) {
        return jsonResponse({ items: [firstProxy, secondProxy], pageSize: 100, hasMore: false, nextCursor: null })
      }
      if (url.includes('/api/v1/proxies/seek')) {
        return jsonResponse({ items: [firstProxy], pageSize: 100, hasMore: true, nextCursor: 'cursor-token' })
      }
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    expect(await screen.findByText((_, element) =>
      element?.tagName === 'CODE' && element.textContent === '192.0.2.10:8080')).toBeInTheDocument()
    const proxyTable = screen.getByRole('table', { name: 'Проверенные прокси' })
    expect(screen.getAllByRole('columnheader')).toHaveLength(5)
    expect(proxyTable).toContainElement(screen.getByRole('cell', { name: '192.0.2.10:8080' }))
    expect(screen.getByRole('button', { name: 'Все' })).toHaveAttribute('aria-pressed', 'true')
    fireEvent.click(screen.getByRole('button', { name: /Показать ещё/ }))

    expect(await screen.findByText((_, element) =>
      element?.tagName === 'CODE' && element.textContent === '198.51.100.20:1080')).toBeInTheDocument()
    expect(screen.getAllByText((_, element) =>
      element?.tagName === 'CODE' && element.textContent === '192.0.2.10:8080')).toHaveLength(1)
    expect(screen.queryByRole('button', { name: /Показать ещё/ })).not.toBeInTheDocument()
    expect(vi.mocked(fetch).mock.calls.some(([input]) =>
      String(input).includes('/api/v1/proxies/seek?pageSize=100&maxLatencyMs=2000&after=cursor-token'))).toBe(true)
  })

  it('ignores a stale catalog response after filters change', async () => {
    let resolveFirstPage!: (response: Response) => void
    const firstPage = new Promise<Response>(resolve => { resolveFirstPage = resolve })
    let catalogRequests = 0
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies/seek')) {
        catalogRequests++
        if (catalogRequests === 1) return firstPage
        return jsonResponse({
          items: [{
            host: '203.0.113.50', port: 1080, protocol: 'Socks5', url: 'socks5://203.0.113.50:1080',
            latencyMs: 180, successRate: 99, lastCheckedAt: '2026-08-09T10:00:00Z',
          }],
          pageSize: 100, hasMore: false, nextCursor: null,
        })
      }
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await waitFor(() => expect(catalogRequests).toBe(1))
    fireEvent.click(screen.getByRole('button', { name: 'SOCKS5' }))
    expect(await screen.findByText((_, element) =>
      element?.tagName === 'CODE' && element.textContent === '203.0.113.50:1080')).toBeInTheDocument()

    resolveFirstPage(jsonResponse({
      items: [{
        host: '192.0.2.99', port: 8080, protocol: 'Http', url: 'http://192.0.2.99:8080',
        latencyMs: 90, successRate: 100, lastCheckedAt: '2026-08-09T10:00:00Z',
      }],
      pageSize: 100, hasMore: false, nextCursor: null,
    }))
    await waitFor(() => expect(screen.queryByText((_, element) =>
      element?.tagName === 'CODE' && element.textContent === '192.0.2.99:8080')).not.toBeInTheDocument())
  })

  it('labels built-in sources and reserves deletion for custom sources', async () => {
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([
        {
          id: 'source-1', name: 'ProxyScrape HTTP', url: 'https://example.test/list.txt',
          defaultProtocol: 'Http', enabled: true, priority: 1, lastItemCount: 100,
          consecutiveFailures: 0, isBuiltIn: true, provider: 'ProxyScrape',
          providerIdentity: 'host:api.proxyscrape.com', catalogRank: 2,
        },
        {
          id: 'source-2', name: 'Собственный feed', url: 'https://custom.example.test/list.txt',
          defaultProtocol: 'Http', enabled: true, priority: 100, lastItemCount: 10,
          consecutiveFailures: 0, isBuiltIn: false,
        },
      ])
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

    expect(await screen.findByTitle(
      'Встроенный источник · ProxyScrape · host:api.proxyscrape.com · ранг 2')).toHaveTextContent('ProxyScrape')
    expect(screen.getAllByRole('button', { name: 'Удалить' })).toHaveLength(1)
  })

  it('shows operational diagnostics and refreshes them after a backup', async () => {
    let failDiagnostics = false
    const diagnostics = {
      serverTime: '2026-08-09T07:00:00Z',
      databaseBytes: 25 * 1024 * 1024,
      validationQueue: { total: 1000, leased: 12, neverChecked: 40, neverAttempted: 38, due: 75, scheduled: 925, repeatedlyFailing: 3, attemptsLastFiveMinutes: 1000, checkedLastFiveMinutes: 994, aliveLastFiveMinutes: 6, deferredLastFiveMinutes: 6, failedRunsLastFiveMinutes: 0, activeRuns: 1, concurrencyLimit: 800, batchSize: 1600, checksPerSecond: 34.4, estimatedDrainSeconds: 3, lastAttemptAt: '2026-08-09T06:59:55Z' },
      sourceCatalog: { lastAuditedOn: '2026-08-09', expectedSources: 81, presentSources: 81, enabledSources: 81, healthySources: 79, failingSources: 0, neverAuditedSources: 0, staleSources: 1, truncatedSources: 1, expectedProviders: 50, presentProviders: 50, enabledProviders: 50, isComplete: true, isHealthy: false },
      recentRuns: [{
        id: 'collection-1', startedAt: '2026-08-09T06:59:00Z', finishedAt: '2026-08-09T06:59:05Z',
        sourcesProcessed: 81, sourcesSucceeded: 81, sourcesFailed: 0, sourcesSkipped: 0,
        sourcesTruncated: 1, candidatesFound: 184005, candidateLimitReached: true,
        newProxies: 6377, status: 'completed',
      }],
      recentValidationRuns: [{
        id: 'validation-1', startedAt: '2026-08-09T06:59:30Z', finishedAt: '2026-08-09T06:59:40Z',
        claimed: 1000, checked: 994, alive: 6, deferred: 6, status: 'completed',
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
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], pageSize: 100, hasMore: false, nextCursor: null })
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
    expect(screen.getAllByText('6 живых', { exact: false })).toHaveLength(2)
    expect(screen.getByText('6 отложено', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('34,4/с', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('лимит 800 × 1 600', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('ETA 3 сек', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('1 000/1 000 попыток', { exact: false })).toBeInTheDocument()
    expect(screen.getByText('достигнут лимит')).toBeInTheDocument()
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('81/81')
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('50/50 провайдеров')
    expect(screen.getByLabelText('Состояние встроенного каталога')).toHaveTextContent('release-аудит 2026-08-09')
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
