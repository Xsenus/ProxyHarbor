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
    vi.unstubAllGlobals()
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

  it('shows operational diagnostics and refreshes them after a backup', async () => {
    let failDiagnostics = false
    const diagnostics = {
      serverTime: '2026-08-09T07:00:00Z',
      databaseBytes: 25 * 1024 * 1024,
      validationQueue: { total: 1000, leased: 12, neverChecked: 40, due: 75, scheduled: 925, repeatedlyFailing: 3 },
      recentRuns: [{
        id: 'collection-1', startedAt: '2026-08-09T06:59:00Z', finishedAt: '2026-08-09T06:59:05Z',
        sourcesProcessed: 81, sourcesSucceeded: 81, sourcesFailed: 0, sourcesSkipped: 0,
        candidatesFound: 184005, newProxies: 6377, status: 'completed',
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
  })
})

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}
