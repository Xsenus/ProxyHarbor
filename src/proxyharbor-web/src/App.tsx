import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Activity, ArrowDownToLine, Check, Clock3, Database, Gauge, KeyRound, Network, Play, RefreshCw, Server, ShieldCheck, Wifi, X } from 'lucide-react'

type Protocol = 'Http' | 'Https' | 'Socks4' | 'Socks5'
type Proxy = { host: string; port: number; protocol: Protocol; url: string; latencyMs: number; successRate: number; exitIp?: string; lastCheckedAt: string }
type CursorPage<T> = { items: T[]; pageSize: number; hasMore: boolean; nextCursor?: string | null }
type Stats = { alive: number; staleAlive: number; pending: number; dead: number; dueForCheck: number; scheduledChecks: number; averageLatencyMs: number | null; sources: number; failingSources: number; repeatedlyFailingSources: number; truncatedSources: number; byProtocol: { protocol: Protocol; count: number }[]; lastRun?: { startedAt: string; candidatesFound: number; newProxies: number; sourcesTruncated: number; candidateLimitReached: boolean; status: string } }
type Source = { id: string; name: string; url: string; defaultProtocol: Protocol; enabled: boolean; priority: number; lastItemCount: number; lastResultTruncated: boolean; lastFetchedAt?: string; lastSucceededAt?: string; lastContentFetchedAt?: string; nextFetchAt?: string; consecutiveFailures: number; lastError?: string; isBuiltIn: boolean; provider?: string; providerIdentity?: string; catalogRank?: number }
type CollectionRun = { id: string; startedAt: string; finishedAt?: string; sourcesProcessed: number; sourcesSucceeded: number; sourcesFailed: number; sourcesSkipped: number; sourcesTruncated: number; candidatesFound: number; candidateLimitReached: boolean; newProxies: number; status: string; error?: string }
type ValidationRun = { id: string; startedAt: string; finishedAt?: string; claimed: number; checked: number; alive: number; deferred: number; status: string; error?: string }
type BackupRun = { id: string; startedAt: string; finishedAt?: string; status: string; fileName?: string; sizeBytes: number; telegramConfigured: boolean; sentToTelegram: boolean; error?: string }
type SourceCatalogSnapshot = { lastAuditedOn: string; expectedSources: number; presentSources: number; enabledSources: number; healthySources: number; failingSources: number; neverAuditedSources: number; staleSources: number; truncatedSources: number; expectedProviders: number; presentProviders: number; enabledProviders: number; isComplete: boolean; isHealthy: boolean }
type PublicSourceFeed = { rank: number; name: string; url: string; protocol: Protocol }
type PublicSourceProvider = { rank: number; name: string; protocols: Protocol[]; feeds: PublicSourceFeed[] }
type PublicSourceCatalog = { lastAuditedOn: string; feedCount: number; providerCount: number; providers: PublicSourceProvider[] }
type Diagnostics = {
  serverTime: string
  databaseBytes: number
  validationQueue?: { total: number; leased: number; neverChecked: number; neverAttempted: number; due: number; scheduled: number; repeatedlyFailing: number; attemptsLastFiveMinutes: number; checkedLastFiveMinutes: number; aliveLastFiveMinutes: number; deferredLastFiveMinutes: number; failedRunsLastFiveMinutes: number; activeRuns: number; concurrencyLimit: number; batchSize: number; checksPerSecond: number; estimatedDrainSeconds?: number; lastAttemptAt?: string }
  sourceCatalog?: SourceCatalogSnapshot
  recentRuns: CollectionRun[]
  recentValidationRuns?: ValidationRun[]
  recentBackups: BackupRun[]
}

const API = import.meta.env.VITE_API_URL ?? ''
const APP_VERSION = import.meta.env.VITE_APP_VERSION ?? '0.0.0-local'
const protocols: Protocol[] = ['Http', 'Https', 'Socks4', 'Socks5']
const adminKeyStorageName = 'proxyharbor-admin-key'

function readStoredAdminKey() {
  try { return sessionStorage.getItem(adminKeyStorageName) ?? '' }
  catch { return '' }
}

function storeAdminKey(value: string) {
  try { sessionStorage.setItem(adminKeyStorageName, value) }
  catch { /* Storage может быть запрещён политикой браузера; in-memory session остаётся рабочей. */ }
}

function removeStoredAdminKey() {
  try { sessionStorage.removeItem(adminKeyStorageName) }
  catch { /* Локальное React-состояние очищается независимо от доступности Storage API. */ }
}

function isAbortError(reason: unknown) {
  return reason instanceof Error && reason.name === 'AbortError'
}

