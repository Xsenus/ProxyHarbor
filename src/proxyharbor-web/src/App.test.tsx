import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'

const stats = {
  alive: 77, staleAlive: 0, pending: 1, dead: 2, dueForCheck: 3,
  checksInProgress: 1, scheduledChecks: 4, averageLatencyMs: 321,
  sources: 98, failingSources: 0, repeatedlyFailingSources: 0,
  truncatedSources: 0, byProtocol: [{ protocol: 'Http', count: 77 }],
}

const proxies = Array.from({ length: 10 }, (_, index) => ({
  host: `203.0.113.${index + 1}`, port: 8000 + index, protocol: 'Http',
  url: `http://203.0.113.${index + 1}:${8000 + index}`, latencyMs: 100 + index,
  successRate: 99.5, lastCheckedAt: new Date().toISOString(),
  activeSince: new Date(Date.now() - 7_200_000).toISOString(), activeForSeconds: 7200,
}))

describe('ProxyHarbor UI', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/')
    sessionStorage.clear()
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: proxies, page: 1, pageSize: 10, total: 77 })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    }))
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('does not expose provider catalog or request its public endpoint', async () => {
    render(<App />)
    await screen.findByText('система активна')
    expect(screen.queryByText(/независимых провайдеров/)).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Источники' })).not.toBeInTheDocument()
    expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).includes('/api/v1/sources'))).toBe(false)
  })

  it('renders active duration and server pagination metadata', async () => {
    render(<App />)
    expect(await screen.findByRole('cell', { name: /^203\.0\.113\.1:8000$/ })).toBeInTheDocument()
    expect(screen.getAllByText('2 ч 0 мин').length).toBeGreaterThan(0)
    expect(screen.getByText('Страница 1 из 8 · Найдено: 77')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Следующая страница' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '10' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('spinbutton', { name: 'Номер страницы' })).toBeInTheDocument()
  })

  it('requests the selected page and page size from the server', async () => {
    render(<App />)
    await screen.findByText('Страница 1 из 8 · Найдено: 77')
    fireEvent.click(screen.getByRole('button', { name: '2' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) => {
      const url = String(input)
      return url.includes('/api/v1/proxies?') && url.includes('page=2') && url.includes('pageSize=10')
    })).toBe(true))
  })

  it('shows the compact quick jump only for a long catalog', async () => {
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) {
        const requestedPage = Number(new URL(url, 'http://localhost').searchParams.get('page')) || 1
        return jsonResponse({ items: proxies, page: requestedPage, pageSize: 10, total: 250 })
      }
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    const pageInput = await screen.findByRole('spinbutton', { name: 'Номер страницы' })
    expect(screen.queryByText('Перейти:')).not.toBeInTheDocument()
    fireEvent.change(pageInput, { target: { value: '8' } })
    fireEvent.click(screen.getByRole('button', { name: 'Перейти на страницу' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) =>
      String(input).includes('page=8'))).toBe(true))
  })

  it('resets to page one when page size changes', async () => {
    render(<App />)
    await screen.findByText('Страница 1 из 8 · Найдено: 77')
    fireEvent.click(screen.getByRole('button', { name: '50' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) => {
      const url = String(input)
      return url.includes('/api/v1/proxies?') && url.includes('page=1') && url.includes('pageSize=50')
    })).toBe(true))
  })

  it('renders a dedicated login page without the public catalog', () => {
    window.history.replaceState({}, '', '/admin/login')
    render(<App />)
    expect(screen.getByRole('heading', { name: 'Вход в ProxyHarbor' })).toBeInTheDocument()
    expect(screen.getByLabelText('Логин или email')).toHaveAttribute('autocomplete', 'username')
    expect(screen.getByLabelText('Пароль')).toHaveAttribute('autocomplete', 'current-password')
    expect(screen.queryByPlaceholderText('X-Admin-Key')).not.toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Лучшие прямо сейчас' })).not.toBeInTheDocument()
    expect(vi.mocked(fetch)).not.toHaveBeenCalled()
  })

  it('submits login and password to the session endpoint without an admin-key header', async () => {
    window.history.replaceState({}, '', '/admin/login')
    vi.mocked(fetch).mockResolvedValue(jsonResponseValue({ title: 'Неверный логин или пароль' }, 401))
    render(<App />)
    fireEvent.change(screen.getByLabelText('Логин или email'), { target: { value: 'admin' } })
    fireEvent.change(screen.getByLabelText('Пароль'), { target: { value: 'wrong-password' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Неверный логин или пароль')
    const [, options] = vi.mocked(fetch).mock.calls[0]
    expect(options?.credentials).toBe('include')
    expect(new Headers(options?.headers).has('X-Admin-Key')).toBe(false)
    expect(JSON.parse(String(options?.body))).toEqual({ username: 'admin', password: 'wrong-password' })
  })

  it.each([
    ['/register', 'Создать аккаунт'],
    ['/forgot-password', 'Восстановить пароль'],
    ['/reset-password?email=user%40example.com&token=token', 'Новый пароль'],
  ])('renders the %s account flow without public content', (path, heading) => {
    window.history.replaceState({}, '', path)
    render(<App />)
    expect(screen.getByRole('heading', { name: heading })).toBeInTheDocument()
    expect(screen.queryByRole('heading', { name: 'Лучшие прямо сейчас' })).not.toBeInTheDocument()
  })

  it('renders a dedicated authenticated admin page without public content', async () => {
    window.history.replaceState({}, '', '/admin')
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([])
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })
    render(<App />)
    expect(await screen.findByRole('heading', { name: 'Обзор' })).toBeInTheDocument()
    expect(screen.getByRole('navigation', { name: 'Разделы админ-панели' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Обзор' })).toHaveAttribute('aria-current', 'page')
    expect(screen.getByRole('link', { name: 'Операции' })).toHaveAttribute('href', '/admin/operations')
    expect(screen.getByRole('link', { name: /Источники/ })).toHaveAttribute('href', '/admin/sources')
    expect(screen.getByRole('link', { name: 'Резервные копии' })).toHaveAttribute('href', '/admin/backups')
    expect(screen.queryByRole('heading', { name: 'Лучшие прямо сейчас' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Пароль')).not.toBeInTheDocument()
    const adminCalls = vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/api/v1/admin/'))
    expect(adminCalls.length).toBeGreaterThan(0)
    expect(adminCalls.every(([, options]) => options?.credentials === 'include')).toBe(true)
    expect(adminCalls.every(([, options]) => !new Headers(options?.headers).has('X-Admin-Key'))).toBe(true)
  })

  it.each([
    ['/admin/operations', 'Операции'],
    ['/admin/sources', 'Источники'],
    ['/admin/backups', 'Резервные копии'],
    ['/admin/users', 'Пользователи'],
  ])('opens the %s admin section as a separate workspace', async (path, heading) => {
    window.history.replaceState({}, '', path)
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([])
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    expect(await screen.findByRole('heading', { name: heading, level: 1 })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: new RegExp(`^${heading}`) })).toHaveAttribute('aria-current', 'page')
  })
})

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
}

function jsonResponseValue(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}
