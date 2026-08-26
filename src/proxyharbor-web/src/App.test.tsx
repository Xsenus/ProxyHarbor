import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
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
  successRate: 99.5, countryCode: 'US', lastCheckedAt: new Date().toISOString(),
  activeSince: new Date(Date.now() - 7_200_000).toISOString(), activeForSeconds: 7200,
}))

describe('ProxyHarbor UI', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/')
    sessionStorage.clear()
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies/countries')) return jsonResponse([{ code: 'US', count: 70 }, { code: 'DE', count: 7 }])
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
    const { container }=render(<App />)
    expect(await screen.findByRole('cell', { name: /^203\.0\.113\.1:8000$/ })).toBeInTheDocument()
    await waitFor(()=>expect(container.querySelector('.country-cell .country-flag.flag-us')).toBeInTheDocument())
    expect(screen.getAllByText('2 ч 0 мин').length).toBeGreaterThan(0)
    expect(screen.getByText('Страница 1 из 8 · Найдено: 77')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Следующая страница' })).toBeEnabled()
    expect(screen.getByRole('button', { name: '10' })).toHaveAttribute('aria-pressed', 'true')
    expect(screen.getByRole('spinbutton', { name: 'Номер страницы' })).toBeInTheDocument()
  })

  it('filters the catalog by countries through the styled multi-select', async () => {
    render(<App />)
    const trigger = await screen.findByRole('button', { name: 'Страны' })
    fireEvent.click(trigger)
    expect(screen.getByRole('dialog', { name: 'Фильтр по странам' })).toBeInTheDocument()
    const germany = screen.getByRole('checkbox', { name: /Германия/ })
    expect(germany).toHaveClass('ui-checkbox-input')
    expect(germany.nextElementSibling).toHaveClass('ui-checkbox-mark')
    expect(germany.parentElement?.querySelector('.country-flag.flag-de')).toBeInTheDocument()
    fireEvent.click(germany)
    expect(germany).toBeChecked()
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) => {
      const url = String(input)
      return url.includes('/api/v1/proxies?') && url.includes('country=DE')
    })).toBe(true))
    expect(screen.getByRole('button', { name: /Страны · 1/ })).toHaveAttribute('aria-expanded', 'true')
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
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({ items: [], page: 1, pageSize: 10, total: 0 })
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

  it('pages admin sources on the server with the shared catalog controls', async () => {
    window.history.replaceState({}, '', '/admin/sources')
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources?')) {
        const requestedPage = Number(new URL(url, 'https://example.test').searchParams.get('page') ?? 1)
        const source = {
          id: `source-${requestedPage}`, name: `Источник ${requestedPage}`, url: `https://example.com/${requestedPage}.txt`,
          defaultProtocol: 'Http', enabled: true, priority: 100, lastItemCount: 10,
          lastResultTruncated: false, consecutiveFailures: 0, isBuiltIn: false,
        }
        return jsonResponse({ items: [source], page: requestedPage, pageSize: 10, total: 25 })
      }
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    expect(await screen.findByText('Источник 1')).toBeInTheDocument()
    expect(screen.getByText('Страница 1 из 3 · Найдено: 25')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button', { name: 'Следующая страница' }))
    expect(await screen.findByText('Источник 2')).toBeInTheDocument()
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) =>
      String(input).includes('/api/v1/admin/sources?page=2&pageSize=10'))).toBe(true))
  })

  it('searches sources on the server and clears the applied filter', async () => {
    window.history.replaceState({}, '', '/admin/sources')
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources?')) {
        const search = new URL(url, 'https://example.test').searchParams.get('search')
        const items = search === 'proxyscrape' ? [{
          id: 'source-search', name: 'ProxyScrape HTTP', url: 'https://example.com/proxies.txt',
          defaultProtocol: 'Http', enabled: true, priority: 10, lastItemCount: 42,
          lastResultTruncated: false, consecutiveFailures: 0, isBuiltIn: true, provider: 'ProxyScrape',
        }] : []
        return jsonResponse({ items, page: 1, pageSize: 10, total: items.length })
      }
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    const search = await screen.findByRole('searchbox', { name: 'Поиск источников' })
    fireEvent.change(search, { target: { value: 'proxyscrape' } })
    fireEvent.submit(screen.getByRole('search'))

    expect(await screen.findByText('ProxyScrape HTTP')).toBeInTheDocument()
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) =>
      String(input).includes('page=1&pageSize=10&search=proxyscrape'))).toBe(true))

    const callsBeforeClear = vi.mocked(fetch).mock.calls.length
    fireEvent.click(screen.getByRole('button', { name: 'Очистить поиск' }))
    await waitFor(() => {
      const newSourceCalls = vi.mocked(fetch).mock.calls.slice(callsBeforeClear).filter(([input]) =>
        new URL(String(input), 'https://example.test').pathname.endsWith('/api/v1/admin/sources'))
      expect(newSourceCalls.length).toBeGreaterThan(0)
      expect(new URL(String(newSourceCalls.at(-1)?.[0]), 'https://example.test').searchParams.has('search')).toBe(false)
    })
  })

  it('shows proxy lifetime statistics and pages the protected inventory on the server', async () => {
    window.history.replaceState({}, '', '/admin/proxies')
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/proxies?')) return jsonResponse({
        items: [{ id:'proxy-1',host:'203.0.113.10',port:8080,protocol:'Http',status:'Alive',latencyMs:120,countryCode:'DE',isAnonymous:true,firstSeenAt:'2026-08-20T10:00:00Z',lastSeenAt:new Date().toISOString(),lastCheckedAt:new Date().toISOString(),firstAliveAt:'2026-08-20T11:00:00Z',lastAliveAt:new Date().toISOString(),currentAliveSince:'2026-08-25T10:00:00Z',activeForSeconds:93600,lastValidationDeferred:false,successfulChecks:12,failedChecks:1,consecutiveFailedChecks:0,successRate:92.3 }],
        page:1,pageSize:10,total:21,
        summary:{total:100,alive:21,freshAlive:20,staleAlive:1,pending:30,dead:49,everAlive:60,averageAliveLatencyMs:120,countries:2,longestActiveSeconds:93600},
        countries:[{code:'DE',count:12},{code:'US',count:8}],
      })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({ items: [], page: 1, pageSize: 10, total: 0 })
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:0,validationQueue:{total:100,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[]})
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    const { container }=render(<App />)
    expect(await screen.findByRole('heading', { name:'Прокси', level:1 })).toBeInTheDocument()
    expect(await screen.findByText('203.0.113.10:8080')).toBeInTheDocument()
    expect(screen.getByRole('button', { name:'Статус прокси' })).toHaveTextContent('Рабочие')
    expect(vi.mocked(fetch).mock.calls.some(([input]) => {
      const url = new URL(String(input), 'https://example.test')
      return url.pathname.endsWith('/api/v1/admin/proxies') && url.searchParams.get('status') === 'Alive'
    })).toBe(true)
    expect(screen.getAllByText('1 д 2 ч').length).toBeGreaterThan(0)
    expect(screen.getByText('Страница 1 из 3 · Найдено: 21')).toBeInTheDocument()
    expect(screen.getByRole('link', { name:/^Прокси/ })).toHaveAttribute('aria-current','page')
    expect(screen.getByLabelText('Страна прокси')).toBeInTheDocument()
    expect(container.querySelector('.admin-proxy-address .country-flag.flag-de')).toBeInTheDocument()
  })

  it('creates sources in a modal editor', async () => {
    window.history.replaceState({}, '', '/admin/sources')
    vi.mocked(fetch).mockImplementation(async (input, options) => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources?')) return jsonResponse({ items: [], page: 1, pageSize: 10, total: 0 })
      if (url.endsWith('/api/v1/admin/sources') && options?.method === 'POST') return jsonResponse({}, 201)
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    const addButton = await screen.findByRole('button', { name: 'Добавить источник' })
    await waitFor(() => expect(addButton).toBeEnabled())
    fireEvent.click(addButton)
    const dialog = screen.getByRole('dialog', { name: 'Добавить источник' })
    expect(within(dialog).queryByRole('combobox')).not.toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: 'Протокол источника' }))
    fireEvent.click(screen.getByRole('option', { name: 'HTTPS' }))
    fireEvent.change(within(dialog).getByLabelText('Название'), { target: { value: 'Новый feed' } })
    fireEvent.change(within(dialog).getByLabelText('HTTPS URL'), { target: { value: 'https://example.com/new.txt' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Добавить источник' }))

    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input, options]) => {
      if (!String(input).endsWith('/api/v1/admin/sources') || options?.method !== 'POST') return false
      const body=JSON.parse(String(options.body))
      return body.name === 'Новый feed' && body.protocol === 'Https'
    })).toBe(true))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
  })

  it('edits and deletes a custom source from the same modal', async () => {
    window.history.replaceState({}, '', '/admin/sources')
    const source = {
      id: 'source-edit', name: 'Редактируемый feed', url: 'https://example.com/old.txt',
      defaultProtocol: 'Http', enabled: true, priority: 100, lastItemCount: 10,
      lastResultTruncated: false, consecutiveFailures: 0, isBuiltIn: false,
    }
    vi.mocked(fetch).mockImplementation(async (input, options) => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources?')) return jsonResponse({ items: [source], page: 1, pageSize: 10, total: 1 })
      if (url.endsWith('/api/v1/admin/sources/source-edit') && ['PUT', 'DELETE'].includes(String(options?.method))) return new Response(null, { status: 204 })
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    fireEvent.click(await screen.findByRole('button', { name: 'Изменить' }))
    let dialog = screen.getByRole('dialog', { name: 'Редактировать источник' })
    fireEvent.change(within(dialog).getByLabelText('Название'), { target: { value: 'Обновлённый feed' } })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Сохранить изменения' }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input, options]) =>
      String(input).endsWith('/source-edit') && options?.method === 'PUT' &&
      JSON.parse(String(options.body)).name === 'Обновлённый feed')).toBe(true))

    fireEvent.click(await screen.findByRole('button', { name: 'Изменить' }))
    dialog = screen.getByRole('dialog', { name: 'Редактировать источник' })
    fireEvent.click(within(dialog).getByRole('button', { name: 'Удалить источник' }))
    expect(within(dialog).getByText('Удалить источник безвозвратно?')).toBeInTheDocument()
    fireEvent.click(within(dialog).getByRole('button', { name: /^Удалить$/ }))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input, options]) =>
      String(input).endsWith('/source-edit') && options?.method === 'DELETE')).toBe(true))
  })

  it('downloads and safely deletes a backup from the paged registry', async () => {
    window.history.replaceState({}, '', '/admin/backups')
    const backup = { id:'backup-1',startedAt:new Date().toISOString(),finishedAt:new Date().toISOString(),status:'completed',fileName:'proxyharbor-20260826-123456-1234.phbackup',sizeBytes:1024,telegramConfigured:true,sentToTelegram:true,available:true }
    let deleted = false
    vi.mocked(fetch).mockImplementation(async (input, options) => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({items:[],page:1,pageSize:10,total:0})
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:2048,validationQueue:{total:0,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[backup]})
      if (url.includes('/api/v1/admin/backups?')) return jsonResponse({items:deleted?[]:[backup],page:1,pageSize:10,total:deleted?0:1})
      if (url.endsWith('/api/v1/admin/backups/backup-1') && options?.method === 'DELETE') { deleted = true; return new Response(null,{status:204}) }
      return jsonResponse({title:'Unexpected request'},500)
    })

    render(<App/>)
    const download = await screen.findByRole('link',{name:'Скачать'})
    expect(download).toHaveAttribute('href','/api/v1/admin/backups/backup-1/download')
    fireEvent.click(screen.getByRole('button',{name:/Удалить proxyharbor/}))
    const dialog = screen.getByRole('dialog',{name:'Удалить резервную копию?'})
    fireEvent.click(within(dialog).getByRole('button',{name:'Удалить навсегда'}))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input, options]) =>
      String(input).endsWith('/api/v1/admin/backups/backup-1') && options?.method === 'DELETE')).toBe(true))
    await waitFor(() => expect(screen.queryByRole('dialog',{name:'Удалить резервную копию?'})).not.toBeInTheDocument())
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
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({ items: [], page: 1, pageSize: 10, total: 0 })
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    const pageHeading = await screen.findByRole('heading', { name: heading, level: 1 })
    expect(pageHeading.closest('.admin-page-heading')).toBeInTheDocument()
    expect(within(pageHeading.closest('.admin-page-heading')!).getByRole('link', { name: 'Панель управления' })).toHaveAttribute('href', '/admin')
    expect(screen.getByRole('link', { name: new RegExp(`^${heading}`) })).toHaveAttribute('aria-current', 'page')
  })

  it('loads users as a searchable server-side table with pagination and a separate editor', async () => {
    window.history.replaceState({}, '', '/admin/users')
    const user = {
      id: 'user-1', userName: 'client', email: 'client@example.test', displayName: 'Клиент',
      isActive: true, createdAt: new Date().toISOString(), lastLoginAt: new Date().toISOString(),
      roles: ['User', 'Subscriber'], subscription: { plan: 'pro', status: 'active', startedAt: new Date().toISOString() },
    }
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({items:[],page:1,pageSize:10,total:0})
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:0,validationQueue:{total:0,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[]})
      if (url.includes('/api/v1/admin/users?')) {
        const query = new URL(url, 'http://localhost').searchParams
        return jsonResponse({items:[{...user,id:`user-${query.get('page') ?? '1'}`}],page:Number(query.get('page') ?? 1),pageSize:Number(query.get('pageSize') ?? 10),total:34})
      }
      return jsonResponse({title:'Unexpected request'},500)
    })

    render(<App/>)
    expect(await screen.findByText('Страница 1 из 4 · Найдено: 34')).toBeInTheDocument()
    expect(screen.getByRole('heading',{name:'Пользователи'}).closest('.admin-page-heading')).toBeInTheDocument()
    expect(screen.getByRole('heading',{name:'Пользователи'}).closest('section')).toHaveClass('users-admin-section')
    expect(screen.getByRole('navigation',{name:'Быстрый переход по страницам'}).closest('section')).toHaveClass('users-registry')
    expect(screen.getByText('Последний вход')).toBeInTheDocument()
    fireEvent.click(screen.getByRole('button',{name:'Следующая страница'}))
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).includes('/api/v1/admin/users?') && String(input).includes('page=2'))).toBe(true))
    fireEvent.change(screen.getByRole('textbox',{name:'Поиск пользователей'}),{target:{value:'client@example.test'}})
    fireEvent.submit(screen.getByRole('textbox',{name:'Поиск пользователей'}).closest('form')!)
    await waitFor(() => expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).includes('search=client%40example.test'))).toBe(true))
    fireEvent.click(await screen.findByRole('button',{name:'Управлять'}))
    expect(screen.getByRole('dialog',{name:/Управление пользователем/})).toBeInTheDocument()
    expect(screen.getByRole('switch',{name:'Права администратора'})).toBeInTheDocument()
  })

  it('groups payment settings and opens each provider in its own dialog', async () => {
    window.history.replaceState({}, '', '/admin/payments')
    vi.mocked(fetch).mockImplementation(async input => {
      const url=String(input)
      if(url.includes('/api/v1/admin/sources'))return jsonResponse({items:[],page:1,pageSize:10,total:0})
      if(url.includes('/api/v1/admin/diagnostics'))return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:0,validationQueue:{total:0,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[]})
      if(url.endsWith('/api/v1/admin/payments'))return jsonResponse({enabled:false,products:[{code:'pro-30',enabled:true,name:'Pro',plan:'pro',durationDays:30,amountMinor:49900,currency:'RUB',description:'Pro'}],providers:['yookassa','yoomoney','cloudpayments','robokassa','tbank','stripe','cryptomus','nowpayments'].map(code=>({code,name:({yookassa:'ЮKassa',yoomoney:'ЮMoney',cloudpayments:'CloudPayments',robokassa:'Robokassa',tbank:'Т-Банк',stripe:'Stripe',cryptomus:'Cryptomus',nowpayments:'NOWPayments'} as Record<string,string>)[code],enabled:false,merchantId:'',publicId:'',testMode:false,secretConfigured:false,secondarySecretConfigured:false,ready:false,webhookUrl:`https://example.test/webhook/${code}`}))})
      if(url.includes('/api/v1/admin/payments/orders'))return jsonResponse({items:[],page:1,pageSize:10,total:0,summary:[]})
      return jsonResponse({title:'Unexpected request'},500)
    })
    render(<App/>);expect(await screen.findByRole('heading',{name:'Оплата'})).toBeInTheDocument();fireEvent.click(await screen.findByRole('tab',{name:'Провайдеры'}));expect(await screen.findByText('ЮMoney')).toBeInTheDocument();expect(screen.getByText('Cryptomus')).toBeInTheDocument();expect(screen.getByText('NOWPayments')).toBeInTheDocument();fireEvent.click((await screen.findAllByRole('button',{name:'Открыть настройки'}))[0]);expect(screen.getByRole('dialog',{name:'Настройки ЮKassa'})).toBeInTheDocument()
  })

  it('exposes Telegram configuration as a dedicated admin section', async () => {
    window.history.replaceState({}, '', '/admin/telegram')
    vi.mocked(fetch).mockImplementation(async input => {const url=String(input);if(url.includes('/api/v1/admin/sources'))return jsonResponse({items:[],page:1,pageSize:10,total:0});if(url.includes('/api/v1/admin/diagnostics'))return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:0,validationQueue:{total:0,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[]});if(url.endsWith('/api/v1/admin/telegram'))return jsonResponse({enabled:false,updateMode:'webhook',name:'ProxyHarbor',description:'Проверенные прокси и управление подпиской.',shortDescription:'Прокси и подписка',supportText:'Сообщение передано оператору.',proxyFileMaxItems:1000,webhookMaxConnections:20,productStars:{'pro-30':250},tokenConfigured:false,webhookUrl:'https://proxy.example/api/v1/telegram/webhook/bot',stats:{users:0,activeUsers30d:0,notificationsEnabled:0,blocked:0,paidOrders:0,starsRevenue:0,queued:0,failed:0}});if(url.endsWith('/api/v1/admin/payments'))return jsonResponse({enabled:true,products:[{code:'pro-30',enabled:true,name:'Pro',plan:'pro',durationDays:30,amountMinor:49900,currency:'RUB',description:'Pro'}],providers:[]});return jsonResponse({title:'Unexpected'},500)})
    render(<App/>)
    expect(await screen.findByRole('heading',{name:'Telegram-бот'})).toBeInTheDocument()
    expect(screen.getByRole('link',{name:'Telegram-бот'})).toHaveAttribute('aria-current','page')
    fireEvent.click(screen.getByRole('tab',{name:'Настройки'}))
    expect(await screen.findByPlaceholderText('123456:ABC…')).toHaveAttribute('autocomplete','new-password')
    expect(screen.getByDisplayValue('250')).toBeInTheDocument()
    const deliveryMode=screen.getByRole('button',{name:'Выбор значения'})
    fireEvent.click(deliveryMode)
    expect(screen.getByRole('listbox',{name:'Выбор значения'})).toBeInTheDocument()
    fireEvent.click(screen.getByRole('option',{name:'Long polling'}))
    expect(deliveryMode).toHaveTextContent('Long polling')
    expect(deliveryMode).toHaveAttribute('aria-expanded','false')
  })

  it('shows subscriptions and opens auditable manual extension', async () => {
    window.history.replaceState({}, '', '/admin/subscriptions')
    vi.mocked(fetch).mockImplementation(async input => {const url=String(input);if(url.includes('/api/v1/admin/sources'))return jsonResponse({items:[],page:1,pageSize:10,total:0});if(url.includes('/api/v1/admin/diagnostics'))return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:0,validationQueue:{total:0,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[]});if(url.includes('/api/v1/admin/subscriptions'))return jsonResponse({items:[{id:'subscription-1',userId:'user-1',userName:'client',email:'client@example.test',plan:'pro',status:'active',startedAt:new Date().toISOString(),expiresAt:new Date().toISOString(),updatedAt:new Date().toISOString()}],page:1,pageSize:10,total:1,summary:{active:1,trialing:0,suspended:0,expiringSoon:1}});return jsonResponse({title:'Unexpected'},500)})
    render(<App/>);expect(await screen.findByRole('heading',{name:'Подписки'})).toBeInTheDocument();fireEvent.click(await screen.findByRole('button',{name:'Управлять'}));expect(screen.getByText(/Каждое изменение сохраняется/)).toBeInTheDocument();expect(screen.getByRole('button',{name:'+30 дней'})).toBeInTheDocument()
  })

  it('shows aggregated IP traffic and creates block rules in a modal', async () => {
    window.history.replaceState({}, '', '/admin/access')
    vi.mocked(fetch).mockImplementation(async input => {
      const url=String(input)
      if(url.includes('/api/v1/admin/sources'))return jsonResponse({items:[],page:1,pageSize:10,total:0})
      if(url.includes('/api/v1/admin/diagnostics'))return jsonResponse({serverTime:new Date().toISOString(),databaseBytes:0,validationQueue:{total:0,due:0},recentRuns:[],recentValidationRuns:[],recentBackups:[]})
      if(url.includes('/api/v1/admin/access/visitors'))return jsonResponse({items:[{ipAddress:'198.51.100.20',displayName:'Посетитель',pageViews:7,pages:3,firstSeenAt:new Date().toISOString(),lastSeenAt:new Date().toISOString()}],page:1,pageSize:10,total:1,retentionDays:90,summary:{pageViews:7,uniqueVisitors:1,authenticatedVisitors:0,active24Hours:1}})
      if(url.includes('/api/v1/admin/access'))return jsonResponse({items:[{ipAddress:'203.0.113.10',requests:123,blockedRequests:0,proxyItems:1200,bytesSent:2048,lastSeenAt:new Date().toISOString()}],page:1,pageSize:10,total:1,rules:[],summary:{requests:123,proxyItems:1200,uniqueIps:1,activeRules:0}})
      return jsonResponse({title:'Unexpected'},500)
    })
    render(<App/>)
    expect(await screen.findByRole('heading',{name:'Доступ и IP'})).toBeInTheDocument()
    expect(await screen.findByText('203.0.113.10')).toBeInTheDocument()
    const trafficTab=screen.getByRole('tab',{name:'Выдача и блокировки'})
    const visitorsTab=screen.getByRole('tab',{name:'Посетители сайта'})
    expect(trafficTab).toHaveAttribute('aria-selected','true')
    expect(screen.queryByRole('heading',{name:'Посетители сайта'})).not.toBeInTheDocument()
    visitorsTab.focus()
    fireEvent.keyDown(visitorsTab,{key:'Home'})
    expect(trafficTab).toHaveFocus()
    fireEvent.keyDown(trafficTab,{key:'ArrowRight'})
    expect(visitorsTab).toHaveFocus()
    expect(visitorsTab).toHaveAttribute('aria-selected','true')
    expect(window.location.search).toBe('?tab=visitors')
    expect(await screen.findByRole('heading',{name:'Посетители сайта'})).toBeInTheDocument()
    expect(await screen.findByText('198.51.100.20')).toBeInTheDocument()
    expect(screen.getByText(/удаляются через 90 дней/)).toBeInTheDocument()
    fireEvent.click(trafficTab)
    expect(window.location.search).toBe('')
    fireEvent.click(screen.getByRole('button',{name:'Добавить блокировку'}))
    expect(screen.getByRole('heading',{name:'Новая блокировка'})).toBeInTheDocument()
  })
})

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve(new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
}

function jsonResponseValue(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}