/** Основная панель: публичный каталог и компактное администрирование в одном интерфейсе. */
export default function App() {
  const [stats, setStats] = useState<Stats | null>(null)
  const [proxies, setProxies] = useState<Proxy[]>([])
  const [protocol, setProtocol] = useState<Protocol | 'All'>('All')
  const [maxLatency, setMaxLatency] = useState(2000)
  const [loading, setLoading] = useState(true)
  const [loadingMore, setLoadingMore] = useState(false)
  const [hasMore, setHasMore] = useState(false)
  const [nextCursor, setNextCursor] = useState<string | null>(null)
  const [apiError, setApiError] = useState('')
  const [sourceCatalog, setSourceCatalog] = useState<PublicSourceCatalog | null>(null)
  const [sourceCatalogError, setSourceCatalogError] = useState('')
  const [sourceCatalogLoading, setSourceCatalogLoading] = useState(false)
  const [adminOpen, setAdminOpen] = useState(false)
  const [adminKey, setAdminKey] = useState(readStoredAdminKey)
  const [adminAuthenticated, setAdminAuthenticated] = useState(false)
  const [adminError, setAdminError] = useState('')
  const [sources, setSources] = useState<Source[]>([])
  const [diagnostics, setDiagnostics] = useState<Diagnostics | null>(null)
  const [adminLoading, setAdminLoading] = useState(false)
  const [action, setAction] = useState('')
  const [sourceBusy, setSourceBusy] = useState('')
  const [sourceDraft, setSourceDraft] = useState<{name: string; url: string; protocol: Protocol; priority: number}>({ name: '', url: '', protocol: 'Http', priority: 100 })
  const lastAdminTriggerRef = useRef<HTMLButtonElement>(null)
  const adminDialogRef = useRef<HTMLElement>(null)
  const adminKeyRef = useRef<HTMLInputElement>(null)
  const firstAdminActionRef = useRef<HTMLButtonElement>(null)
  const focusAdminActionAfterLoginRef = useRef(false)
  const autoLoginAttemptedRef = useRef(false)
  const extendedCatalogRef = useRef(false)
  const catalogRequestIdRef = useRef(0)
  const publicRequestIdRef = useRef(0)
  const publicAbortRef = useRef<AbortController | null>(null)
  const loadMoreAbortRef = useRef<AbortController | null>(null)
  const sourceCatalogRequestIdRef = useRef(0)
  const sourceCatalogAbortRef = useRef<AbortController | null>(null)
  const adminRequestIdRef = useRef(0)
  const adminAbortRef = useRef<AbortController | null>(null)
  const adminSessionIdRef = useRef(0)
  const adminMutationAbortRefs = useRef(new Set<AbortController>())

  const cancelPublicRequests = useCallback(() => {
    publicRequestIdRef.current++
    catalogRequestIdRef.current++
    publicAbortRef.current?.abort()
    loadMoreAbortRef.current?.abort()
  }, [])

  const loadSourceCatalog = useCallback(async () => {
    const requestId = ++sourceCatalogRequestIdRef.current
    sourceCatalogAbortRef.current?.abort()
    const controller = new AbortController()
    sourceCatalogAbortRef.current = controller
    setSourceCatalogLoading(true)
    try {
      const response = await fetch(`${API}/api/v1/sources`, { signal: controller.signal })
      if (!response.ok) throw new Error('Каталог источников временно недоступен')
      const snapshot = await response.json() as PublicSourceCatalog
      if (requestId !== sourceCatalogRequestIdRef.current) return
      setSourceCatalog(snapshot)
      setSourceCatalogError('')
    } catch (reason) {
      if (!isAbortError(reason) && requestId === sourceCatalogRequestIdRef.current)
        setSourceCatalogError(reason instanceof Error ? reason.message : 'Каталог источников временно недоступен')
    } finally {
      if (requestId === sourceCatalogRequestIdRef.current) {
        sourceCatalogAbortRef.current = null
        setSourceCatalogLoading(false)
      }
    }
  }, [])

  const cancelSourceCatalogRequest = useCallback(() => {
    sourceCatalogRequestIdRef.current++
    sourceCatalogAbortRef.current?.abort()
  }, [])

  useEffect(() => {
    void loadSourceCatalog()
    return cancelSourceCatalogRequest
  }, [loadSourceCatalog, cancelSourceCatalogRequest])

  /** Обновляет статистику и, при необходимости, первую keyset-страницу каталога. */
  const load = useCallback(async (includeCatalog = true) => {
    const requestId = ++publicRequestIdRef.current
    const catalogRequestId = includeCatalog ? ++catalogRequestIdRef.current : 0
    publicAbortRef.current?.abort()
    const controller = new AbortController()
    publicAbortRef.current = controller
    try {
      const query = new URLSearchParams({ pageSize: '100', maxLatencyMs: String(maxLatency) })
      if (protocol !== 'All') query.set('protocol', protocol)
      const [statsResponse, proxyResponse] = await Promise.all([
        fetch(`${API}/api/v1/stats`, { signal: controller.signal }),
        includeCatalog ? fetch(`${API}/api/v1/proxies/seek?${query}`, { signal: controller.signal }) : Promise.resolve(null),
      ])
      if (!statsResponse.ok || (proxyResponse && !proxyResponse.ok)) throw new Error('API пока недоступен')
      const statsSnapshot = await statsResponse.json()
      if (requestId !== publicRequestIdRef.current) return
      setStats(statsSnapshot)
      if (proxyResponse && catalogRequestId === catalogRequestIdRef.current) {
        const page = await proxyResponse.json() as CursorPage<Proxy>
        setProxies(page.items)
        setHasMore(page.hasMore)
        setNextCursor(page.nextCursor ?? null)
      }
      setApiError('')
    } catch (reason) {
      if (!isAbortError(reason) && requestId === publicRequestIdRef.current &&
          (!includeCatalog || catalogRequestId === catalogRequestIdRef.current)) {
        setApiError(reason instanceof Error ? reason.message : 'Ошибка загрузки')
      }
    } finally {
      if (requestId === publicRequestIdRef.current) {
        publicAbortRef.current = null
        setLoading(false)
      }
    }
  }, [protocol, maxLatency])

  useEffect(() => {
    // Смена фильтра начинает новый обход; старые ответы больше не могут заменить его результаты.
    extendedCatalogRef.current = false
    let stopped = false
    let refreshTimer: number | undefined
    const refresh = async () => {
      await load(!extendedCatalogRef.current)
      if (!stopped) refreshTimer = window.setTimeout(() => void refresh(), 15_000)
    }
    void refresh()
    return () => {
      stopped = true
      if (refreshTimer !== undefined) window.clearTimeout(refreshTimer)
      cancelPublicRequests()
    }
  }, [load, cancelPublicRequests])

  /** Добавляет следующую страницу, не выполняя дорожающий OFFSET и не дублируя изменившиеся строки. */
  const loadMore = async () => {
    if (!nextCursor || loadingMore) return
    extendedCatalogRef.current = true
    setLoadingMore(true)
    const catalogRequestId = catalogRequestIdRef.current
    loadMoreAbortRef.current?.abort()
    const controller = new AbortController()
    loadMoreAbortRef.current = controller
    try {
      const query = new URLSearchParams({ pageSize: '100', maxLatencyMs: String(maxLatency), after: nextCursor })
      if (protocol !== 'All') query.set('protocol', protocol)
      const response = await fetch(`${API}/api/v1/proxies/seek?${query}`, { signal: controller.signal })
      if (!response.ok) throw new Error(await responseMessage(response, 'Не удалось загрузить следующую страницу'))
      const page = await response.json() as CursorPage<Proxy>
      if (catalogRequestId !== catalogRequestIdRef.current) return
      setProxies(current => {
        const known = new Set(current.map(proxy => proxy.url))
        return [...current, ...page.items.filter(proxy => !known.has(proxy.url))]
      })
      setHasMore(page.hasMore)
      setNextCursor(page.nextCursor ?? null)
      setApiError('')
    } catch (reason) {
      if (!isAbortError(reason) && catalogRequestId === catalogRequestIdRef.current) {
        setApiError(reason instanceof Error ? reason.message : 'Ошибка загрузки')
      }
    }
    finally {
      if (loadMoreAbortRef.current === controller) {
        loadMoreAbortRef.current = null
        setLoadingMore(false)
      }
    }
  }

  /** Инвалидирует предыдущий cursor до запуска запроса с новым фильтром. */
  const changeProtocol = (value: Protocol | 'All') => {
    if (value === protocol) return
    catalogRequestIdRef.current++
    loadMoreAbortRef.current?.abort()
    extendedCatalogRef.current = false
    setLoading(true)
    setHasMore(false)
    setNextCursor(null)
    setProtocol(value)
  }

  const changeMaxLatency = (value: number) => {
    if (value === maxLatency) return
    catalogRequestIdRef.current++
    loadMoreAbortRef.current?.abort()
    extendedCatalogRef.current = false
    setLoading(true)
    setHasMore(false)
    setNextCursor(null)
    setMaxLatency(value)
  }

  const loadAdminData = useCallback(async (focusFirstAction = false) => {
    const requestId = ++adminRequestIdRef.current
    adminAbortRef.current?.abort()
    if (!adminKey) {
      setAdminAuthenticated(false)
      setAdminLoading(false)
      return
    }
    const controller = new AbortController()
    adminAbortRef.current = controller
    setAdminLoading(true)
    try {
      const requestOptions = { headers: { 'X-Admin-Key': adminKey }, signal: controller.signal }
      const [sourcesResponse, diagnosticsResponse] = await Promise.all([
        fetch(`${API}/api/v1/admin/sources`, requestOptions),
        fetch(`${API}/api/v1/admin/diagnostics`, requestOptions),
      ])
      if (requestId !== adminRequestIdRef.current) return
      const unauthorizedResponse = [sourcesResponse, diagnosticsResponse].find(response => response.status === 401)
      if (unauthorizedResponse) {
        removeStoredAdminKey()
        setAdminAuthenticated(false)
        setSources([])
        setDiagnostics(null)
        throw new Error(await responseMessage(unauthorizedResponse, 'Неверный ключ администратора'))
      }
      if (!sourcesResponse.ok) throw new Error(await responseMessage(sourcesResponse, 'Неверный ключ администратора'))
      if (!diagnosticsResponse.ok) throw new Error(await responseMessage(diagnosticsResponse, 'Диагностика недоступна'))
      const [sourceRows, diagnosticSnapshot] = await Promise.all([sourcesResponse.json(), diagnosticsResponse.json()])
      if (requestId !== adminRequestIdRef.current) return
      storeAdminKey(adminKey)
      setSources(sourceRows)
      setDiagnostics(diagnosticSnapshot)
      setAdminAuthenticated(true)
      setAdminError('')
      // Кнопка входа на время запроса disabled, поэтому браузер теряет focus.
      // Effect ниже ждёт React commit и переводит его в начало рабочей зоны.
      focusAdminActionAfterLoginRef.current = focusFirstAction
    } catch (reason) {
      if (!isAbortError(reason) && requestId === adminRequestIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Не удалось открыть административную консоль')
    } finally {
      if (requestId === adminRequestIdRef.current) {
        adminAbortRef.current = null
        setAdminLoading(false)
      }
    }
  }, [adminKey])

  useEffect(() => {
    if (!adminAuthenticated || !focusAdminActionAfterLoginRef.current) return
    focusAdminActionAfterLoginRef.current = false
    firstAdminActionRef.current?.focus()
  }, [adminAuthenticated])

  useEffect(() => {
    if (!adminOpen) {
      autoLoginAttemptedRef.current = false
      return
    }
    if (autoLoginAttemptedRef.current) return
    autoLoginAttemptedRef.current = true
    if (!adminKey) return
    const initialLoad = window.setTimeout(() => void loadAdminData(true), 0)
    return () => window.clearTimeout(initialLoad)
  }, [adminOpen, adminKey, loadAdminData])

  useEffect(() => {
    if (!adminOpen || !adminAuthenticated) return
    let stopped = false
    let refreshTimer = window.setTimeout(async function refresh() {
      await loadAdminData()
      if (!stopped) refreshTimer = window.setTimeout(refresh, 15_000)
    }, 15_000)
    return () => {
      stopped = true
      window.clearTimeout(refreshTimer)
    }
  }, [adminOpen, adminAuthenticated, loadAdminData])

  const openAdmin = (event: React.MouseEvent<HTMLButtonElement>) => {
    lastAdminTriggerRef.current = event.currentTarget
    setAdminOpen(true)
  }
  /** Отменяет все чтения и мутации, принадлежавшие предыдущей admin-сессии. */
  const invalidateAdminSession = useCallback(() => {
    adminSessionIdRef.current++
    adminRequestIdRef.current++
    adminAbortRef.current?.abort()
    adminMutationAbortRefs.current.forEach(controller => controller.abort())
    adminMutationAbortRefs.current.clear()
  }, [])
  useEffect(() => () => invalidateAdminSession(), [invalidateAdminSession])
  const closeAdmin = useCallback(() => {
    invalidateAdminSession()
    setAdminLoading(false)
    setAction('')
    setSourceBusy('')
    setAdminOpen(false)
  }, [invalidateAdminSession])
  const logoutAdmin = useCallback(() => {
    invalidateAdminSession()
    removeStoredAdminKey()
    setAdminKey('')
    setAdminAuthenticated(false)
    setAdminError('')
    setSources([])
    setDiagnostics(null)
    setAction('')
    setSourceBusy('')
    window.requestAnimationFrame(() => adminKeyRef.current?.focus())
  }, [invalidateAdminSession])

  const changeAdminKey = (value: string) => {
    invalidateAdminSession()
    setAdminLoading(false)
    setAdminKey(value)
    setAdminAuthenticated(false)
    setAdminError('')
    setSources([])
    setDiagnostics(null)
  }

  useEffect(() => {
    if (!adminOpen) return
    const previousFocus = document.activeElement instanceof HTMLElement && document.activeElement !== document.body
      ? document.activeElement
      : lastAdminTriggerRef.current
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    window.requestAnimationFrame(() => adminKeyRef.current?.focus())
    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        event.preventDefault()
        closeAdmin()
        return
      }
      if (event.key !== 'Tab' || !adminDialogRef.current) return
      const focusable = [...adminDialogRef.current.querySelectorAll<HTMLElement>('button:not([disabled]), input:not([disabled]), select:not([disabled]), a[href]')]
        .filter(element => !element.hidden && element.getAttribute('aria-hidden') !== 'true')
      if (focusable.length === 0) return
      const first = focusable[0]
      const last = focusable[focusable.length - 1]
      if (!adminDialogRef.current.contains(document.activeElement)) {
        event.preventDefault()
        ;(event.shiftKey ? last : first).focus()
      } else if (event.shiftKey && document.activeElement === first) {
        event.preventDefault()
        last.focus()
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault()
        first.focus()
      }
    }
    document.addEventListener('keydown', handleKeyDown)
    return () => {
      document.removeEventListener('keydown', handleKeyDown)
      document.body.style.overflow = previousOverflow
      previousFocus?.focus()
    }
  }, [adminOpen, closeAdmin])

  const runAdminAction = async (name: 'collect' | 'validate' | 'backup') => {
    if (!adminAuthenticated || action || sourceBusy) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    setAction(name)
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/${name}`, { method: 'POST', headers: { 'X-Admin-Key': adminKey }, signal: controller.signal })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { removeStoredAdminKey(); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Административная операция не выполнена'))
      }
      await Promise.all([load(), loadAdminData()])
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Административная операция не выполнена')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setAction('')
    }
  }

  const saveSource = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!adminAuthenticated || action || sourceBusy) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    setSourceBusy('new')
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/sources`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
        body: JSON.stringify({ ...sourceDraft, enabled: true }),
        signal: controller.signal,
      })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { removeStoredAdminKey(); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Не удалось добавить источник'))
      }
      setSourceDraft({ name: '', url: '', protocol: 'Http', priority: 100 })
      await loadAdminData()
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Не удалось добавить источник')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setSourceBusy('')
    }
  }

  const toggleSource = async (source: Source) => {
    if (!adminAuthenticated || action || sourceBusy) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    setSourceBusy(source.id)
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
        body: JSON.stringify({ name: source.name, url: source.url, protocol: source.defaultProtocol, priority: source.priority, enabled: !source.enabled }),
        signal: controller.signal,
      })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { removeStoredAdminKey(); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Не удалось изменить состояние источника'))
      }
      await loadAdminData()
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Не удалось изменить состояние источника')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setSourceBusy('')
    }
  }

  const removeSource = async (source: Source) => {
    if (!adminAuthenticated || action || sourceBusy) return
    if (!window.confirm(`Удалить или отключить источник «${source.name}»?`)) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    setSourceBusy(source.id)
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, { method: 'DELETE', headers: { 'X-Admin-Key': adminKey }, signal: controller.signal })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { removeStoredAdminKey(); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Не удалось удалить источник'))
      }
      await loadAdminData()
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Не удалось удалить источник')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setSourceBusy('')
    }
  }

  const protocolCounts = useMemo(() => Object.fromEntries(stats?.byProtocol.map(x => [x.protocol, x.count]) ?? []), [stats])
  const exportQuery = useMemo(() => {
    const query = new URLSearchParams({ maxLatencyMs: String(maxLatency) })
    if (protocol !== 'All') query.set('protocol', protocol)
    return query.toString()
  }, [protocol, maxLatency])
  const freshness = stats?.lastRun?.startedAt ? timeAgo(stats.lastRun.startedAt) : 'ожидается'
  const latestCollection = diagnostics?.recentRuns[0]
  const latestBackup = diagnostics?.recentBackups[0]
  const adminMutationBusy = !!action || !!sourceBusy

  return <div className="app-shell">
    <header aria-hidden={adminOpen || undefined} inert={adminOpen || undefined}>
      <a className="brand" href="#top" aria-label="ProxyHarbor — наверх"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
      <nav><a href="#sources">Источники</a><a href="#catalog">Прокси</a><a href="#api">API</a><button className="admin-link" onClick={openAdmin}><KeyRound size={15}/> Управление</button></nav>
      <button className="mobile-admin" aria-label="Открыть управление" onClick={openAdmin}><KeyRound size={17}/></button>
      <div className={`live-pill ${apiError ? 'offline' : ''}`} aria-live="polite"><span/> {loading ? 'проверка…' : apiError ? 'API недоступен' : 'система активна'}</div>
    </header>

    <main id="top" aria-hidden={adminOpen || undefined} inert={adminOpen || undefined}>
      <section className="hero">
        <div className="eyebrow"><ShieldCheck size={15}/> Проверено в реальном времени</div>
        <h1>Чистый поток<br/><em>рабочих прокси.</em></h1>
        <p>ProxyHarbor непрерывно собирает открытые адреса, проверяет реальным HTTPS-запросом и отдаёт только те, которым можно доверять прямо сейчас.</p>
        <div className="hero-actions"><a className="primary" href="#catalog"><Wifi size={18}/> Открыть каталог</a><a className="secondary" href="#api"><ArrowDownToLine size={18}/> Экспортировать</a></div>
        <div className="pulse-card">
          <div className="pulse-head"><span>Состояние сети</span><span className="updated"><RefreshCw size={13}/> {freshness}</span></div>
          <div className="pulse-number">{formatNumber(stats?.alive)}<small> живых</small></div>
          <div className="protocol-strip">{protocols.map((item, index) => <div key={item} style={{'--share': `${Math.max(7, ((protocolCounts[item] ?? 0) / Math.max(1, stats?.alive ?? 1)) * 100)}%`, '--delay': `${index * 70}ms`} as React.CSSProperties}><span>{label(item)}</span><b>{formatNumber(protocolCounts[item] ?? 0)}</b></div>)}</div>
        </div>
      </section>

      <section className="metrics" aria-label="Главные показатели">
        <Metric icon={<Activity/>} label="Живых адресов" value={formatNumber(stats?.alive)} note={stats?.staleAlive ? `${formatNumber(stats.staleAlive)} скрыто как устаревшие` : 'прошли свежую проверку'}/>
        <Metric icon={<Gauge/>} label="Средняя задержка" value={stats?.averageLatencyMs ? `${Math.round(stats.averageLatencyMs)} мс` : '—'} note="до контрольного HTTPS"/>
        <Metric icon={<Database/>} label="Feed-источников" value={formatNumber(stats?.sources)} note={stats?.failingSources ? `${stats.failingSources} требуют внимания` : stats?.truncatedSources ? `${stats.truncatedSources} упёрлись в лимит` : sourceCatalog ? `${sourceCatalog.providerCount} независимых провайдеров` : 'все источники стабильны'}/>
        <Metric icon={<Clock3/>} label="Готовы к проверке" value={formatNumber(stats?.dueForCheck)} note={`${formatNumber(stats?.scheduledChecks)} запланировано позже`}/>
      </section>

      <section id="sources" className="public-sources" aria-label="Встроенные источники прокси">
        <div className="section-heading"><div><span className="kicker">SOURCE TRANSPARENCY</span><h2>{sourceCatalog?.providerCount ?? 50} независимых провайдеров</h2></div><p>{sourceCatalog ? `${sourceCatalog.feedCount} HTTPS feed · аудит ${sourceCatalog.lastAuditedOn}` : 'Публичный read-only каталог'}</p></div>
        {sourceCatalogError && <div className="source-catalog-error" role="status" aria-label="Состояние каталога источников"><span>{sourceCatalogError}</span><button onClick={() => void loadSourceCatalog()} disabled={sourceCatalogLoading}>{sourceCatalogLoading ? 'повторяем…' : 'повторить'}</button></div>}
        {sourceCatalog && <div className="provider-grid">{sourceCatalog.providers.map(provider =>
          <article key={`${provider.rank}-${provider.name}`}>
            <div><span>#{provider.rank}</span><strong>{provider.name}</strong></div>
            <small>{provider.feeds.length} feed · {provider.protocols.map(label).join(' · ')}</small>
            <a href={provider.feeds[0].url} target="_blank" rel="noreferrer" aria-label={`${provider.name}: открыть исходный feed`}>исходный feed ↗</a>
          </article>)}</div>}
      </section>

      <section id="catalog" className="catalog">
        <div className="section-heading"><div><span className="kicker">LIVE CATALOG</span><h2>Лучшие прямо сейчас</h2></div><p>Быстрый обход без глубокого OFFSET</p></div>
        <div className="filters"><div className="tabs" aria-label="Фильтр по протоколу"><button aria-pressed={protocol === 'All'} className={protocol === 'All' ? 'active' : ''} onClick={() => changeProtocol('All')}>Все</button>{protocols.map(x => <button key={x} aria-pressed={protocol === x} className={protocol === x ? 'active' : ''} onClick={() => changeProtocol(x)}>{label(x)}</button>)}</div><label>до <b>{maxLatency} мс</b><input type="range" min="200" max="5000" step="100" value={maxLatency} onChange={e => changeMaxLatency(Number(e.target.value))}/></label></div>
        {apiError && <div className="error-banner" role="alert"><X size={17}/>{apiError}<button onClick={() => { setApiError(''); setLoading(true); void load() }}>повторить</button></div>}
        <div className="proxy-table" role="table" aria-label="Проверенные прокси" aria-busy={loading}>
          <div className="table-row table-head" role="row"><span role="columnheader">Адрес</span><span role="columnheader">Протокол</span><span role="columnheader">Задержка</span><span role="columnheader">Надёжность</span><span role="columnheader">Проверен</span></div>
          {loading ? <div className="empty" role="status" aria-label="Состояние каталога прокси"><RefreshCw className="spin"/> Загружаем свежий каталог…</div> : proxies.length === 0 ? <div className="empty" role="status" aria-label="Состояние каталога прокси"><Server/> Живые прокси появятся после первого цикла проверки.</div> : proxies.map(proxy => <div className="table-row" role="row" key={proxy.url}><code role="cell">{proxy.host}<i>:</i>{proxy.port}</code><span role="cell" className={`badge ${proxy.protocol.toLowerCase()}`}>{label(proxy.protocol)}</span><span role="cell" className="latency"><i className={proxy.latencyMs < 800 ? 'fast' : proxy.latencyMs < 1800 ? 'medium' : 'slow'}/>{proxy.latencyMs} мс</span><span role="cell">{proxy.successRate}%</span><span role="cell">{timeAgo(proxy.lastCheckedAt)}</span></div>)}
        </div>
        {hasMore && nextCursor && !loading && <div className="catalog-more" aria-live="polite"><button onClick={loadMore} disabled={loadingMore}>{loadingMore ? <><RefreshCw className="spin"/> Загружаем…</> : <>Показать ещё <span>· загружено {formatNumber(proxies.length)}</span></>}</button></div>}
      </section>

      <section id="api" className="api-panel"><div><span className="kicker">ONE-CLICK EXPORT</span><h2>Забирайте как удобно</h2><p>Фильтруйте через API или скачивайте готовый список. Экспорт содержит только свежие Alive-прокси; большие наборы обходятся последовательными cursor-страницами без замедляющего OFFSET.</p></div><div className="export-grid">{['json','xml','txt','csv'].map(format => <a key={format} href={`${API}/api/v1/export/${format}?${exportQuery}`}><span>.{format}</span><ArrowDownToLine size={18}/></a>)}</div><div className="endpoint"><span>GET</span><code>/api/v1/proxies/seek?protocol=Socks5&amp;maxLatencyMs=1000</code></div></section>
    </main>

    <footer aria-hidden={adminOpen || undefined} inert={adminOpen || undefined}><div className="brand"><span className="brand-mark"><Network size={18}/></span><span>Proxy<span>Harbor</span></span></div><p>Используйте публичные прокси ответственно и в рамках закона.</p><span>v{APP_VERSION} · © {new Date().getFullYear()}</span></footer>

    {adminOpen && <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && closeAdmin()}>
      <section ref={adminDialogRef} className="admin-modal" role="dialog" aria-modal="true" aria-labelledby="admin-title">
        <button className="close" aria-label="Закрыть" onClick={closeAdmin}><X/></button>
        <span className="kicker">ADMIN CONSOLE</span><h2 id="admin-title">Управление сбором</h2><p>Ключ хранится только до закрытия вкладки.</p>
        <form className="key-input" aria-label="Вход администратора" onSubmit={event => { event.preventDefault(); void loadAdminData(true) }}><KeyRound size={18}/><input ref={adminKeyRef} type="password" aria-label="Ключ администратора" placeholder="X-Admin-Key" autoComplete="off" autoCapitalize="none" spellCheck={false} maxLength={256} value={adminKey} onChange={e => changeAdminKey(e.target.value)}/><button type="submit" disabled={adminLoading}>{adminLoading ? 'Проверяем…' : 'Войти'}</button>{adminAuthenticated && <button type="button" className="logout" onClick={logoutAdmin}>Выйти</button>}</form>
        {adminError && <div className="admin-notice" role="alert"><X size={16}/>{adminError}</div>}
        <div className="admin-actions">
          <button ref={firstAdminActionRef} onClick={() => runAdminAction('collect')} disabled={!adminAuthenticated || adminMutationBusy}><Play/> {action === 'collect' ? 'Собираем…' : 'Запустить сбор'}</button>
          <button onClick={() => runAdminAction('validate')} disabled={!adminAuthenticated || adminMutationBusy}><Check/> {action === 'validate' ? 'Проверяем…' : 'Проверить пакет'}</button>
          <button onClick={() => runAdminAction('backup')} disabled={!adminAuthenticated || adminMutationBusy}><Database/> {action === 'backup' ? 'Копируем…' : 'Создать backup'}</button>
        </div>
        {adminAuthenticated && <section className="admin-diagnostics" aria-label="Диагностика сервиса">
          <div className="diagnostics-heading"><h3>Диагностика</h3><button aria-label="Обновить диагностику" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></div>
          <div className="diagnostic-grid">
            <article title={`Параллельность ${formatNumber(diagnostics?.validationQueue?.concurrencyLimit)}, партия ${formatNumber(diagnostics?.validationQueue?.batchSize)}`}><span>Очередь проверки</span><strong>{formatNumber(diagnostics?.validationQueue?.due)}</strong><small>{formatNumber(diagnostics?.validationQueue?.attemptsLastFiveMinutes)} попыток за 5 мин · {formatRate(diagnostics?.validationQueue?.checksPerSecond)} · лимит {formatNumber(diagnostics?.validationQueue?.concurrencyLimit)} × {formatNumber(diagnostics?.validationQueue?.batchSize)} · ETA {formatDuration(diagnostics?.validationQueue?.estimatedDrainSeconds)} · {formatNumber(diagnostics?.validationQueue?.aliveLastFiveMinutes)} живых · {formatNumber(diagnostics?.validationQueue?.deferredLastFiveMinutes)} отложено</small></article>
            <article><span>Последний сбор</span><strong className={latestCollection?.candidateLimitReached || latestCollection?.sourcesTruncated ? 'status-running' : statusClass(latestCollection?.status)}>{latestCollection?.candidateLimitReached || latestCollection?.sourcesTruncated ? 'достигнут лимит' : statusLabel(latestCollection?.status)}</strong><small>{latestCollection ? `${formatNumber(latestCollection.candidatesFound)} кандидатов · ${timeAgo(latestCollection.startedAt)}` : 'Циклов пока нет'}</small></article>
            <article><span>Последний backup</span><strong className={statusClass(latestBackup?.status)}>{statusLabel(latestBackup?.status)}</strong><small>{latestBackup ? `${formatBytes(latestBackup.sizeBytes)} · ${backupDelivery(latestBackup)}` : 'Backup ещё не создавался'}</small></article>
            <article><span>Размер PostgreSQL</span><strong>{formatBytes(diagnostics?.databaseBytes)}</strong><small>{formatNumber(diagnostics?.validationQueue?.total)} известных прокси</small></article>
            <article aria-label="Состояние встроенного каталога"><span>Встроенный каталог</span><strong className={catalogStatusClass(diagnostics?.sourceCatalog)}>{diagnostics?.sourceCatalog ? `${diagnostics.sourceCatalog.enabledSources}/${diagnostics.sourceCatalog.expectedSources}` : '—'}</strong><small>{diagnostics?.sourceCatalog ? `${diagnostics.sourceCatalog.enabledProviders}/${diagnostics.sourceCatalog.expectedProviders} провайдеров · ${diagnostics.sourceCatalog.healthySources} полных и свежих · release-аудит ${diagnostics.sourceCatalog.lastAuditedOn}${diagnostics.sourceCatalog.truncatedSources ? ` · ${diagnostics.sourceCatalog.truncatedSources} усечено` : ''}${diagnostics.sourceCatalog.staleSources ? ` · ${diagnostics.sourceCatalog.staleSources} устарело` : ''}` : 'Снимок недоступен'}</small></article>
          </div>
          <div className="diagnostic-history">
            <div><h4>Последние сборы</h4>{diagnostics?.recentRuns.slice(0, 4).map(run => <article key={run.id} title={run.error}><span><i className={run.candidateLimitReached || run.sourcesTruncated ? 'status-running' : statusClass(run.status)}/>{timeAgo(run.startedAt)}</span><small>{formatNumber(run.sourcesSucceeded)}/{formatNumber(run.sourcesProcessed)} источников · +{formatNumber(run.newProxies)}{run.sourcesTruncated ? ` · усечено: ${run.sourcesTruncated}` : ''}{run.candidateLimitReached ? ' · общий лимит' : ''}</small></article>)}{diagnostics?.recentRuns.length === 0 && <p>Истории пока нет.</p>}</div>
            <div><h4>Последние проверки</h4>{(diagnostics?.recentValidationRuns ?? []).slice(0, 4).map(run => <article key={run.id} title={run.error}><span><i className={statusClass(run.status)}/>{timeAgo(run.startedAt)}</span><small>{formatNumber(run.checked + run.deferred)}/{formatNumber(run.claimed)} попыток · {formatNumber(run.alive)} живых · {run.finishedAt ? formatDuration((new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()) / 1000) : 'выполняется'}</small></article>)}{(diagnostics?.recentValidationRuns ?? []).length === 0 && <p>Истории пока нет.</p>}</div>
            <div><h4>История backup</h4>{diagnostics?.recentBackups.slice(0, 4).map(run => <article key={run.id} title={run.error}><span><i className={statusClass(run.status)}/>{run.fileName ?? timeAgo(run.startedAt)}</span><small>{formatBytes(run.sizeBytes)} · {backupDelivery(run)}</small></article>)}{diagnostics?.recentBackups.length === 0 && <p>Истории пока нет.</p>}</div>
          </div>
        </section>}
        <h3>Добавить источник</h3>
        <form className="source-form" onSubmit={saveSource}>
          <input required minLength={2} maxLength={120} aria-label="Название источника" placeholder="Название" value={sourceDraft.name} onChange={e => setSourceDraft({...sourceDraft, name: e.target.value})}/>
          <input required type="url" maxLength={2048} pattern="https://.*" aria-label="HTTPS URL источника" placeholder="https://example.org/proxies.txt" value={sourceDraft.url} onChange={e => setSourceDraft({...sourceDraft, url: e.target.value})}/>
          <select aria-label="Протокол источника" value={sourceDraft.protocol} onChange={e => setSourceDraft({...sourceDraft, protocol: e.target.value as Protocol})}>{protocols.map(item => <option key={item} value={item}>{label(item)}</option>)}</select>
          <input type="number" min={-10000} max={10000} aria-label="Приоритет источника" value={sourceDraft.priority} onChange={e => setSourceDraft({...sourceDraft, priority: Number(e.target.value)})}/>
          <button type="submit" disabled={!adminAuthenticated || adminMutationBusy}>{sourceBusy === 'new' ? 'Добавляем…' : 'Добавить'}</button>
        </form>
        <h3>Источники <span>{sources.length}</span></h3>
        <div className="source-list">{sources.map(source => <article key={source.id}>
          <div><b>{source.name}</b><small>{source.defaultProtocol} · {source.lastItemCount.toLocaleString('ru-RU')} адресов{source.lastContentFetchedAt ? ` · полный feed ${new Date(source.lastContentFetchedAt).toLocaleString('ru-RU')}` : ' · полный feed ещё не получен'}{source.lastResultTruncated ? ' · результат усечён' : ''}{source.consecutiveFailures > 0 ? ` · сбоев подряд: ${source.consecutiveFailures}` : ''}{source.nextFetchAt ? ` · повтор ${timeUntil(source.nextFetchAt)}` : ''}</small></div>
          <div className="source-controls"><span title={source.isBuiltIn ? `Встроенный источник · ${source.provider} · ${source.providerIdentity} · ранг ${source.catalogRank}` : 'Пользовательский источник'} className="source-kind">{source.isBuiltIn ? source.provider : 'свой'}</span><span title={source.lastError} className={source.lastError ? 'source-error' : 'source-ok'}>{source.lastError ? 'ошибка' : source.enabled ? 'активен' : 'пауза'}</span><button disabled={adminMutationBusy} onClick={() => toggleSource(source)}>{source.enabled ? 'Пауза' : 'Включить'}</button>{!source.isBuiltIn && <button className="danger" disabled={adminMutationBusy} onClick={() => removeSource(source)}>Удалить</button>}</div>
        </article>)}</div>
      </section>
    </div>}
  </div>
}

function Metric({icon, label, value, note}: {icon: React.ReactNode; label: string; value: string; note: string}) { return <article className="metric"><div className="metric-icon">{icon}</div><div><span>{label}</span><strong>{value}</strong><small>{note}</small></div></article> }
function formatNumber(value?: number) { return value === undefined ? '—' : value.toLocaleString('ru-RU') }
function formatBytes(value?: number) {
  if (value === undefined) return '—'
  if (value < 1024) return `${value} Б`
  if (value < 1024 ** 2) return `${(value / 1024).toLocaleString('ru-RU', { maximumFractionDigits: 1 })} КБ`
  if (value < 1024 ** 3) return `${(value / 1024 ** 2).toLocaleString('ru-RU', { maximumFractionDigits: 1 })} МБ`
  return `${(value / 1024 ** 3).toLocaleString('ru-RU', { maximumFractionDigits: 1 })} ГБ`
}
function formatRate(value?: number) { return value === undefined || value <= 0 ? 'скорость неизвестна' : `${value.toLocaleString('ru-RU', { maximumFractionDigits: 1 })}/с` }
function formatDuration(value?: number) { if (value === undefined || value <= 0) return '—'; if (value < 60) return `${Math.ceil(value)} сек`; const minutes = Math.ceil(value / 60); if (minutes < 60) return `${minutes} мин`; return `${Math.floor(minutes / 60)} ч ${minutes % 60} мин` }
function statusClass(status?: string) { return status === 'completed' ? 'status-ok' : status === 'failed' ? 'status-failed' : 'status-running' }
function catalogStatusClass(catalog?: SourceCatalogSnapshot) { return !catalog ? '' : catalog.isHealthy ? 'status-ok' : catalog.isComplete ? 'status-running' : 'status-failed' }
function statusLabel(status?: string) { return status === 'completed' ? 'успешно' : status === 'failed' ? 'ошибка' : status === 'running' ? 'выполняется' : 'нет данных' }
function backupDelivery(run: BackupRun) { return run.sentToTelegram ? 'доставлен в Telegram' : run.telegramConfigured ? 'Telegram не доставлен' : 'только локально' }
function label(protocol: Protocol) { return ({Http: 'HTTP', Https: 'HTTPS', Socks4: 'SOCKS4', Socks5: 'SOCKS5'})[protocol] }
function timeAgo(value: string) { const sec = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000)); if (sec < 10) return 'только что'; if (sec < 60) return `${sec} сек назад`; if (sec < 3600) return `${Math.floor(sec / 60)} мин назад`; return `${Math.floor(sec / 3600)} ч назад` }
function timeUntil(value: string) { const sec = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000)); if (sec < 60) return `через ${sec} сек`; if (sec < 3600) return `через ${Math.ceil(sec / 60)} мин`; return `через ${Math.ceil(sec / 3600)} ч` }

async function responseMessage(response: Response, fallback: string) {
  try {
    const problem = await response.json() as { title?: string; detail?: string }
    return problem.detail || problem.title || fallback
  } catch { return fallback }
}
