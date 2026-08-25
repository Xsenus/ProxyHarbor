import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Activity, ArrowDownToLine, ArrowRight, Ban, Bell, Bot, CalendarClock, Check, Clock3, CreditCard, Database, Eye, EyeOff, Gauge, HardDriveDownload, LayoutDashboard, LockKeyhole, LogOut, Mail, MessageCircle, Network, Pencil, Play, Plus, Radio, Receipt, RefreshCw, Send, Server, Settings2, ShieldCheck, ShieldOff, Star, Trash2, User, Users, Wifi, Workflow, X } from 'lucide-react'

type Protocol = 'Http' | 'Https' | 'Socks4' | 'Socks5'
type Proxy = { host: string; port: number; protocol: Protocol; url: string; latencyMs: number; successRate: number; exitIp?: string; lastCheckedAt: string; firstAliveAt?: string; lastAliveAt?: string; activeSince?: string; activeForSeconds?: number }
type PagedResult<T> = { items: T[]; page: number; pageSize: number; total: number }
type Stats = { alive: number; staleAlive: number; pending: number; dead: number; dueForCheck: number; checksInProgress: number; scheduledChecks: number; averageLatencyMs: number | null; sources: number; failingSources: number; repeatedlyFailingSources: number; truncatedSources: number; byProtocol: { protocol: Protocol; count: number }[]; lastRun?: { startedAt: string; candidatesFound: number; newProxies: number; sourcesTruncated: number; candidateLimitReached: boolean; status: string } }
type Source = { id: string; name: string; url: string; defaultProtocol: Protocol; enabled: boolean; priority: number; lastItemCount: number; lastResultTruncated: boolean; lastFetchedAt?: string; lastSucceededAt?: string; lastContentFetchedAt?: string; nextFetchAt?: string; consecutiveFailures: number; lastError?: string; isBuiltIn: boolean; provider?: string; providerIdentity?: string; catalogRank?: number }
type CollectionRun = { id: string; startedAt: string; finishedAt?: string; sourcesProcessed: number; sourcesSucceeded: number; sourcesFailed: number; sourcesSkipped: number; sourcesTruncated: number; candidatesFound: number; candidateLimitReached: boolean; newProxies: number; status: string; error?: string }
type ValidationRun = { id: string; startedAt: string; finishedAt?: string; claimed: number; checked: number; alive: number; deferred: number; status: string; error?: string }
type BackupRun = { id: string; startedAt: string; finishedAt?: string; status: string; fileName?: string; sizeBytes: number; telegramConfigured: boolean; sentToTelegram: boolean; error?: string }
type SourceCatalogSnapshot = { lastAuditedOn: string; expectedSources: number; presentSources: number; enabledSources: number; healthySources: number; failingSources: number; neverAuditedSources: number; staleSources: number; truncatedSources: number; expectedProviders: number; presentProviders: number; enabledProviders: number; isComplete: boolean; isHealthy: boolean }
type Diagnostics = {
  serverTime: string
  databaseBytes: number
  validationQueue?: { total: number; everAlive: number; historicalDead: number; leased: number; neverChecked: number; neverAttempted: number; due: number; scheduled: number; repeatedlyFailing: number; staleUnseen: number; attemptsLastFiveMinutes: number; checkedLastFiveMinutes: number; aliveLastFiveMinutes: number; deferredLastFiveMinutes: number; failedRunsLastFiveMinutes: number; activeRuns: number; concurrencyLimit: number; batchSize: number; checksPerSecond: number; estimatedDrainSeconds?: number; lastAttemptAt?: string }
  sourceCatalog?: SourceCatalogSnapshot
  recentRuns: CollectionRun[]
  recentValidationRuns?: ValidationRun[]
  recentBackups: BackupRun[]
}

const API = import.meta.env.VITE_API_URL ?? ''
const APP_VERSION = import.meta.env.VITE_APP_VERSION ?? '0.0.0-local'
const protocols: Protocol[] = ['Http', 'Https', 'Socks4', 'Socks5']
type AdminSection = 'overview' | 'operations' | 'sources' | 'backups' | 'users' | 'payments' | 'telegram' | 'subscriptions' | 'access'
type AccountProfile = { id: string; userName: string; email: string; displayName?: string; createdAt: string; lastLoginAt?: string; roles: string[]; subscription?: { plan: string; status: string; startedAt: string; expiresAt?: string } }
type PaymentCatalog = { enabled: boolean; products: PaymentProduct[]; providers: PaymentProvider[] }
type PaymentProduct = { code: string; name: string; plan: string; durationDays: number; amountMinor: number; currency: string; description: string }
type PaymentProvider = { code: string; name: string; available: boolean }
type PaymentOrder = { id: string; productCode: string; plan: string; provider: string; amountMinor: number; currency: string; status: string; createdAt: string; paidAt?: string }
type AdminPaymentProduct = PaymentProduct & { enabled: boolean }
type AdminPaymentProvider = { code: string; name: string; enabled: boolean; merchantId: string; publicId: string; testMode: boolean; secretConfigured: boolean; secondarySecretConfigured: boolean; ready: boolean; webhookUrl: string }
type AdminPaymentSettings = { enabled: boolean; products: AdminPaymentProduct[]; providers: AdminPaymentProvider[] }
type AdminPaymentProviderDraft = AdminPaymentProvider & { secretKey: string; secondarySecret: string; clearSecretKey: boolean; clearSecondarySecret: boolean }
type AdminInvoice = PaymentOrder & { userId:string; userName:string; email:string; providerPaymentId?:string; updatedAt:string }
type InvoicePage = PagedResult<AdminInvoice> & { summary:{status:string;count:number;amountMinor:number}[] }
type AdminSubscription = { id:string;userId:string;userName:string;email:string;displayName?:string;plan:string;status:string;startedAt:string;expiresAt?:string;updatedAt:string }
type SubscriptionPage = PagedResult<AdminSubscription> & { summary:{active:number;trialing:number;suspended:number;expiringSoon:number} }
type AccessClient = {ipAddress:string;userId?:string;requests:number;blockedRequests:number;proxyItems:number;bytesSent:number;lastSeenAt:string}
type AccessRule = {id:string;kind:string;value:string;userId?:string;reason:string;enabled:boolean;expiresAt?:string;createdAt:string}
type AccessPage = PagedResult<AccessClient> & {rules:AccessRule[];summary:{requests:number;proxyItems:number;uniqueIps:number;activeRules:number}}
type AdminUser = AccountProfile & { isActive: boolean }
type UserAccessDraft = { isActive: boolean; administrator: boolean; subscriber: boolean; plan: string; status: string }
type SourceDraft = { name: string; url: string; protocol: Protocol; priority: number; enabled: boolean }
type TelegramStats = { users:number;activeUsers30d:number;notificationsEnabled:number;blocked:number;paidOrders:number;starsRevenue:number;queued:number;failed:number }
type TelegramSettings = { enabled:boolean;updateMode:'webhook'|'polling';name:string;description:string;shortDescription:string;supportText:string;proxyFileMaxItems:number;webhookMaxConnections:number;productStars:Record<string,number>;tokenConfigured:boolean;botId?:number;botUsername?:string;provisionedAt?:string;updatedAt?:string;webhookUrl:string;stats:TelegramStats }
type TelegramChat = { id:string;chatId:number;telegramUserId:number;username?:string;displayName:string;languageCode?:string;notificationsEnabled:boolean;isBlocked:boolean;createdAt:string;lastInteractionAt:string;subscription:{plan:string;status:string;expiresAt?:string};messages:number }
type TelegramMessage = { id:string;direction:'inbound'|'bot'|'admin';text:string;createdAt:string }

const emptySourceDraft: SourceDraft = { name: '', url: '', protocol: 'Http', priority: 100, enabled: true }

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
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [total, setTotal] = useState(0)
  const [apiError, setApiError] = useState('')
  const currentPath = window.location.pathname.replace(/\/+$/, '') || '/'
  const loginPage = currentPath === '/admin/login' || currentPath === '/login'
  const registerPage = currentPath === '/register'
  const forgotPasswordPage = currentPath === '/forgot-password'
  const resetPasswordPage = currentPath === '/reset-password'
  const accountOpen = currentPath === '/account' || currentPath === '/account/profile'
  const adminOpen = currentPath === '/admin' || currentPath.startsWith('/admin/') && !loginPage
  const adminSection: AdminSection = currentPath === '/admin/sources' ? 'sources'
    : currentPath === '/admin/operations' ? 'operations'
      : currentPath === '/admin/backups' ? 'backups'
        : currentPath === '/admin/users' ? 'users'
          : currentPath === '/admin/payments' ? 'payments'
            : currentPath === '/admin/telegram' ? 'telegram'
              : currentPath === '/admin/subscriptions' ? 'subscriptions'
              : currentPath === '/admin/access' ? 'access' : 'overview'
  const [adminAuthenticated, setAdminAuthenticated] = useState(false)
  const [adminError, setAdminError] = useState('')
  const [sources, setSources] = useState<Source[]>([])
  const [sourcePage, setSourcePage] = useState(1)
  const [sourcePageSize, setSourcePageSize] = useState(10)
  const [sourceTotal, setSourceTotal] = useState(0)
  const [diagnostics, setDiagnostics] = useState<Diagnostics | null>(null)
  const [adminLoading, setAdminLoading] = useState(false)
  const [action, setAction] = useState('')
  const [sourceBusy, setSourceBusy] = useState('')
  const [sourceDraft, setSourceDraft] = useState<SourceDraft>(emptySourceDraft)
  const [sourceEditorOpen, setSourceEditorOpen] = useState(false)
  const [editingSource, setEditingSource] = useState<Source | null>(null)
  const [sourceDeleteConfirm, setSourceDeleteConfirm] = useState(false)
  const firstAdminActionRef = useRef<HTMLButtonElement>(null)
  const focusAdminActionAfterLoginRef = useRef(false)
  const autoLoginAttemptedRef = useRef(false)
  const catalogRequestIdRef = useRef(0)
  const publicRequestIdRef = useRef(0)
  const publicAbortRef = useRef<AbortController | null>(null)
  const adminRequestIdRef = useRef(0)
  const adminAbortRef = useRef<AbortController | null>(null)
  const adminSessionIdRef = useRef(0)
  const adminMutationAbortRefs = useRef(new Set<AbortController>())

  const cancelPublicRequests = useCallback(() => {
    publicRequestIdRef.current++
    catalogRequestIdRef.current++
    publicAbortRef.current?.abort()
  }, [])

  /** Обновляет статистику и, при необходимости, первую keyset-страницу каталога. */
  const load = useCallback(async (includeCatalog = true) => {
    const requestId = ++publicRequestIdRef.current
    const catalogRequestId = includeCatalog ? ++catalogRequestIdRef.current : 0
    publicAbortRef.current?.abort()
    const controller = new AbortController()
    publicAbortRef.current = controller
    try {
      const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize), maxLatencyMs: String(maxLatency) })
      if (protocol !== 'All') query.set('protocol', protocol)
      const [statsResponse, proxyResponse] = await Promise.all([
        fetch(`${API}/api/v1/stats`, { signal: controller.signal }),
        includeCatalog ? fetch(`${API}/api/v1/proxies?${query}`, { signal: controller.signal }) : Promise.resolve(null),
      ])
      if (!statsResponse.ok || (proxyResponse && !proxyResponse.ok)) throw new Error('API пока недоступен')
      const statsSnapshot = await statsResponse.json()
      if (requestId !== publicRequestIdRef.current) return
      setStats(statsSnapshot)
      if (proxyResponse && catalogRequestId === catalogRequestIdRef.current) {
        const snapshot = await proxyResponse.json() as PagedResult<Proxy>
        setProxies(snapshot.items)
        setTotal(snapshot.total)
        const availablePages = Math.max(1, Math.ceil(snapshot.total / pageSize))
        if (page > availablePages) setPage(availablePages)
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
  }, [protocol, maxLatency, page, pageSize])

  useEffect(() => {
    // Смена фильтра начинает новый обход; старые ответы больше не могут заменить его результаты.
    if (currentPath !== '/') return
    let stopped = false
    let refreshTimer: number | undefined
    const refresh = async () => {
      await load(true)
      if (!stopped) refreshTimer = window.setTimeout(() => void refresh(), 15_000)
    }
    void refresh()
    return () => {
      stopped = true
      if (refreshTimer !== undefined) window.clearTimeout(refreshTimer)
      cancelPublicRequests()
    }
  }, [load, cancelPublicRequests, currentPath])

  /** Инвалидирует предыдущий cursor до запуска запроса с новым фильтром. */
  const changeProtocol = (value: Protocol | 'All') => {
    if (value === protocol) return
    catalogRequestIdRef.current++
    setLoading(true)
    setPage(1)
    setProtocol(value)
  }

  const changeMaxLatency = (value: number) => {
    if (value === maxLatency) return
    catalogRequestIdRef.current++
    setLoading(true)
    setPage(1)
    setMaxLatency(value)
  }

  const loadAdminData = useCallback(async (
    focusFirstAction = false,
    requestedSourcePage = sourcePage,
    requestedSourcePageSize = sourcePageSize,
  ) => {
    const requestId = ++adminRequestIdRef.current
    adminAbortRef.current?.abort()
    const controller = new AbortController()
    adminAbortRef.current = controller
    setAdminLoading(true)
    try {
      const requestOptions = { credentials: 'include' as const, signal: controller.signal }
      const sourceQuery = new URLSearchParams({ page: String(requestedSourcePage), pageSize: String(requestedSourcePageSize) })
      const [sourcesResponse, diagnosticsResponse] = await Promise.all([
        fetch(`${API}/api/v1/admin/sources?${sourceQuery}`, requestOptions),
        fetch(`${API}/api/v1/admin/diagnostics`, requestOptions),
      ])
      if (requestId !== adminRequestIdRef.current) return
      const unauthorizedResponse = [sourcesResponse, diagnosticsResponse].find(response => response.status === 401)
      if (unauthorizedResponse) {
        setAdminAuthenticated(false)
        setSources([])
        setSourceTotal(0)
        setDiagnostics(null)
        window.location.replace('/login')
        return
      }
      if ([sourcesResponse, diagnosticsResponse].some(response => response.status === 403)) {
        window.location.replace('/account')
        return
      }
      if (!sourcesResponse.ok) throw new Error(await responseMessage(sourcesResponse, 'Административная сессия недоступна'))
      if (!diagnosticsResponse.ok) throw new Error(await responseMessage(diagnosticsResponse, 'Диагностика недоступна'))
      const [sourceSnapshot, diagnosticSnapshot] = await Promise.all([
        sourcesResponse.json() as Promise<PagedResult<Source>>,
        diagnosticsResponse.json(),
      ])
      if (requestId !== adminRequestIdRef.current) return
      setSources(sourceSnapshot.items)
      setSourceTotal(sourceSnapshot.total)
      const availableSourcePages = Math.max(1, Math.ceil(sourceSnapshot.total / requestedSourcePageSize))
      if (requestedSourcePage > availableSourcePages) setSourcePage(availableSourcePages)
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
  }, [sourcePage, sourcePageSize])

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
    const initialLoad = window.setTimeout(() => void loadAdminData(true), 0)
    return () => window.clearTimeout(initialLoad)
  }, [adminOpen, loadAdminData])

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

  /** Отменяет все чтения и мутации, принадлежавшие предыдущей admin-сессии. */
  const invalidateAdminSession = useCallback(() => {
    adminSessionIdRef.current++
    adminRequestIdRef.current++
    adminAbortRef.current?.abort()
    adminMutationAbortRefs.current.forEach(controller => controller.abort())
    adminMutationAbortRefs.current.clear()
  }, [])
  useEffect(() => () => invalidateAdminSession(), [invalidateAdminSession])
  const logoutAdmin = useCallback(async () => {
    invalidateAdminSession()
    setAdminAuthenticated(false)
    setAdminError('')
    setSources([])
    setSourceTotal(0)
    setDiagnostics(null)
    setAction('')
    setSourceBusy('')
    setSourceEditorOpen(false)
    setEditingSource(null)
    setSourceDeleteConfirm(false)
    setSourceDraft(emptySourceDraft)
    try { await fetch(`${API}/api/v1/auth/logout`, { method: 'POST', credentials: 'include' }) }
    finally { window.location.replace('/login') }
  }, [invalidateAdminSession])

  const runAdminAction = async (name: 'collect' | 'validate' | 'backup') => {
    if (!adminAuthenticated || action || sourceBusy) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    setAction(name)
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/${name}`, { method: 'POST', credentials: 'include', signal: controller.signal })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { setAdminAuthenticated(false); window.location.replace('/login') }
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

  const openNewSource = () => {
    setEditingSource(null)
    setSourceDraft(emptySourceDraft)
    setSourceDeleteConfirm(false)
    setAdminError('')
    setSourceEditorOpen(true)
  }

  const openSourceEditor = (source: Source) => {
    setEditingSource(source)
    setSourceDraft({
      name: source.name,
      url: source.url,
      protocol: source.defaultProtocol,
      priority: source.priority,
      enabled: source.enabled,
    })
    setSourceDeleteConfirm(false)
    setAdminError('')
    setSourceEditorOpen(true)
  }

  const closeSourceEditor = () => {
    if (sourceBusy) return
    setSourceEditorOpen(false)
    setEditingSource(null)
    setSourceDeleteConfirm(false)
    setSourceDraft(emptySourceDraft)
  }

  const saveSource = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!adminAuthenticated || action || sourceBusy) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    const source = editingSource
    setSourceBusy(source?.id ?? 'new')
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/sources${source ? `/${source.id}` : ''}`, {
        method: source ? 'PUT' : 'POST', credentials: 'include', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(sourceDraft),
        signal: controller.signal,
      })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { setAdminAuthenticated(false); window.location.replace('/login') }
        throw new Error(await responseMessage(response, source ? 'Не удалось изменить источник' : 'Не удалось добавить источник'))
      }
      setSourceEditorOpen(false)
      setEditingSource(null)
      setSourceDeleteConfirm(false)
      setSourceDraft(emptySourceDraft)
      await loadAdminData(false, sourcePage, sourcePageSize)
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : source ? 'Не удалось изменить источник' : 'Не удалось добавить источник')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setSourceBusy('')
    }
  }

  const removeSource = async (source: Source) => {
    if (!adminAuthenticated || action || sourceBusy) return
    const sessionId = adminSessionIdRef.current
    const controller = new AbortController()
    adminMutationAbortRefs.current.add(controller)
    setSourceBusy(source.id)
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, { method: 'DELETE', credentials: 'include', signal: controller.signal })
      if (sessionId !== adminSessionIdRef.current) return
      if (!response.ok) {
        if (response.status === 401) { setAdminAuthenticated(false); window.location.replace('/login') }
        throw new Error(await responseMessage(response, 'Не удалось удалить источник'))
      }
      setSourceEditorOpen(false)
      setEditingSource(null)
      setSourceDeleteConfirm(false)
      setSourceDraft(emptySourceDraft)
      const nextPage = !source.isBuiltIn && sources.length === 1 && sourcePage > 1 ? sourcePage - 1 : sourcePage
      if (nextPage !== sourcePage) setSourcePage(nextPage)
      await loadAdminData(false, nextPage, sourcePageSize)
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Не удалось удалить источник')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setSourceBusy('')
    }
  }

  useEffect(() => {
    if (!sourceEditorOpen) return
    const previousOverflow = document.body.style.overflow
    document.body.style.overflow = 'hidden'
    const closeOnEscape = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !sourceBusy) {
        setSourceEditorOpen(false)
        setEditingSource(null)
        setSourceDeleteConfirm(false)
        setSourceDraft(emptySourceDraft)
      }
    }
    window.addEventListener('keydown', closeOnEscape)
    return () => {
      document.body.style.overflow = previousOverflow
      window.removeEventListener('keydown', closeOnEscape)
    }
  }, [sourceEditorOpen, sourceBusy])

  const protocolCounts = useMemo(() => Object.fromEntries(stats?.byProtocol.map(x => [x.protocol, x.count]) ?? []), [stats])
  const exportQuery = useMemo(() => {
    const query = new URLSearchParams({ maxLatencyMs: String(maxLatency) })
    if (protocol !== 'All') query.set('protocol', protocol)
    return query.toString()
  }, [protocol, maxLatency])
  const freshness = stats?.lastRun?.startedAt ? timeAgo(stats.lastRun.startedAt) : 'ожидается'
  const latestCollection = diagnostics?.recentRuns[0]
  const latestValidation = diagnostics?.recentValidationRuns?.[0]
  const latestBackup = diagnostics?.recentBackups[0]
  const adminMutationBusy = !!action || !!sourceBusy
  const totalPages = Math.max(1, Math.ceil(total / pageSize))
  const sourceTotalPages = Math.max(1, Math.ceil(sourceTotal / sourcePageSize))

  if (loginPage) return <AccountLoginPage/>
  if (registerPage) return <RegisterPage/>
  if (forgotPasswordPage) return <ForgotPasswordPage/>
  if (resetPasswordPage) return <ResetPasswordPage/>
  if (accountOpen) return <AccountPage/>

  return <div className="app-shell">
    {!adminOpen && <><header>
      <a className="brand" href="#top" aria-label="ProxyHarbor — наверх"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
      <nav><a href="#catalog">Прокси</a><a href="#api">API</a><a className="admin-link" href="/login"><LockKeyhole size={15}/> Войти</a></nav>
      <a className="mobile-admin" aria-label="Войти в аккаунт" href="/login"><LockKeyhole size={17}/></a>
      <div className={`live-pill ${apiError ? 'offline' : ''}`} aria-live="polite"><span/> {loading ? 'проверка…' : apiError ? 'API недоступен' : 'система активна'}</div>
    </header>

    <main id="top">
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
        <Metric icon={<Database/>} label="Поток данных" value="24 / 7" note="непрерывный автоматический сбор"/>
        <Metric icon={<Clock3/>} label="Готовы к проверке" value={formatNumber(stats?.dueForCheck)} note={`${formatNumber(stats?.checksInProgress)} выполняется · ${formatNumber(stats?.scheduledChecks)} запланировано позже`}/>
      </section>

      <section id="catalog" className="catalog">
        <div className="section-heading"><div><span className="kicker">LIVE CATALOG</span><h2>Лучшие прямо сейчас</h2></div><p>Серверная выборка · {formatNumber(total)} найдено</p></div>
        <div className="filters"><div className="tabs" aria-label="Фильтр по протоколу"><button aria-pressed={protocol === 'All'} className={protocol === 'All' ? 'active' : ''} onClick={() => changeProtocol('All')}>Все</button>{protocols.map(x => <button key={x} aria-pressed={protocol === x} className={protocol === x ? 'active' : ''} onClick={() => changeProtocol(x)}>{label(x)}</button>)}</div><label>до <b>{maxLatency} мс</b><input type="range" min="200" max="5000" step="100" value={maxLatency} onChange={e => changeMaxLatency(Number(e.target.value))}/></label></div>
        {apiError && <div className="error-banner" role="alert"><X size={17}/>{apiError}<button onClick={() => { setApiError(''); setLoading(true); void load() }}>повторить</button></div>}
        <div className="proxy-table" role="table" aria-label="Проверенные прокси" aria-busy={loading}>
          <div role="rowgroup"><div className="table-row table-head" role="row"><span role="columnheader">Адрес</span><span role="columnheader">Протокол</span><span role="columnheader">Задержка</span><span role="columnheader">Надёжность</span><span role="columnheader">Активен</span><span role="columnheader">Проверен</span></div></div>
          <div role="rowgroup">
            {loading ? <div role="row"><div className="empty" role="cell" aria-live="polite" aria-label="Состояние каталога прокси"><RefreshCw className="spin"/> Загружаем свежий каталог…</div></div> : proxies.length === 0 ? <div role="row"><div className="empty" role="cell" aria-live="polite" aria-label="Состояние каталога прокси"><Server/> Живые прокси появятся после первого цикла проверки.</div></div> : proxies.map(proxy => <div className="table-row" role="row" key={proxy.url}><code role="cell">{proxy.host}<i>:</i>{proxy.port}</code><span role="cell" className={`badge ${proxy.protocol.toLowerCase()}`}>{label(proxy.protocol)}</span><span role="cell" className="latency"><i className={proxy.latencyMs < 800 ? 'fast' : proxy.latencyMs < 1800 ? 'medium' : 'slow'}/>{proxy.latencyMs} мс</span><span role="cell">{proxy.successRate}%</span><span role="cell" title={proxy.activeSince ? `С ${new Date(proxy.activeSince).toLocaleString('ru-RU')}` : undefined}>{formatActiveDuration(proxy.activeForSeconds)}</span><span role="cell">{timeAgo(proxy.lastCheckedAt)}</span></div>)}
          </div>
        </div>
        {!loading && total > 0 && <ProxyPagination
          page={page} pageSize={pageSize} total={total} totalPages={totalPages}
          onPageChange={next => { setLoading(true); setPage(next); document.getElementById('catalog')?.scrollIntoView?.({ behavior: 'smooth' }) }}
          onPageSizeChange={size => { setLoading(true); setPageSize(size); setPage(1) }}/>
        }
      </section>

      <section id="api" className="api-panel"><div><span className="kicker">ONE-CLICK EXPORT</span><h2>Забирайте как удобно</h2><p>Фильтруйте через API или скачивайте готовый список. Экспорт содержит только свежие Alive-прокси; большие наборы обходятся последовательными cursor-страницами без замедляющего OFFSET.</p></div><div className="export-grid">{['json','xml','txt','csv'].map(format => <a key={format} href={`${API}/api/v1/export/${format}?${exportQuery}`}><span>.{format}</span><ArrowDownToLine size={18}/></a>)}</div><div className="endpoint"><span>GET</span><code>/api/v1/proxies/seek?protocol=Socks5&amp;maxLatencyMs=1000</code></div></section>
    </main>

    <footer><div className="brand"><span className="brand-mark"><Network size={18}/></span><span>Proxy<span>Harbor</span></span></div><p>Используйте публичные прокси ответственно и в рамках закона.</p><span>v{APP_VERSION} · © {new Date().getFullYear()}</span></footer></>}

    {adminOpen && <main className="admin-workspace">
      <aside className="admin-sidebar" aria-label="Навигация по панели управления">
        <a className="brand admin-brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
        <nav className="admin-nav-group" aria-label="Разделы админ-панели"><span>Управление</span>
          <a className={adminSection === 'overview' ? 'active' : ''} aria-current={adminSection === 'overview' ? 'page' : undefined} href="/admin"><LayoutDashboard/>Обзор</a>
          <a className={adminSection === 'operations' ? 'active' : ''} aria-current={adminSection === 'operations' ? 'page' : undefined} href="/admin/operations"><Workflow/>Операции</a>
          <a className={adminSection === 'sources' ? 'active' : ''} aria-current={adminSection === 'sources' ? 'page' : undefined} href="/admin/sources"><Server/>Источники <b>{sourceTotal || '—'}</b></a>
          <a className={adminSection === 'backups' ? 'active' : ''} aria-current={adminSection === 'backups' ? 'page' : undefined} href="/admin/backups"><HardDriveDownload/>Резервные копии</a>
          <a className={adminSection === 'users' ? 'active' : ''} aria-current={adminSection === 'users' ? 'page' : undefined} href="/admin/users"><Users/>Пользователи</a>
          <a className={adminSection === 'payments' ? 'active' : ''} aria-current={adminSection === 'payments' ? 'page' : undefined} href="/admin/payments"><CreditCard/>Оплата</a>
          <a className={adminSection === 'telegram' ? 'active' : ''} aria-current={adminSection === 'telegram' ? 'page' : undefined} href="/admin/telegram"><Bot/>Telegram-бот</a>
          <a className={adminSection === 'subscriptions' ? 'active' : ''} aria-current={adminSection === 'subscriptions' ? 'page' : undefined} href="/admin/subscriptions"><CalendarClock/>Подписки</a>
          <a className={adminSection === 'access' ? 'active' : ''} aria-current={adminSection === 'access' ? 'page' : undefined} href="/admin/access"><ShieldOff/>Доступ и IP</a>
        </nav>
        <div className="admin-sidebar-foot"><a href="/account"><User/>Профиль</a><a href="/"><ArrowRight/>На главную</a><button onClick={logoutAdmin}><LogOut/>Выйти</button></div>
      </aside>

      <section className="admin-content">
        <header className="admin-content-header"><div><span className="kicker">ADMIN CONSOLE</span><strong>Панель управления</strong></div><div className="admin-session"><span/><div><b>Администратор</b><small>Защищённая сессия</small></div></div></header>
        {adminError && <div className="admin-notice admin-page-notice" role="alert"><X size={16}/>{adminError}</div>}
        {adminLoading && !adminAuthenticated ? <div className="admin-initial-loading"><RefreshCw className="spin"/><span>Загружаем панель…</span></div> : <>
          {adminSection === 'overview' && <section className="admin-section" aria-labelledby="admin-overview-title">
            <div className="admin-section-heading"><div><span className="kicker">СОСТОЯНИЕ СИСТЕМЫ</span><h1 id="admin-overview-title">Обзор</h1><p>Ключевые показатели ProxyHarbor в одном месте.</p></div><button className="icon-button" aria-label="Обновить диагностику" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></div>
            <div className="admin-summary-grid">
              <article><span className="summary-icon"><Activity/></span><div><small>Очередь проверки</small><strong>{formatNumber(diagnostics?.validationQueue?.due)}</strong><p>{formatRate(diagnostics?.validationQueue?.checksPerSecond)} · ETA {formatDuration(diagnostics?.validationQueue?.estimatedDrainSeconds)}</p></div></article>
              <article><span className="summary-icon"><Server/></span><div><small>Активные источники</small><strong>{formatNumber(diagnostics?.sourceCatalog?.enabledSources)}</strong><p>{formatNumber(diagnostics?.sourceCatalog?.healthySources)} полных и свежих</p></div></article>
              <article><span className="summary-icon"><Database/></span><div><small>Известно прокси</small><strong>{formatNumber(diagnostics?.validationQueue?.total)}</strong><p>{formatNumber(diagnostics?.validationQueue?.everAlive)} работали хотя бы раз</p></div></article>
              <article><span className="summary-icon"><HardDriveDownload/></span><div><small>База PostgreSQL</small><strong>{formatBytes(diagnostics?.databaseBytes)}</strong><p>{latestBackup ? `Backup ${timeAgo(latestBackup.startedAt)}` : 'Backup ещё не создавался'}</p></div></article>
            </div>
            <div className="admin-panel-grid">
              <section className="admin-card"><div className="card-heading"><div><span className="kicker">ПОСЛЕДНИЕ ЦИКЛЫ</span><h2>Активность</h2></div><a href="/admin/operations">Все операции <ArrowRight/></a></div><div className="activity-list">
                <AdminActivity icon={<Play/>} title="Сбор прокси" status={latestCollection?.candidateLimitReached || latestCollection?.sourcesTruncated ? 'attention' : latestCollection?.status} detail={latestCollection ? `${formatNumber(latestCollection.candidatesFound)} кандидатов · +${formatNumber(latestCollection.newProxies)} новых` : 'Запусков пока нет'} time={latestCollection?.startedAt}/>
                <AdminActivity icon={<Check/>} title="Проверка прокси" status={latestValidation?.status} detail={latestValidation ? `${formatNumber(latestValidation.checked)} проверено · ${formatNumber(latestValidation.alive)} живых` : 'Запусков пока нет'} time={latestValidation?.startedAt}/>
                <AdminActivity icon={<Database/>} title="Резервная копия" status={latestBackup?.status} detail={latestBackup ? `${formatBytes(latestBackup.sizeBytes)} · ${backupDelivery(latestBackup)}` : 'Копий пока нет'} time={latestBackup?.startedAt}/>
              </div></section>
              <section className="admin-card catalog-health" aria-label="Состояние встроенного каталога"><div className="card-heading"><div><span className="kicker">КАТАЛОГ</span><h2>Источники</h2></div><a href="/admin/sources">Управление <ArrowRight/></a></div><div className={`health-orbit ${catalogStatusClass(diagnostics?.sourceCatalog)}`}><strong>{diagnostics?.sourceCatalog ? `${diagnostics.sourceCatalog.enabledProviders}/${diagnostics.sourceCatalog.expectedProviders}` : '—'}</strong><span>провайдеров</span></div><p>{diagnostics?.sourceCatalog ? `${diagnostics.sourceCatalog.healthySources} источников полны и свежи. Последний аудит: ${diagnostics.sourceCatalog.lastAuditedOn}.` : 'Снимок каталога пока недоступен.'}</p></section>
            </div>
          </section>}

          {adminSection === 'operations' && <section className="admin-section" aria-labelledby="admin-operations-title">
            <div className="admin-section-heading"><div><span className="kicker">РУЧНОЕ УПРАВЛЕНИЕ</span><h1 id="admin-operations-title">Операции</h1><p>Запускайте сбор и проверку, следите за очередью и последними циклами.</p></div><button className="icon-button" aria-label="Обновить диагностику" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></div>
            <div className="operation-actions">
              <button ref={firstAdminActionRef} onClick={() => runAdminAction('collect')} disabled={!adminAuthenticated || adminMutationBusy}><span><Play/></span><div><b>{action === 'collect' ? 'Собираем…' : 'Запустить сбор'}</b><small>Получить свежие адреса из всех активных источников</small></div><ArrowRight/></button>
              <button onClick={() => runAdminAction('validate')} disabled={!adminAuthenticated || adminMutationBusy}><span><Check/></span><div><b>{action === 'validate' ? 'Проверяем…' : 'Проверить пакет'}</b><small>Немедленно запустить очередную порцию проверок</small></div><ArrowRight/></button>
            </div>
            <section className="admin-card queue-card"><div className="card-heading"><div><span className="kicker">ВАЛИДАТОР</span><h2>Очередь проверки</h2></div><strong>{formatNumber(diagnostics?.validationQueue?.due)}</strong></div><div className="queue-metrics"><div><span>Скорость</span><b>{formatRate(diagnostics?.validationQueue?.checksPerSecond)}</b></div><div><span>Попыток за 5 минут</span><b>{formatNumber(diagnostics?.validationQueue?.attemptsLastFiveMinutes)}</b></div><div><span>Живых за 5 минут</span><b>{formatNumber(diagnostics?.validationQueue?.aliveLastFiveMinutes)}</b></div><div><span>Оценка завершения</span><b>{formatDuration(diagnostics?.validationQueue?.estimatedDrainSeconds)}</b></div></div></section>
            <div className="history-columns"><AdminRunHistory title="Последние сборы" empty="Сборы ещё не запускались.">{diagnostics?.recentRuns.slice(0, 8).map(run => <HistoryRow key={run.id} status={run.candidateLimitReached || run.sourcesTruncated ? 'attention' : run.status} title={`${formatNumber(run.sourcesSucceeded)}/${formatNumber(run.sourcesProcessed)} источников`} detail={`${formatNumber(run.candidatesFound)} кандидатов · +${formatNumber(run.newProxies)} новых`} time={run.startedAt}/>)}</AdminRunHistory><AdminRunHistory title="Последние проверки" empty="Проверки ещё не запускались.">{(diagnostics?.recentValidationRuns ?? []).slice(0, 8).map(run => <HistoryRow key={run.id} status={run.status} title={`${formatNumber(run.checked + run.deferred)}/${formatNumber(run.claimed)} попыток`} detail={`${formatNumber(run.alive)} живых · ${run.finishedAt ? formatDuration((new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()) / 1000) : 'выполняется'}`} time={run.startedAt}/>)}</AdminRunHistory></div>
          </section>}

          {adminSection === 'sources' && <section className="admin-section" aria-labelledby="admin-sources-title">
            <div className="admin-section-heading"><div><span className="kicker">КАТАЛОГ СБОРА</span><h1 id="admin-sources-title">Источники</h1><p>Подключайте собственные HTTPS-feed и управляйте активностью существующих источников.</p></div><div className="admin-heading-actions"><span className="section-count">{sourceTotal}</span><button className="primary-admin-button" onClick={openNewSource} disabled={!adminAuthenticated || adminMutationBusy}><Plus/>Добавить источник</button></div></div>
            <section className="admin-card source-catalog-card"><div className="card-heading"><div><span className="kicker">ВСЕ ИСТОЧНИКИ</span><h2>Каталог <em>{sourceTotal}</em></h2></div><button className="icon-button" aria-label="Обновить источники" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></div><div className="source-list">{sources.map(source => <article key={source.id}>
              <div><b>{source.name}</b><small>{source.defaultProtocol} · {source.lastItemCount.toLocaleString('ru-RU')} адресов{source.lastContentFetchedAt ? ` · полный feed ${new Date(source.lastContentFetchedAt).toLocaleString('ru-RU')}` : ' · полный feed ещё не получен'}{source.lastResultTruncated ? ' · результат усечён' : ''}{source.consecutiveFailures > 0 ? ` · сбоев подряд: ${source.consecutiveFailures}` : ''}{source.nextFetchAt ? ` · повтор ${timeUntil(source.nextFetchAt)}` : ''}</small></div>
              <div className="source-controls"><span title={source.isBuiltIn ? `Встроенный источник · ${source.provider} · ${source.providerIdentity} · ранг ${source.catalogRank}` : 'Пользовательский источник'} className="source-kind">{source.isBuiltIn ? source.provider : 'свой'}</span><span title={source.lastError} className={source.lastError ? 'source-error' : 'source-ok'}>{source.lastError ? 'ошибка' : source.enabled ? 'активен' : 'пауза'}</span><button className="source-edit-button" disabled={adminMutationBusy} onClick={() => openSourceEditor(source)}><Pencil/>Изменить</button></div>
            </article>)}</div>{sourceTotal > 0 && <ProxyPagination page={sourcePage} pageSize={sourcePageSize} total={sourceTotal} totalPages={sourceTotalPages} onPageChange={next => { setSourcePage(next); void loadAdminData(false, next, sourcePageSize); document.getElementById('admin-sources-title')?.scrollIntoView?.({behavior:'smooth'}) }} onPageSizeChange={size => { setSourcePageSize(size); setSourcePage(1); void loadAdminData(false, 1, size) }}/>}</section>
          </section>}

          {adminSection === 'backups' && <section className="admin-section" aria-labelledby="admin-backups-title">
            <div className="admin-section-heading"><div><span className="kicker">ЗАЩИТА ДАННЫХ</span><h1 id="admin-backups-title">Резервные копии</h1><p>Создавайте зашифрованные снимки базы данных и контролируйте доставку в Telegram.</p></div><button className="primary-admin-button" onClick={() => runAdminAction('backup')} disabled={!adminAuthenticated || adminMutationBusy}><Database/>{action === 'backup' ? 'Создаём…' : 'Создать backup'}</button></div>
            <div className="backup-summary"><article><span><Database/></span><div><small>Размер базы</small><strong>{formatBytes(diagnostics?.databaseBytes)}</strong></div></article><article><span><HardDriveDownload/></span><div><small>Последняя копия</small><strong>{latestBackup ? formatBytes(latestBackup.sizeBytes) : '—'}</strong></div></article><article><span><ShieldCheck/></span><div><small>Доставка</small><strong>{latestBackup ? backupDelivery(latestBackup) : 'Нет данных'}</strong></div></article></div>
            <AdminRunHistory title="История резервного копирования" empty="Резервные копии ещё не создавались.">{diagnostics?.recentBackups.map(run => <HistoryRow key={run.id} status={run.status} title={run.fileName ?? 'Резервная копия'} detail={`${formatBytes(run.sizeBytes)} · ${backupDelivery(run)}`} time={run.startedAt}/>)}</AdminRunHistory>
          </section>}
          {adminSection === 'users' && <AdminUsersPage/>}
          {adminSection === 'payments' && <AdminPaymentsPage/>}
          {adminSection === 'telegram' && <AdminTelegramPage/>}
          {adminSection === 'subscriptions' && <AdminSubscriptionsPage/>}
          {adminSection === 'access' && <AdminAccessPage/>}
        </>}
      </section>
    </main>}

    {sourceEditorOpen && <div className="source-editor-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeSourceEditor() }}>
      <section className="source-editor-modal" role="dialog" aria-modal="true" aria-labelledby="source-editor-title">
        <div className="source-editor-heading"><div><span className="kicker">{editingSource ? 'НАСТРОЙКА FEED' : 'НОВЫЙ FEED'}</span><h2 id="source-editor-title">{editingSource ? 'Редактировать источник' : 'Добавить источник'}</h2><p>{editingSource?.isBuiltIn ? 'У встроенного источника можно изменить только активность.' : 'Укажите публичный HTTPS-адрес и параметры разбора списка прокси.'}</p></div><button type="button" className="icon-button" aria-label="Закрыть редактор источника" onClick={closeSourceEditor} disabled={!!sourceBusy}><X/></button></div>
        {adminError && <div className="admin-notice source-editor-notice" role="alert"><X size={16}/>{adminError}</div>}
        <form className="source-editor-form" onSubmit={saveSource}>
          <label>Название<input autoFocus required minLength={2} maxLength={120} disabled={editingSource?.isBuiltIn} value={sourceDraft.name} onChange={event => setSourceDraft({...sourceDraft, name:event.target.value})}/></label>
          <label>HTTPS URL<input required type="url" maxLength={2048} pattern="https://.*" disabled={editingSource?.isBuiltIn} placeholder="https://example.org/proxies.txt" value={sourceDraft.url} onChange={event => setSourceDraft({...sourceDraft, url:event.target.value})}/></label>
          <div className="source-editor-grid"><label>Протокол<select disabled={editingSource?.isBuiltIn} value={sourceDraft.protocol} onChange={event => setSourceDraft({...sourceDraft, protocol:event.target.value as Protocol})}>{protocols.map(item => <option key={item} value={item}>{label(item)}</option>)}</select></label><label>Приоритет<input type="number" min={-10000} max={10000} disabled={editingSource?.isBuiltIn} value={sourceDraft.priority} onChange={event => setSourceDraft({...sourceDraft, priority:Number(event.target.value)})}/></label></div>
          <label className="source-enabled"><input type="checkbox" checked={sourceDraft.enabled} onChange={event => setSourceDraft({...sourceDraft, enabled:event.target.checked})}/><span><b>Источник активен</b><small>Активные источники участвуют в очередном цикле сбора.</small></span></label>
          {sourceDeleteConfirm && editingSource && <div className="source-delete-confirm" role="alert"><div><b>{editingSource.isBuiltIn ? 'Отключить встроенный источник?' : 'Удалить источник безвозвратно?'}</b><p>{editingSource.isBuiltIn ? 'Он останется в каталоге и его можно будет включить позже.' : 'Запись источника будет удалена. Уже собранные прокси сохранятся в базе.'}</p></div><button type="button" onClick={() => setSourceDeleteConfirm(false)} disabled={!!sourceBusy}>Отмена</button><button type="button" className="danger" onClick={() => void removeSource(editingSource)} disabled={!!sourceBusy}>{sourceBusy ? 'Выполняем…' : editingSource.isBuiltIn ? 'Отключить' : 'Удалить'}</button></div>}
          <div className="source-editor-actions">{editingSource && !sourceDeleteConfirm && <button type="button" className="danger-link" onClick={() => setSourceDeleteConfirm(true)} disabled={!!sourceBusy}><Trash2/>{editingSource.isBuiltIn ? 'Отключить источник' : 'Удалить источник'}</button>}<span/><button type="button" className="secondary-admin-button" onClick={closeSourceEditor} disabled={!!sourceBusy}>Отмена</button><button type="submit" className="primary-admin-button" disabled={!adminAuthenticated || adminMutationBusy}>{sourceBusy ? 'Сохраняем…' : editingSource ? 'Сохранить изменения' : 'Добавить источник'}</button></div>
        </form>
      </section>
    </div>}
  </div>
}

/** Компактная строка последней административной активности. */
function AdminActivity({icon, title, status, detail, time}: {icon: React.ReactNode; title: string; status?: string; detail: string; time?: string}) {
  return <article><span className="activity-icon">{icon}</span><div><b>{title}</b><small>{detail}</small></div><div className="activity-state"><i className={statusClass(status)}/><span>{status === 'attention' ? 'внимание' : statusLabel(status)}</span><time>{time ? timeAgo(time) : '—'}</time></div></article>
}

/** Унифицированный контейнер истории запуска для разделов операций и backup. */
function AdminRunHistory({title, empty, children}: {title: string; empty: string; children?: React.ReactNode}) {
  const hasChildren = Array.isArray(children) ? children.length > 0 : Boolean(children)
  return <section className="admin-card run-history"><div className="card-heading"><div><span className="kicker">ИСТОРИЯ</span><h2>{title}</h2></div></div><div>{hasChildren ? children : <p className="empty-state">{empty}</p>}</div></section>
}

/** Одинаковое представление результата фонового запуска во всех разделах. */
function HistoryRow({status, title, detail, time}: {status?: string; title: string; detail: string; time: string}) {
  return <article><i className={statusClass(status)}/><div><b>{title}</b><small>{detail}</small></div><time>{timeAgo(time)}</time></article>
}

/** Общая страница входа для администраторов и будущих клиентов. */
function AccountLoginPage() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!username || !password || busy) return
    setBusy(true)
    setError('')
    try {
      const response = await fetch(`${API}/api/v1/auth/login`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password }),
      })
      if (!response.ok) throw new Error(await responseMessage(response, 'Неверный логин, email или пароль'))
      const session = await response.json() as { roles?: string[] }
      window.location.assign(session.roles?.includes('Administrator') ? '/admin' : '/account')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Не удалось выполнить вход')
      setBusy(false)
    }
  }

  return <main className="login-page">
    <a className="brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
    <section className="login-card" aria-labelledby="login-title">
      <span className="kicker">ACCOUNT ACCESS</span>
      <h1 id="login-title">Вход в ProxyHarbor</h1>
      <p>Используйте логин или email. Одна защищённая сессия открывает личный кабинет в соответствии с вашими правами.</p>
      <form className="login-form" onSubmit={submit}>
        <label htmlFor="account-identifier">Логин или email</label>
        <div className="login-field"><User size={18}/><input id="account-identifier" autoFocus required type="text" placeholder="login или name@example.com" autoComplete="username" autoCapitalize="none" spellCheck={false} minLength={3} maxLength={254} value={username} onChange={event => setUsername(event.target.value)}/></div>
        <label htmlFor="admin-password">Пароль</label>
        <div className="login-field"><LockKeyhole size={18}/><input id="admin-password" required type={showPassword ? 'text' : 'password'} placeholder="Пароль" autoComplete="current-password" maxLength={256} value={password} onChange={event => setPassword(event.target.value)}/><button className="password-toggle" type="button" aria-label={showPassword ? 'Скрыть пароль' : 'Показать пароль'} onClick={() => setShowPassword(value => !value)}>{showPassword ? <EyeOff/> : <Eye/>}</button></div>
        <a className="forgot-link" href="/forgot-password">Забыли пароль?</a>
        <button className="login-submit" type="submit" disabled={busy || !username || !password}>{busy ? 'Проверяем…' : 'Войти'}</button>
      </form>
      {error && <div className="admin-notice" role="alert"><X size={16}/>{error}</div>}
      <div className="account-auth-footer"><span>Ещё нет аккаунта?</span><a href="/register">Зарегистрироваться</a></div>
      <a className="back-link" href="/">← Вернуться на главную</a>
    </section>
  </main>
}

/** Самостоятельная регистрация создаёт только безопасную базовую роль User и free-подписку. */
function RegisterPage() {
  const [form, setForm] = useState({ username: '', email: '', displayName: '', password: '', confirm: '' })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (form.password !== form.confirm) { setError('Пароли не совпадают'); return }
    setBusy(true); setError('')
    try {
      const response = await fetch(`${API}/api/v1/auth/register`, { method: 'POST', credentials: 'include', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(form) })
      if (!response.ok) throw new Error(await responseMessage(response, 'Не удалось создать аккаунт'))
      window.location.assign('/account')
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Не удалось создать аккаунт'); setBusy(false) }
  }
  return <AuthLayout title="Создать аккаунт" kicker="FREE ACCOUNT" description="Бесплатный аккаунт уже готов к будущим тарифам и персональным лимитам.">
    <form className="login-form registration-form" onSubmit={submit}>
      <label htmlFor="register-name">Как к вам обращаться</label><div className="login-field"><User/><input id="register-name" maxLength={120} autoComplete="name" placeholder="Имя (необязательно)" value={form.displayName} onChange={event => setForm({...form, displayName: event.target.value})}/></div>
      <label htmlFor="register-username">Логин</label><div className="login-field"><User/><input id="register-username" required minLength={3} maxLength={64} pattern="[A-Za-z0-9._-]+" autoComplete="username" placeholder="proxy.user" value={form.username} onChange={event => setForm({...form, username: event.target.value})}/></div>
      <label htmlFor="register-email">Email</label><div className="login-field"><Mail/><input id="register-email" required type="email" maxLength={254} autoComplete="email" placeholder="name@example.com" value={form.email} onChange={event => setForm({...form, email: event.target.value})}/></div>
      <label htmlFor="register-password">Пароль</label><div className="login-field"><LockKeyhole/><input id="register-password" required minLength={12} maxLength={256} type="password" autoComplete="new-password" placeholder="Не менее 12 символов" value={form.password} onChange={event => setForm({...form, password: event.target.value})}/></div>
      <label htmlFor="register-confirm">Повторите пароль</label><div className="login-field"><ShieldCheck/><input id="register-confirm" required type="password" autoComplete="new-password" placeholder="Повторите пароль" value={form.confirm} onChange={event => setForm({...form, confirm: event.target.value})}/></div>
      <button className="login-submit" disabled={busy}>{busy ? 'Создаём…' : 'Создать аккаунт'}</button>
    </form>{error && <div className="admin-notice" role="alert"><X/>{error}</div>}<a className="back-link" href="/login">← Уже есть аккаунт</a>
  </AuthLayout>
}

/** Запрос восстановления всегда показывает нейтральный результат без раскрытия аккаунта. */
function ForgotPasswordPage() {
  const [email, setEmail] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const submit = async (event: React.FormEvent) => {
    event.preventDefault(); setError(''); setMessage('')
    const response = await fetch(`${API}/api/v1/auth/forgot-password`, { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({email}) })
    if (!response.ok) { setError(await responseMessage(response, 'Не удалось отправить письмо')); return }
    setMessage('Если аккаунт существует, ссылка уже отправлена на указанную почту.')
  }
  return <AuthLayout title="Восстановить пароль" kicker="ACCOUNT RECOVERY" description="Укажите email аккаунта — мы отправим одноразовую защищённую ссылку.">
    <form className="login-form" onSubmit={submit}><label htmlFor="recovery-email">Email</label><div className="login-field"><Mail/><input id="recovery-email" required type="email" autoComplete="email" placeholder="name@example.com" value={email} onChange={event => setEmail(event.target.value)}/></div><button className="login-submit">Отправить ссылку</button></form>
    {message && <div className="account-success" role="status"><Check/>{message}</div>}{error && <div className="admin-notice" role="alert"><X/>{error}</div>}<a className="back-link" href="/login">← Вернуться ко входу</a>
  </AuthLayout>
}

/** Применяет email и token только из ссылки, а новый пароль вводится дважды. */
function ResetPasswordPage() {
  const query = new URLSearchParams(window.location.search)
  const email = query.get('email') ?? ''
  const token = query.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const submit = async (event: React.FormEvent) => {
    event.preventDefault(); setError('')
    if (!email || !token) { setError('Ссылка восстановления неполная'); return }
    if (password !== confirm) { setError('Пароли не совпадают'); return }
    const response = await fetch(`${API}/api/v1/auth/reset-password`, { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({email, token, newPassword: password}) })
    if (!response.ok) { setError(await responseMessage(response, 'Ссылка недействительна или устарела')); return }
    setMessage('Пароль изменён. Теперь можно войти в аккаунт.')
  }
  return <AuthLayout title="Новый пароль" kicker="SECURE RESET" description={email ? `Восстановление для ${email}` : 'Проверьте полноту ссылки из письма.'}>
    {!message && <form className="login-form" onSubmit={submit}><label htmlFor="reset-password">Новый пароль</label><div className="login-field"><LockKeyhole/><input id="reset-password" required minLength={12} type="password" autoComplete="new-password" value={password} onChange={event => setPassword(event.target.value)}/></div><label htmlFor="reset-confirm">Повторите пароль</label><div className="login-field"><ShieldCheck/><input id="reset-confirm" required type="password" autoComplete="new-password" value={confirm} onChange={event => setConfirm(event.target.value)}/></div><button className="login-submit">Изменить пароль</button></form>}
    {message && <div className="account-success"><Check/>{message}</div>}{error && <div className="admin-notice" role="alert"><X/>{error}</div>}<a className="back-link" href="/login">← Перейти ко входу</a>
  </AuthLayout>
}

/** Единая оболочка auth-экранов сохраняет визуальный ритм и семантику заголовков. */
function AuthLayout({title, kicker, description, children}: {title: string; kicker: string; description: string; children: React.ReactNode}) {
  return <main className="login-page"><a className="brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a><section className="login-card account-auth-card" aria-labelledby="auth-title"><span className="kicker">{kicker}</span><h1 id="auth-title">{title}</h1><p>{description}</p>{children}</section></main>
}

/** Личный кабинет работает для любой роли и не запрашивает административные API. */
function AccountPage() {
  const [profile, setProfile] = useState<AccountProfile | null>(null)
  const [displayName, setDisplayName] = useState('')
  const [passwords, setPasswords] = useState({currentPassword: '', newPassword: ''})
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [payments, setPayments] = useState<PaymentCatalog | null>(null)
  const [orders, setOrders] = useState<PaymentOrder[]>([])
  const [checkoutBusy, setCheckoutBusy] = useState('')
  const loadProfile = useCallback(async () => {
    const [response, catalogResponse, ordersResponse] = await Promise.all([
      fetch(`${API}/api/v1/account/profile`, {credentials: 'include'}),
      fetch(`${API}/api/v1/payments/catalog`, {credentials: 'include'}),
      fetch(`${API}/api/v1/payments/orders`, {credentials: 'include'}),
    ])
    if (response.status === 401) { window.location.replace('/login'); return }
    if (!response.ok) { setError(await responseMessage(response, 'Профиль недоступен')); return }
    const data = await response.json() as AccountProfile; setProfile(data); setDisplayName(data.displayName ?? '')
    if (catalogResponse.ok) setPayments(await catalogResponse.json() as PaymentCatalog)
    if (ordersResponse.ok) setOrders(await ordersResponse.json() as PaymentOrder[])
  }, [])
  useEffect(() => { const initial = window.setTimeout(() => void loadProfile(), 0); return () => window.clearTimeout(initial) }, [loadProfile])
  const saveProfile = async (event: React.FormEvent) => { event.preventDefault(); setError(''); const response = await fetch(`${API}/api/v1/account/profile`, {method:'PUT', credentials:'include', headers:{'Content-Type':'application/json'}, body:JSON.stringify({displayName})}); if (!response.ok) {setError(await responseMessage(response,'Не удалось сохранить профиль'));return} setNotice('Профиль сохранён'); await loadProfile() }
  const changePassword = async (event: React.FormEvent) => { event.preventDefault(); setError(''); const response = await fetch(`${API}/api/v1/account/change-password`, {method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(passwords)}); if(!response.ok){setError(await responseMessage(response,'Не удалось изменить пароль'));return} setPasswords({currentPassword:'',newPassword:''});setNotice('Пароль изменён. Другие сессии будут отозваны.') }
  const logout = async () => { await fetch(`${API}/api/v1/auth/logout`, {method:'POST',credentials:'include'}); window.location.replace('/login') }
  const checkout = async (productCode:string, provider:string) => { const key=`${productCode}:${provider}`;setCheckoutBusy(key);setError('');const response=await fetch(`${API}/api/v1/payments/checkout`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({productCode,provider})});if(!response.ok){setError(await responseMessage(response,'Не удалось открыть оплату'));setCheckoutBusy('');return}const result=await response.json() as {checkoutUrl:string};window.location.assign(result.checkoutUrl) }
  return <main className="account-page"><header><a className="brand" href="/"><span className="brand-mark"><Network/></span><span>Proxy<span>Harbor</span></span></a><div><a href="/">На главную</a>{profile?.roles.includes('Administrator') && <a href="/admin">Админ-панель</a>}<button onClick={logout}><LogOut/>Выйти</button></div></header><section className="account-container"><div className="admin-section-heading"><div><span className="kicker">PERSONAL ACCOUNT</span><h1>Профиль</h1><p>Учётные данные, безопасность и параметры подписки.</p></div></div>{error && <div className="admin-notice"><X/>{error}</div>}{notice && <div className="account-success"><Check/>{notice}</div>}
    <div className="account-profile-grid"><section className="admin-card profile-card"><div className="profile-avatar"><User/></div><h2>{profile?.displayName || profile?.userName || 'Загрузка…'}</h2><p>{profile?.email}</p><div className="role-badges">{profile?.roles.map(role => <span key={role}>{role}</span>)}</div></section><section className="admin-card subscription-card"><span className="kicker">ПОДПИСКА</span><CreditCard/><strong>{planLabel(profile?.subscription?.plan)}</strong><p>{profile?.subscription ? `Статус: ${profile.subscription.status}` : 'Данные загружаются'}</p></section></div>
    <div className="account-forms"><section className="admin-card"><h2>Данные профиля</h2><form onSubmit={saveProfile}><label htmlFor="profile-login">Логин</label><input id="profile-login" disabled value={profile?.userName ?? ''}/><label htmlFor="profile-email">Email</label><input id="profile-email" disabled value={profile?.email ?? ''}/><label htmlFor="profile-name">Отображаемое имя</label><input id="profile-name" maxLength={120} value={displayName} onChange={event=>setDisplayName(event.target.value)}/><button>Сохранить</button></form></section><section className="admin-card"><h2>Сменить пароль</h2><form onSubmit={changePassword}><label htmlFor="current-password">Текущий пароль</label><input id="current-password" required type="password" autoComplete="current-password" value={passwords.currentPassword} onChange={event=>setPasswords({...passwords,currentPassword:event.target.value})}/><label htmlFor="new-password">Новый пароль</label><input id="new-password" required minLength={12} type="password" autoComplete="new-password" value={passwords.newPassword} onChange={event=>setPasswords({...passwords,newPassword:event.target.value})}/><button>Изменить пароль</button></form></section></div>
    <section className="admin-card billing-card"><span className="kicker">BILLING</span><h2>Тарифы и оплата</h2><p>Оплата проходит на защищённой странице выбранного сервиса. ProxyHarbor не получает и не хранит реквизиты карты.</p>
      <div className="payment-products">{payments?.products.map(product=><article key={product.code}><div><strong>{product.name}</strong><p>{product.description}</p><b>{money(product.amountMinor,product.currency)}</b></div><div className="payment-providers">{payments.providers.map(provider=><button key={provider.code} disabled={!provider.available||!!checkoutBusy} title={provider.available?'Перейти к защищённой оплате':'Провайдер ещё не подключён'} onClick={()=>void checkout(product.code,provider.code)}>{checkoutBusy===`${product.code}:${provider.code}`?'Открываем…':provider.name}</button>)}</div></article>)}</div>
      {!payments?.enabled&&<div className="billing-pending"><Clock3/>Приём платежей подготовлен, но merchant-аккаунты ещё не подключены.</div>}
      {orders.length>0&&<div className="payment-history"><h3>История платежей</h3>{orders.map(order=><div key={order.id}><span>{planLabel(order.plan)} · {providerLabel(order.provider)}</span><b>{money(order.amountMinor,order.currency)}</b><em className={`payment-status ${order.status}`}>{paymentStatusLabel(order.status)}</em><time>{new Date(order.createdAt).toLocaleDateString('ru-RU')}</time></div>)}</div>}
    </section>
  </section></main>
}

/** Управление ролями и тарифом остаётся отдельным административным разделом. */
function AdminUsersPage() {
  const [items, setItems] = useState<AdminUser[]>([])
  const [drafts, setDrafts] = useState<Record<string,UserAccessDraft>>({})
  const [error, setError] = useState('')
  const [busy, setBusy] = useState('')
  const loadUsers = useCallback(async () => { const response=await fetch(`${API}/api/v1/admin/users?pageSize=50`,{credentials:'include'}); if(!response.ok){setError(await responseMessage(response,'Пользователи недоступны'));return} const data=await response.json() as PagedResult<AdminUser>;setItems(data.items);setDrafts(Object.fromEntries(data.items.map(user=>[user.id,{isActive:user.isActive,administrator:user.roles.includes('Administrator'),subscriber:user.roles.includes('Subscriber'),plan:user.subscription?.plan??'free',status:user.subscription?.status??'active'}]))) },[])
  useEffect(()=>{const initial=window.setTimeout(()=>void loadUsers(),0);return()=>window.clearTimeout(initial)},[loadUsers])
  const save = async (user:AdminUser) => { const draft=drafts[user.id];if(!draft)return;setBusy(user.id);setError('');const roles=['User',...(draft.subscriber?['Subscriber']:[]),...(draft.administrator?['Administrator']:[])];const response=await fetch(`${API}/api/v1/admin/users/${user.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({isActive:draft.isActive,roles,plan:draft.plan,status:draft.status,expiresAt:user.subscription?.expiresAt??null})});if(!response.ok)setError(await responseMessage(response,'Не удалось обновить права'));else await loadUsers();setBusy('') }
  const update=(id:string,patch:Partial<UserAccessDraft>)=>setDrafts(current=>({...current,[id]:{...current[id],...patch}}))
  return <section className="admin-section" aria-labelledby="admin-users-title"><div className="admin-section-heading"><div><span className="kicker">ACCESS CONTROL</span><h1 id="admin-users-title">Пользователи</h1><p>Роли отвечают за доступ к функциям, а тариф — за будущие лимиты выдачи прокси.</p></div><span className="section-count">{items.length}</span></div>{error&&<div className="admin-notice"><X/>{error}</div>}<section className="admin-card users-card"><div className="user-list">{items.map(user=>{const draft=drafts[user.id];return <article key={user.id}><div className="user-identity"><span><User/></span><div><b>{user.displayName||user.userName}</b><small>{user.email} · создан {new Date(user.createdAt).toLocaleDateString('ru-RU')}</small></div></div>{draft&&<div className="user-access-controls"><label><input type="checkbox" checked={draft.isActive} onChange={e=>update(user.id,{isActive:e.target.checked})}/> Активен</label><label><input type="checkbox" checked={draft.subscriber} onChange={e=>update(user.id,{subscriber:e.target.checked})}/> Subscriber</label><label><input type="checkbox" checked={draft.administrator} onChange={e=>update(user.id,{administrator:e.target.checked})}/> Admin</label><select aria-label={`Тариф ${user.userName}`} value={draft.plan} onChange={e=>update(user.id,{plan:e.target.value})}><option value="free">Free</option><option value="pro">Pro</option><option value="unlimited">Unlimited</option></select><select aria-label={`Статус ${user.userName}`} value={draft.status} onChange={e=>update(user.id,{status:e.target.value})}><option value="active">Active</option><option value="trialing">Trial</option><option value="past_due">Past due</option><option value="canceled">Canceled</option><option value="expired">Expired</option></select><button disabled={!!busy} onClick={()=>void save(user)}>{busy===user.id?'Сохраняем…':'Сохранить'}</button></div>}</article>})}</div></section></section>
}

/** Управление Telegram Bot API, Stars и встроенной CRM из отдельного раздела панели. */
function AdminTelegramPage(){
  const [tab,setTab]=useState<'overview'|'settings'|'chats'>('overview')
  const [settings,setSettings]=useState<TelegramSettings|null>(null)
  const [draft,setDraft]=useState<TelegramSettings|null>(null)
  const [products,setProducts]=useState<AdminPaymentProduct[]>([])
  const [tokenValue,setTokenValue]=useState('')
  const [chats,setChats]=useState<PagedResult<TelegramChat>|null>(null)
  const [page,setPage]=useState(1);const [pageSize,setPageSize]=useState(10);const [query,setQuery]=useState('')
  const [selected,setSelected]=useState<TelegramChat|null>(null)
  const [messages,setMessages]=useState<TelegramMessage[]>([])
  const [message,setMessage]=useState('');const [broadcast,setBroadcast]=useState('')
  const [busy,setBusy]=useState('');const [error,setError]=useState('');const [notice,setNotice]=useState('')
  const load=useCallback(async()=>{try{const [configResponse,paymentResponse]=await Promise.all([fetch(`${API}/api/v1/admin/telegram`,{credentials:'include'}),fetch(`${API}/api/v1/admin/payments`,{credentials:'include'})]);if(!configResponse.ok)throw new Error(await responseMessage(configResponse,'Настройки Telegram недоступны'));const config=await configResponse.json() as TelegramSettings;setSettings(config);setDraft(config);if(paymentResponse.ok){const payment=await paymentResponse.json() as AdminPaymentSettings;setProducts(payment.products.filter(x=>x.enabled))}setError('')}catch(reason){setError(reason instanceof Error?reason.message:'Настройки Telegram недоступны')}},[])
  const loadChats=useCallback(async()=>{const params=new URLSearchParams({page:String(page),pageSize:String(pageSize)});if(query.trim())params.set('query',query.trim());const response=await fetch(`${API}/api/v1/admin/telegram/chats?${params}`,{credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Диалоги Telegram недоступны'));return}setChats(await response.json() as PagedResult<TelegramChat>)},[page,pageSize,query])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load]);useEffect(()=>{if(tab!=='chats')return;const timer=window.setTimeout(()=>void loadChats(),0);return()=>window.clearTimeout(timer)},[tab,loadChats])
  const save=async()=>{if(!draft)return;setBusy('save');setNotice('');const response=await fetch(`${API}/api/v1/admin/telegram`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:draft.enabled,updateMode:draft.updateMode,name:draft.name,description:draft.description,shortDescription:draft.shortDescription,supportText:draft.supportText,proxyFileMaxItems:draft.proxyFileMaxItems,webhookMaxConnections:draft.webhookMaxConnections,productStars:draft.productStars,botToken:tokenValue.trim()||null})});if(!response.ok)setError(await responseMessage(response,'Telegram-бот не сохранён'));else{const value=await response.json() as TelegramSettings;setSettings(value);setDraft(value);setTokenValue('');setError('');setNotice('Профиль, команды, изображение и режим доставки настроены автоматически.')}setBusy('')}
  const provision=async()=>{setBusy('provision');const response=await fetch(`${API}/api/v1/admin/telegram/provision`,{method:'POST',credentials:'include'});if(!response.ok)setError(await responseMessage(response,'Повторная настройка не выполнена'));else{await load();setNotice('Настройки Telegram применены повторно.')}setBusy('')}
  const openChat=async(chat:TelegramChat)=>{setSelected(chat);setMessages([]);const response=await fetch(`${API}/api/v1/admin/telegram/chats/${chat.id}/messages?take=100`,{credentials:'include'});if(response.ok)setMessages(await response.json() as TelegramMessage[])}
  const send=async(isBroadcast:boolean)=>{const text=isBroadcast?broadcast:message;if(!text.trim())return;setBusy(isBroadcast?'broadcast':'message');const response=await fetch(`${API}/api/v1/admin/telegram/messages`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({chatId:isBroadcast?null:selected?.id,broadcast:isBroadcast,text})});if(!response.ok)setError(await responseMessage(response,'Сообщение не поставлено в очередь'));else{if(isBroadcast){setBroadcast('');setNotice('Рассылка поставлена в безопасную очередь с соблюдением лимитов Telegram.')}else{setMessage('');if(selected)await openChat(selected)}}setBusy('')}
  const updateChat=async(chat:TelegramChat,patch:Partial<TelegramChat>)=>{const next={...chat,...patch};const response=await fetch(`${API}/api/v1/admin/telegram/chats/${chat.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({notificationsEnabled:next.notificationsEnabled,isBlocked:next.isBlocked})});if(response.ok){setSelected(next);await loadChats()}else setError(await responseMessage(response,'Состояние чата не обновлено'))}
  const stats=settings?.stats
  return <section className="admin-section telegram-admin" aria-labelledby="admin-telegram-title">
    <div className="admin-section-heading"><div><span className="kicker">TELEGRAM COMMERCE</span><h1 id="admin-telegram-title">Telegram-бот</h1><p>Продажи через Stars, личный кабинет, выдача прокси и сообщения клиентам.</p></div><span className={`telegram-health ${settings?.enabled?'online':''}`}><Radio/>{settings?.enabled?'Работает':'Отключён'}</span></div>
    {error&&<div className="admin-notice" role="alert"><X/>{error}</div>}{notice&&<div className="admin-notice telegram-success" role="status"><Check/>{notice}</div>}
    <AdminTabs value={tab} onChange={value=>setTab(value as typeof tab)} items={[["overview","Обзор"],["settings","Настройки"],["chats","CRM и сообщения"]]}/>
    {tab==='overview'&&<><div className="admin-summary-grid telegram-summary"><article><span className="summary-icon"><Users/></span><div><small>Пользователи</small><strong>{stats?.users??'—'}</strong><p>{stats?.activeUsers30d??'—'} активны за 30 дней</p></div></article><article><span className="summary-icon"><Star/></span><div><small>Выручка Stars</small><strong>{stats?.starsRevenue??'—'} ⭐</strong><p>{stats?.paidOrders??'—'} успешных оплат</p></div></article><article><span className="summary-icon"><Bell/></span><div><small>Уведомления</small><strong>{stats?.notificationsEnabled??'—'}</strong><p>{stats?.blocked??'—'} остановили бота</p></div></article><article><span className="summary-icon"><Send/></span><div><small>Очередь</small><strong>{stats?.queued??'—'}</strong><p>{stats?.failed??'—'} требуют внимания</p></div></article></div><section className="admin-card telegram-status-card"><div><span className="kicker">ПОДКЛЮЧЕНИЕ</span><h2>{settings?.botUsername?`@${settings.botUsername}`:'Бот ещё не подключён'}</h2><p>{settings?.tokenConfigured?'Token надёжно зашифрован и не передаётся в браузер.':'Откройте настройки и укажите token из BotFather.'}</p></div><dl><div><dt>Доставка</dt><dd>{settings?.updateMode==='webhook'?'Webhook':'Long polling'}</dd></div><div><dt>Webhook</dt><dd>{settings?.webhookUrl||'—'}</dd></div><div><dt>Последняя настройка</dt><dd>{settings?.provisionedAt?new Date(settings.provisionedAt).toLocaleString('ru-RU'):'—'}</dd></div></dl><button className="secondary-admin-button" disabled={!settings?.tokenConfigured||!!busy} onClick={()=>void provision()}><RefreshCw className={busy==='provision'?'spin':''}/>Применить заново</button></section></>}
    {tab==='settings'&&draft&&<section className="admin-card telegram-settings-card"><div className="card-heading"><div><span className="kicker">BOT API</span><h2>Профиль и автоматизация</h2></div><Toggle checked={draft.enabled} onChange={enabled=>setDraft({...draft,enabled})} label={draft.enabled?'Бот включён':'Бот выключен'}/></div><div className="telegram-form"><label>Token BotFather<input type="password" autoComplete="new-password" value={tokenValue} placeholder={draft.tokenConfigured?'Сохранён — оставьте пустым':'123456:ABC…'} onChange={event=>setTokenValue(event.target.value)}/><small>После сохранения система проверит token и сама установит имя, описание, иконку, команды и webhook.</small></label><label>Режим доставки<StyledSelect value={draft.updateMode} onChange={value=>setDraft({...draft,updateMode:value as 'webhook'|'polling'})} options={[["webhook","Webhook — рекомендуется"],["polling","Long polling"]]}/></label><label>Имя<input minLength={2} maxLength={64} value={draft.name} onChange={event=>setDraft({...draft,name:event.target.value})}/></label><label>Короткое описание<input minLength={5} maxLength={120} value={draft.shortDescription} onChange={event=>setDraft({...draft,shortDescription:event.target.value})}/></label><label className="wide">Полное описание<textarea minLength={10} maxLength={512} value={draft.description} onChange={event=>setDraft({...draft,description:event.target.value})}/></label><label className="wide">Ответ поддержки<textarea minLength={5} maxLength={1000} value={draft.supportText} onChange={event=>setDraft({...draft,supportText:event.target.value})}/></label><label>Прокси в TXT-файле<input type="number" min={1} max={10000} value={draft.proxyFileMaxItems} onChange={event=>setDraft({...draft,proxyFileMaxItems:Number(event.target.value)})}/></label><label>Webhook connections<input type="number" min={1} max={100} value={draft.webhookMaxConnections} onChange={event=>setDraft({...draft,webhookMaxConnections:Number(event.target.value)})}/></label></div><div className="telegram-stars"><h3>Цены Telegram Stars</h3>{products.length===0?<p>Сначала включите тарифы в разделе «Оплата».</p>:products.map(product=><label key={product.code}><span><b>{product.name}</b><small>{product.durationDays} дней · {product.code}</small></span><input type="number" min={1} max={1000000} value={draft.productStars[product.code]??''} onChange={event=>{const value=Number(event.target.value);const next={...draft.productStars};if(value>0)next[product.code]=value;else delete next[product.code];setDraft({...draft,productStars:next})}}/></label>)}</div><div className="telegram-save"><button className="primary-admin-button" disabled={!!busy} onClick={()=>void save()}><Bot/>{busy==='save'?'Настраиваем…':'Сохранить и настроить'}</button></div></section>}
    {tab==='chats'&&<><section className="admin-card telegram-broadcast"><div><span className="kicker">BROADCAST</span><h2>Сообщение всем</h2><p>Получат только подписанные и не заблокированные чаты; отправка идёт через очередь.</p></div><textarea maxLength={4096} value={broadcast} placeholder="Текст рассылки…" onChange={event=>setBroadcast(event.target.value)}/><button className="primary-admin-button" disabled={!broadcast.trim()||!!busy} onClick={()=>void send(true)}><Send/>{busy==='broadcast'?'Ставим в очередь…':'Отправить всем'}</button></section><section className="admin-card telegram-chat-list"><div className="card-heading"><div><span className="kicker">CRM</span><h2>Диалоги <em>{chats?.total??0}</em></h2></div><form onSubmit={event=>{event.preventDefault();setPage(1);void loadChats()}}><input value={query} placeholder="Имя, username или ID" onChange={event=>setQuery(event.target.value)}/><button className="icon-button" aria-label="Найти"><RefreshCw/></button></form></div>{chats?.items.map(chat=><button key={chat.id} className="telegram-chat-row" onClick={()=>void openChat(chat)}><span className="summary-icon"><MessageCircle/></span><span><b>{chat.displayName||chat.username||chat.chatId}</b><small>{chat.username?`@${chat.username} · `:''}{chat.messages} сообщений · {chat.subscription.plan}</small></span><em className={chat.isBlocked?'blocked':'active'}>{chat.isBlocked?'заблокирован':'активен'}</em><time>{new Date(chat.lastInteractionAt).toLocaleString('ru-RU')}</time></button>)}{chats&&chats.total>0&&<ProxyPagination page={page} pageSize={pageSize} total={chats.total} totalPages={Math.ceil(chats.total/pageSize)} onPageChange={setPage} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}</section></>}
    {selected&&<div className="source-editor-backdrop" onMouseDown={event=>{if(event.target===event.currentTarget)setSelected(null)}}><section className="source-editor-modal telegram-dialog" role="dialog" aria-modal="true"><div className="source-editor-heading"><div><span className="kicker">CRM DIALOG</span><h2>{selected.displayName||selected.username||selected.chatId}</h2><p>{selected.username?`@${selected.username} · `:''}{selected.subscription.plan} / {selected.subscription.status}</p></div><button className="icon-button" aria-label="Закрыть диалог" onClick={()=>setSelected(null)}><X/></button></div><div className="telegram-messages">{messages.length===0?<p>История пока пуста.</p>:messages.map(item=><article key={item.id} className={item.direction}><b>{item.direction==='inbound'?'Клиент':item.direction==='admin'?'Администратор':'Бот'}</b><p>{item.text}</p><time>{new Date(item.createdAt).toLocaleString('ru-RU')}</time></article>)}</div><div className="telegram-chat-flags"><Toggle checked={selected.notificationsEnabled} onChange={value=>void updateChat(selected,{notificationsEnabled:value})} label="Уведомления"/><Toggle checked={selected.isBlocked} onChange={value=>void updateChat(selected,{isBlocked:value})} label="Заблокирован" danger/></div><div className="telegram-reply"><textarea maxLength={4096} value={message} placeholder="Ответ клиенту…" onChange={event=>setMessage(event.target.value)}/><button className="primary-admin-button" disabled={!message.trim()||selected.isBlocked||!!busy} onClick={()=>void send(false)}><Send/>Отправить</button></div></section></div>}
  </section>
}

/** Runtime-настройка тарифов и шлюзов; сохранённые секреты никогда не загружаются в браузер. */
function AdminPaymentsPage() {
  const [settings, setSettings] = useState<AdminPaymentSettings | null>(null)
  const [providers, setProviders] = useState<AdminPaymentProviderDraft[]>([])
  const [tab,setTab]=useState<'overview'|'providers'|'invoices'|'tariffs'>('overview')
  const [providerOpen,setProviderOpen]=useState<string|null>(null)
  const [invoices,setInvoices]=useState<InvoicePage|null>(null)
  const [invoicePage,setInvoicePage]=useState(1)
  const [invoiceStatus,setInvoiceStatus]=useState('')
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [busy, setBusy] = useState(false)
  const loadSettings = useCallback(async () => {
    const response = await fetch(`${API}/api/v1/admin/payments`, {credentials:'include'})
    if (!response.ok) { setError(await responseMessage(response,'Настройки оплаты недоступны')); return }
    const value = await response.json() as AdminPaymentSettings
    setSettings(value)
    setProviders(value.providers.map(provider=>({...provider,secretKey:'',secondarySecret:'',clearSecretKey:false,clearSecondarySecret:false})))
    setError('')
  },[])
  useEffect(()=>{const initial=window.setTimeout(()=>void loadSettings(),0);return()=>window.clearTimeout(initial)},[loadSettings])
  const loadInvoices=useCallback(async()=>{const query=new URLSearchParams({page:String(invoicePage),pageSize:'10'});if(invoiceStatus)query.set('status',invoiceStatus);if(providerOpen)query.set('provider',providerOpen);const response=await fetch(`${API}/api/v1/admin/payments/orders?${query}`,{credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Счета недоступны'));return}setInvoices(await response.json() as InvoicePage)},[invoicePage,invoiceStatus,providerOpen])
  useEffect(()=>{if(tab==='overview'||tab==='invoices'||providerOpen){const timer=window.setTimeout(()=>void loadInvoices(),0);return()=>window.clearTimeout(timer)}},[tab,providerOpen,loadInvoices])
  const updateProduct=(code:string,patch:Partial<AdminPaymentProduct>)=>setSettings(current=>current?{...current,products:current.products.map(product=>product.code===code?{...product,...patch}:product)}:current)
  const updateProvider=(code:string,patch:Partial<AdminPaymentProviderDraft>)=>setProviders(current=>current.map(provider=>provider.code===code?{...provider,...patch}:provider))
  const save = async () => {
    if (!settings) return
    setBusy(true);setError('');setNotice('')
    const response=await fetch(`${API}/api/v1/admin/payments`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:settings.enabled,products:settings.products,providers:providers.map(provider=>({code:provider.code,enabled:provider.enabled,merchantId:provider.merchantId,publicId:provider.publicId,testMode:provider.testMode,secretKey:provider.secretKey||null,secondarySecret:provider.secondarySecret||null,clearSecretKey:provider.clearSecretKey,clearSecondarySecret:provider.clearSecondarySecret}))})})
    if(!response.ok){setError(await responseMessage(response,'Не удалось сохранить настройки'));setBusy(false);return}
    const value=await response.json() as AdminPaymentSettings
    setSettings(value);setProviders(value.providers.map(provider=>({...provider,secretKey:'',secondarySecret:'',clearSecretKey:false,clearSecondarySecret:false})));setNotice('Настройки применены без перезапуска сервиса.');setBusy(false)
  }
  return <section className="admin-section payment-settings" aria-labelledby="admin-payments-title">
    <div className="admin-section-heading"><div><span className="kicker">BILLING CONTROL</span><h1 id="admin-payments-title">Оплата</h1><p>Счета, тарифы и пять изолированных платёжных шлюзов.</p></div><button className="primary-admin-button" disabled={!settings||busy} onClick={()=>void save()}><ShieldCheck/>{busy?'Сохраняем…':'Сохранить изменения'}</button></div>
    {error&&<div className="admin-notice"><X/>{error}</div>}{notice&&<div className="account-success"><Check/>{notice}</div>}
    {!settings?<div className="admin-card payment-settings-loading"><RefreshCw className="spin"/>Загружаем настройки…</div>:<>
      <AdminTabs value={tab} onChange={value=>setTab(value as typeof tab)} items={[["overview","Обзор"],["providers","Провайдеры"],["invoices","Счета"],["tariffs","Тарифы"]]}/>
      {tab==='overview'&&<><section className="admin-card billing-master-switch"><div><span className="kicker">ГЛОБАЛЬНЫЙ СТАТУС</span><h2>Приём платежей</h2><p>Включайте после настройки хотя бы одного шлюза и проверки юридических данных.</p></div><Toggle checked={settings.enabled} onChange={checked=>setSettings({...settings,enabled:checked})} label={settings.enabled?'Включён':'Выключен'}/></section><div className="billing-overview-grid"><article className="admin-card"><Receipt/><small>Счетов в реестре</small><strong>{invoices?.total??'—'}</strong><button onClick={()=>setTab('invoices')}>Открыть <ArrowRight/></button></article><article className="admin-card"><CreditCard/><small>Готовых шлюзов</small><strong>{providers.filter(x=>x.ready).length} / {providers.length}</strong><button onClick={()=>setTab('providers')}>Настроить <ArrowRight/></button></article><article className="admin-card"><CalendarClock/><small>Активных тарифов</small><strong>{settings.products.filter(x=>x.enabled).length}</strong><button onClick={()=>setTab('tariffs')}>Изменить <ArrowRight/></button></article></div></>}
      {tab==='tariffs'&&<section className="admin-card payment-products-settings"><div className="card-heading"><div><span className="kicker">ТАРИФЫ</span><h2>Продукты подписки</h2></div></div><div>{settings.products.map(product=><article key={product.code}><Toggle checked={product.enabled} onChange={checked=>updateProduct(product.code,{enabled:checked})} label="Доступен"/><label>Название<input maxLength={120} value={product.name} onChange={event=>updateProduct(product.code,{name:event.target.value})}/></label><label>План<StyledSelect value={product.plan} onChange={value=>updateProduct(product.code,{plan:value})} options={[["pro","Pro"],["unlimited","Unlimited"]]}/></label><label>Цена, ₽<input type="number" min="0.01" max="10000000" step="0.01" value={(product.amountMinor/100).toString()} onChange={event=>updateProduct(product.code,{amountMinor:Math.round(Number(event.target.value)*100)})}/></label><label>Срок, дней<input type="number" min="1" max="3660" value={product.durationDays} onChange={event=>updateProduct(product.code,{durationDays:Number(event.target.value)})}/></label><label className="payment-description">Описание<input maxLength={300} value={product.description} onChange={event=>updateProduct(product.code,{description:event.target.value})}/></label></article>)}</div></section>}
      <section className="payment-provider-settings"><div className="card-heading"><div><span className="kicker">ПРОВАЙДЕРЫ</span><h2>Платёжные шлюзы</h2></div></div>{providers.map(provider=><article className="admin-card" key={provider.code}><header><div><CreditCard/><span><b>{provider.name}</b><small>{provider.ready?'Готов к работе':provider.secretConfigured?'Нужны дополнительные реквизиты':'Не настроен'}</small></span></div><label className="payment-enable"><input type="checkbox" checked={provider.enabled} onChange={event=>updateProvider(provider.code,{enabled:event.target.checked})}/><span>Включить</span></label></header><div className="provider-fields">{provider.code!=='stripe'&&provider.code!=='cloudpayments'&&<label>{merchantFieldLabel(provider.code)}<input autoComplete="off" maxLength={256} value={provider.merchantId} onChange={event=>updateProvider(provider.code,{merchantId:event.target.value})}/></label>}{provider.code==='cloudpayments'&&<label>Public ID<input autoComplete="off" maxLength={256} value={provider.publicId} onChange={event=>updateProvider(provider.code,{publicId:event.target.value})}/></label>}<label>{primarySecretLabel(provider.code)}<input type="password" autoComplete="new-password" maxLength={4096} placeholder={provider.secretConfigured?'Сохранён · введите для замены':'Введите секрет'} value={provider.secretKey} onChange={event=>updateProvider(provider.code,{secretKey:event.target.value,clearSecretKey:false})}/><small>{provider.secretConfigured?'Секрет настроен и скрыт':'Секрет ещё не задан'}</small></label>{needsSecondarySecret(provider.code)&&<label>{secondarySecretLabel(provider.code)}<input type="password" autoComplete="new-password" maxLength={4096} placeholder={provider.secondarySecretConfigured?'Сохранён · введите для замены':'Введите второй секрет'} value={provider.secondarySecret} onChange={event=>updateProvider(provider.code,{secondarySecret:event.target.value,clearSecondarySecret:false})}/><small>{provider.secondarySecretConfigured?'Второй секрет настроен и скрыт':'Второй секрет ещё не задан'}</small></label>}</div><footer><code>{provider.webhookUrl}</code><div>{provider.secretConfigured&&<label className="clear-secret"><input type="checkbox" checked={provider.clearSecretKey} onChange={event=>updateProvider(provider.code,{clearSecretKey:event.target.checked,secretKey:''})}/>Удалить основной секрет</label>}{provider.secondarySecretConfigured&&<label className="clear-secret"><input type="checkbox" checked={provider.clearSecondarySecret} onChange={event=>updateProvider(provider.code,{clearSecondarySecret:event.target.checked,secondarySecret:''})}/>Удалить второй секрет</label>}{provider.code==='robokassa'&&<label className="payment-enable"><input type="checkbox" checked={provider.testMode} onChange={event=>updateProvider(provider.code,{testMode:event.target.checked})}/><span>Тестовый режим</span></label>}</div></footer></article>)}</section>
      {tab==='providers'&&<ProviderCards providers={providers} onOpen={code=>{setInvoices(null);setProviderOpen(code)}}/>}
      {tab==='invoices'&&<InvoiceRegistry data={invoices} status={invoiceStatus} onStatus={value=>{setInvoiceStatus(value);setInvoicePage(1)}} page={invoicePage} onPage={setInvoicePage}/>}
      {providerOpen&&<ProviderDialog provider={providers.find(x=>x.code===providerOpen)!} invoices={invoices} busy={busy} onClose={()=>setProviderOpen(null)} onUpdate={updateProvider} onSave={()=>void save()}/>}
    </>}
  </section>
}

function merchantFieldLabel(code:string){return code==='yookassa'?'Shop ID':code==='robokassa'?'Merchant Login':code==='tbank'?'Terminal Key':'Merchant ID'}
function primarySecretLabel(code:string){return code==='yookassa'?'Secret Key':code==='cloudpayments'?'API Secret':code==='robokassa'?'Пароль №1':code==='tbank'?'Пароль терминала':'Secret Key'}
function secondarySecretLabel(code:string){return code==='robokassa'?'Пароль №2':'Webhook Secret'}
function needsSecondarySecret(code:string){return code==='robokassa'||code==='stripe'}

/** Доступный переключатель вместо платформенно-зависимого checkbox. */
function Toggle({checked,onChange,label,danger=false}:{checked:boolean;onChange:(value:boolean)=>void;label:string;danger?:boolean}){return <button type="button" role="switch" aria-checked={checked} className={`ui-switch ${checked?'on':''} ${danger?'danger':''}`} onClick={()=>onChange(!checked)}><i><span/></i><b>{label}</b></button>}
function StyledSelect({value,onChange,options}:{value:string;onChange:(value:string)=>void;options:[string,string][]}){return <span className="styled-select"><select value={value} onChange={event=>onChange(event.target.value)}>{options.map(([key,label])=><option key={key} value={key}>{label}</option>)}</select><ArrowDownToLine/></span>}
function AdminTabs({value,onChange,items}:{value:string;onChange:(value:string)=>void;items:[string,string][]}){return <nav className="admin-tabs" aria-label="Разделы страницы">{items.map(([key,label])=><button key={key} className={value===key?'active':''} onClick={()=>onChange(key)}>{label}</button>)}</nav>}

function ProviderCards({providers,onOpen}:{providers:AdminPaymentProviderDraft[];onOpen:(code:string)=>void}){return <section className="provider-card-grid">{providers.map(provider=><article className="admin-card provider-card" key={provider.code}><div className="provider-card-icon"><CreditCard/></div><div><strong>{provider.name}</strong><small>{provider.ready?'Реквизиты заполнены':'Требуется настройка'}</small></div><span className={`state-pill ${provider.enabled&&provider.ready?'active':''}`}>{provider.enabled?'Включён':'Выключен'}</span><button onClick={()=>onOpen(provider.code)}><Settings2/>Открыть настройки</button></article>)}</section>}

function ProviderDialog({provider,invoices,busy,onClose,onUpdate,onSave}:{provider:AdminPaymentProviderDraft;invoices:InvoicePage|null;busy:boolean;onClose:()=>void;onUpdate:(code:string,patch:Partial<AdminPaymentProviderDraft>)=>void;onSave:()=>void}){return <div className="source-editor-backdrop" role="presentation" onMouseDown={event=>{if(event.target===event.currentTarget)onClose()}}><section className="source-editor-modal provider-modal" role="dialog" aria-modal="true" aria-label={`Настройки ${provider.name}`}><div className="source-editor-heading"><div><span className="kicker">PAYMENT PROVIDER</span><h2>{provider.name}</h2><p>Реквизиты шлюза изолированы; сохранённые секреты никогда не возвращаются в браузер.</p></div><button className="icon-button" onClick={onClose}><X/></button></div><div className="provider-modal-status"><Toggle checked={provider.enabled} onChange={checked=>onUpdate(provider.code,{enabled:checked})} label="Принимать платежи"/><span className={`state-pill ${provider.ready?'active':''}`}>{provider.ready?'Готов':'Не настроен'}</span></div><div className="provider-fields">{provider.code!=='stripe'&&provider.code!=='cloudpayments'&&<label>{merchantFieldLabel(provider.code)}<input autoComplete="off" maxLength={256} value={provider.merchantId} onChange={event=>onUpdate(provider.code,{merchantId:event.target.value})}/></label>}{provider.code==='cloudpayments'&&<label>Public ID<input autoComplete="off" maxLength={256} value={provider.publicId} onChange={event=>onUpdate(provider.code,{publicId:event.target.value})}/></label>}<label>{primarySecretLabel(provider.code)}<input type="password" autoComplete="new-password" maxLength={4096} placeholder={provider.secretConfigured?'Сохранён · введите для замены':'Введите секрет'} value={provider.secretKey} onChange={event=>onUpdate(provider.code,{secretKey:event.target.value,clearSecretKey:false})}/><small>{provider.secretConfigured?'Секрет настроен и скрыт':'Секрет ещё не задан'}</small></label>{needsSecondarySecret(provider.code)&&<label>{secondarySecretLabel(provider.code)}<input type="password" autoComplete="new-password" maxLength={4096} placeholder={provider.secondarySecretConfigured?'Сохранён · введите для замены':'Введите второй секрет'} value={provider.secondarySecret} onChange={event=>onUpdate(provider.code,{secondarySecret:event.target.value,clearSecondarySecret:false})}/></label>}</div>{provider.code==='robokassa'&&<Toggle checked={provider.testMode} onChange={checked=>onUpdate(provider.code,{testMode:checked})} label="Тестовый режим"/>}<div className="webhook-box"><small>Webhook URL</small><code>{provider.webhookUrl}</code></div><div className="provider-secret-actions">{provider.secretConfigured&&<Toggle danger checked={provider.clearSecretKey} onChange={checked=>onUpdate(provider.code,{clearSecretKey:checked,secretKey:''})} label="Удалить основной секрет"/>}{provider.secondarySecretConfigured&&<Toggle danger checked={provider.clearSecondarySecret} onChange={checked=>onUpdate(provider.code,{clearSecondarySecret:checked,secondarySecret:''})} label="Удалить второй секрет"/>}</div><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={onClose}>Закрыть</button><button className="primary-admin-button" onClick={onSave} disabled={busy}><ShieldCheck/>{busy?'Сохраняем…':'Сохранить'}</button></div><div className="provider-invoices"><h3>Последние счета через {provider.name}</h3><InvoiceTable data={invoices}/></div></section></div>}

function InvoiceRegistry({data,status,onStatus,page,onPage}:{data:InvoicePage|null;status:string;onStatus:(value:string)=>void;page:number;onPage:(value:number)=>void}){const totalPages=Math.max(1,Math.ceil((data?.total??0)/10));return <section className="admin-card invoice-registry"><div className="card-heading"><div><span className="kicker">ЕДИНЫЙ РЕЕСТР</span><h2>Счета</h2></div><span className="section-count">{data?.total??0}</span></div><AdminTabs value={status} onChange={onStatus} items={[["","Все"],["pending","Ожидают"],["paid","Оплачены"],["failed","Ошибки"],["canceled","Отменены"],["refunded","Возвраты"]]}/><InvoiceTable data={data}/>{data&&data.total>10&&<ProxyPagination page={page} pageSize={10} total={data.total} totalPages={totalPages} onPageChange={onPage} onPageSizeChange={()=>{}}/>}</section>}
function InvoiceTable({data}:{data:InvoicePage|null}){return <div className="admin-data-table invoice-table"><div className="admin-data-head"><span>Счёт / клиент</span><span>Провайдер</span><span>Сумма</span><span>Статус</span><span>Создан</span></div>{!data?<div className="empty-state"><RefreshCw className="spin"/>Загружаем счета…</div>:data.items.length===0?<div className="empty-state"><Receipt/>Счетов в этой группе пока нет.</div>:data.items.map(item=><article key={item.id}><span><b>#{item.id.slice(0,8)}</b><small>{item.email||item.userName}</small></span><span>{providerLabel(item.provider)}</span><b>{money(item.amountMinor,item.currency)}</b><em className={`payment-status ${item.status}`}>{paymentStatusLabel(item.status)}</em><time>{new Date(item.createdAt).toLocaleString('ru-RU')}</time></article>)}</div>}

/** Отдельный реестр коммерческого доступа, не смешанный с Identity-ролями. */
function AdminSubscriptionsPage(){
  const [data,setData]=useState<SubscriptionPage|null>(null);const [page,setPage]=useState(1);const [status,setStatus]=useState('');const [editing,setEditing]=useState<AdminSubscription|null>(null);const [draft,setDraft]=useState({plan:'free',status:'active',expiresAt:'',extensionDays:0,reason:''});const [error,setError]=useState('');const [busy,setBusy]=useState(false)
  const load=useCallback(async()=>{const query=new URLSearchParams({page:String(page),pageSize:'10'});if(status)query.set('status',status);const response=await fetch(`${API}/api/v1/admin/subscriptions?${query}`,{credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Подписки недоступны'));return}setData(await response.json() as SubscriptionPage);setError('')},[page,status])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const open=(item:AdminSubscription)=>{setEditing(item);setDraft({plan:item.plan,status:item.status,expiresAt:item.expiresAt?item.expiresAt.slice(0,10):'',extensionDays:0,reason:''})}
  const save=async()=>{if(!editing)return;setBusy(true);const response=await fetch(`${API}/api/v1/admin/subscriptions/${editing.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({...draft,expiresAt:draft.expiresAt?new Date(`${draft.expiresAt}T23:59:59Z`).toISOString():null})});if(!response.ok)setError(await responseMessage(response,'Не удалось изменить подписку'));else{setEditing(null);await load()}setBusy(false)}
  const summary=data?.summary
  return <section className="admin-section"><div className="admin-section-heading"><div><span className="kicker">SUBSCRIPTION CONTROL</span><h1>Подписки</h1><p>Сроки платного доступа, ручное продление и приостановка с журналом действий.</p></div><span className="section-count">{data?.total??0}</span></div>{error&&<div className="admin-notice"><X/>{error}</div>}<div className="admin-summary-grid compact-summary"><article><span className="summary-icon"><Check/></span><div><small>Активные</small><strong>{summary?.active??'—'}</strong></div></article><article><span className="summary-icon"><Clock3/></span><div><small>Истекают за 7 дней</small><strong>{summary?.expiringSoon??'—'}</strong></div></article><article><span className="summary-icon"><CalendarClock/></span><div><small>Пробные</small><strong>{summary?.trialing??'—'}</strong></div></article><article><span className="summary-icon"><Ban/></span><div><small>Приостановлены</small><strong>{summary?.suspended??'—'}</strong></div></article></div><section className="admin-card admin-registry"><AdminTabs value={status} onChange={value=>{setStatus(value);setPage(1)}} items={[["","Все"],["active","Активные"],["trialing","Пробные"],["past_due","Просрочены"],["suspended","Заблокированы"],["expired","Истекли"]]}/><div className="admin-data-table subscriptions-table"><div className="admin-data-head"><span>Пользователь</span><span>Тариф</span><span>Статус</span><span>Действует до</span><span/></div>{data?.items.map(item=><article key={item.id}><span><b>{item.displayName||item.userName}</b><small>{item.email}</small></span><b>{planLabel(item.plan)}</b><em className={`payment-status ${item.status}`}>{subscriptionStatusLabel(item.status)}</em><time>{item.expiresAt?new Date(item.expiresAt).toLocaleDateString('ru-RU'):'Бессрочно'}</time><button className="table-action" onClick={()=>open(item)}><Pencil/>Управлять</button></article>)}</div>{data&&data.total>10&&<ProxyPagination page={page} pageSize={10} total={data.total} totalPages={Math.ceil(data.total/10)} onPageChange={setPage} onPageSizeChange={()=>{}}/>}</section>{editing&&<div className="source-editor-backdrop"><section className="source-editor-modal subscription-modal"><div className="source-editor-heading"><div><span className="kicker">РУЧНОЕ УПРАВЛЕНИЕ</span><h2>{editing.displayName||editing.userName}</h2><p>{editing.email}. Каждое изменение сохраняется в неизменяемом журнале аудита.</p></div><button className="icon-button" onClick={()=>setEditing(null)}><X/></button></div><div className="source-editor-grid"><label>Тариф<StyledSelect value={draft.plan} onChange={plan=>setDraft({...draft,plan})} options={[["free","Free"],["pro","Pro"],["unlimited","Unlimited"]]}/></label><label>Статус<StyledSelect value={draft.status} onChange={statusValue=>setDraft({...draft,status:statusValue})} options={[["active","Активна"],["trialing","Пробная"],["past_due","Просрочена"],["canceled","Отменена"],["expired","Истекла"],["suspended","Приостановлена"]]}/></label><label>Действует до<input type="date" value={draft.expiresAt} onChange={e=>setDraft({...draft,expiresAt:e.target.value,extensionDays:0})}/></label><label>Продлить на дней<input type="number" min="0" max="3660" value={draft.extensionDays} onChange={e=>setDraft({...draft,extensionDays:Number(e.target.value)})}/></label></div><label className="modal-wide-label">Причина изменения<textarea maxLength={500} value={draft.reason} onChange={e=>setDraft({...draft,reason:e.target.value})} placeholder="Например: компенсация по обращению #123"/></label><div className="quick-extend"><span>Быстро продлить:</span>{[7,30,90,365].map(days=><button key={days} onClick={()=>setDraft({...draft,extensionDays:days})}>+{days} дней</button>)}</div><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={()=>setEditing(null)}>Отмена</button><button className="primary-admin-button" disabled={busy} onClick={()=>void save()}><ShieldCheck/>{busy?'Сохраняем…':'Применить'}</button></div></section></div>}</section>
}

/** Наблюдаемость выдачи и блокировки клиентов с самым высоким трафиком. */
function AdminAccessPage(){
  const [data,setData]=useState<AccessPage|null>(null);const [page,setPage]=useState(1);const [modal,setModal]=useState(false);const [draft,setDraft]=useState({kind:'ip',value:'',reason:'',expiresAt:''});const [error,setError]=useState('');const [busy,setBusy]=useState(false)
  const load=useCallback(async()=>{const response=await fetch(`${API}/api/v1/admin/access?page=${page}&pageSize=10`,{credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Статистика доступа недоступна'));return}setData(await response.json() as AccessPage);setError('')},[page]);useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const create=async()=>{setBusy(true);const response=await fetch(`${API}/api/v1/admin/access/rules`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({...draft,expiresAt:draft.expiresAt?new Date(draft.expiresAt).toISOString():null})});if(!response.ok)setError(await responseMessage(response,'Правило не создано'));else{setModal(false);setDraft({kind:'ip',value:'',reason:'',expiresAt:''});await load()}setBusy(false)}
  const toggle=async(rule:AccessRule)=>{await fetch(`${API}/api/v1/admin/access/rules/${rule.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:!rule.enabled,expiresAt:rule.expiresAt??null})});await load()}
  return <section className="admin-section"><div className="admin-section-heading"><div><span className="kicker">TRAFFIC CONTROL</span><h1>Доступ и IP</h1><p>Агрегаты за 30 дней, самые активные клиенты и блокировки IP, подсетей или аккаунтов.</p></div><button className="primary-admin-button" onClick={()=>setModal(true)}><Ban/>Добавить блокировку</button></div>{error&&<div className="admin-notice"><X/>{error}</div>}<div className="admin-summary-grid compact-summary"><article><span className="summary-icon"><Activity/></span><div><small>Запросов</small><strong>{formatNumber(data?.summary.requests)}</strong></div></article><article><span className="summary-icon"><Database/></span><div><small>Выдано адресов</small><strong>{formatNumber(data?.summary.proxyItems)}</strong></div></article><article><span className="summary-icon"><Network/></span><div><small>Уникальных IP</small><strong>{formatNumber(data?.summary.uniqueIps)}</strong></div></article><article><span className="summary-icon"><ShieldOff/></span><div><small>Активных правил</small><strong>{formatNumber(data?.summary.activeRules)}</strong></div></article></div><div className="access-layout"><section className="admin-card admin-registry"><div className="card-heading"><div><span className="kicker">НАГРУЗКА</span><h2>Клиенты выдачи</h2></div></div><div className="admin-data-table access-table"><div className="admin-data-head"><span>IP / аккаунт</span><span>Запросов</span><span>Прокси</span><span>Трафик</span><span>Последний</span></div>{data?.items.map(item=><article key={`${item.ipAddress}:${item.userId}`}><span><b>{item.ipAddress}</b><small>{item.userId?`Аккаунт ${item.userId.slice(0,8)}`:'Анонимный доступ'}</small></span><b>{formatNumber(item.requests)}</b><span>{formatNumber(item.proxyItems)}</span><span>{formatBytes(item.bytesSent)}</span><time>{timeAgo(item.lastSeenAt)}</time></article>)}</div>{data&&data.total>10&&<ProxyPagination page={page} pageSize={10} total={data.total} totalPages={Math.ceil(data.total/10)} onPageChange={setPage} onPageSizeChange={()=>{}}/>}</section><section className="admin-card rules-card"><div className="card-heading"><div><span className="kicker">ПРАВИЛА</span><h2>Блокировки</h2></div></div><div className="rule-list">{data?.rules.length===0&&<div className="empty-state"><ShieldCheck/>Блокировок нет.</div>}{data?.rules.map(rule=><article key={rule.id}><div><b>{rule.kind.toUpperCase()} · {rule.value}</b><small>{rule.reason}{rule.expiresAt?` · до ${new Date(rule.expiresAt).toLocaleString('ru-RU')}`:' · бессрочно'}</small></div><Toggle checked={rule.enabled} onChange={()=>void toggle(rule)} label={rule.enabled?'Активно':'Отключено'}/></article>)}</div></section></div>{modal&&<div className="source-editor-backdrop"><section className="source-editor-modal"><div className="source-editor-heading"><div><span className="kicker">ACCESS RULE</span><h2>Новая блокировка</h2><p>Правило начнёт применяться к каталогу и экспорту сразу после сохранения.</p></div><button className="icon-button" onClick={()=>setModal(false)}><X/></button></div><div className="source-editor-grid"><label>Тип<StyledSelect value={draft.kind} onChange={kind=>setDraft({...draft,kind})} options={[["ip","IP-адрес"],["cidr","Подсеть CIDR"],["user","Пользователь UUID"]]}/></label><label>Значение<input value={draft.value} onChange={e=>setDraft({...draft,value:e.target.value})} placeholder={draft.kind==='cidr'?'203.0.113.0/24':draft.kind==='user'?'UUID пользователя':'203.0.113.10'}/></label><label>Действует до<input type="datetime-local" value={draft.expiresAt} onChange={e=>setDraft({...draft,expiresAt:e.target.value})}/></label></div><label className="modal-wide-label">Причина<textarea required minLength={3} maxLength={500} value={draft.reason} onChange={e=>setDraft({...draft,reason:e.target.value})}/></label><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={()=>setModal(false)}>Отмена</button><button className="primary-admin-button" disabled={busy||draft.reason.trim().length<3||!draft.value.trim()} onClick={()=>void create()}><Ban/>{busy?'Создаём…':'Заблокировать'}</button></div></section></div>}</section>
}

/** Пагинация повторяет серверный UX RMS: размер, страницы, быстрый переход и итог. */
function ProxyPagination({page, pageSize, total, totalPages, onPageChange, onPageSizeChange}: {page: number; pageSize: number; total: number; totalPages: number; onPageChange: (page: number) => void; onPageSizeChange: (size: number) => void}) {
  const [jump, setJump] = useState('')
  const pages = paginationWindow(page, totalPages)
  const go = (next: number) => onPageChange(Math.min(totalPages, Math.max(1, next)))
  const showQuickJump = totalPages > 7
  return <nav className={`pagination${showQuickJump ? '' : ' pagination-compact'}`} aria-label="Пагинация каталога">
    <div className="page-sizes"><span>Показывать:</span>{[10, 25, 50, 100].map(size => <button key={size} className={pageSize === size ? 'active' : ''} aria-pressed={pageSize === size} onClick={() => onPageSizeChange(size)}>{size}</button>)}</div>
    <div className="page-controls"><button aria-label="Предыдущая страница" disabled={page === 1} onClick={() => go(page - 1)}>←</button>{pages.map((item, index) => item === '…' ? <span key={`ellipsis-${index}`}>…</span> : <button key={item} className={item === page ? 'active' : ''} aria-current={item === page ? 'page' : undefined} onClick={() => go(item)}>{item}</button>)}<button aria-label="Следующая страница" disabled={page === totalPages} onClick={() => go(page + 1)}>→</button></div>
    {showQuickJump && <form className="page-jump" aria-label="Быстрый переход по страницам" onSubmit={event => { event.preventDefault(); const value = Number(jump); if (Number.isInteger(value) && value > 0) { go(value); setJump('') } }}><input aria-label="Номер страницы" inputMode="numeric" min={1} max={totalPages} type="number" placeholder="Стр." value={jump} onChange={event => setJump(event.target.value)}/><span aria-hidden="true">/ {totalPages}</span><button type="submit" aria-label="Перейти на страницу"><ArrowRight size={14}/></button></form>}
    <p>Страница {page} из {totalPages} · Найдено: {formatNumber(total)}</p>
  </nav>
}

function paginationWindow(page: number, totalPages: number): (number | '…')[] {
  if (totalPages <= 7) return Array.from({ length: totalPages }, (_, index) => index + 1)
  if (page <= 4) return [1, 2, 3, 4, 5, '…', totalPages]
  if (page >= totalPages - 3) return [1, '…', totalPages - 4, totalPages - 3, totalPages - 2, totalPages - 1, totalPages]
  return [1, '…', page - 1, page, page + 1, '…', totalPages]
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
function formatActiveDuration(value?: number) {
  if (value === undefined) return '—'
  if (value < 60) return '< 1 мин'
  const minutes = Math.floor(value / 60)
  if (minutes < 60) return `${minutes} мин`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} ч ${minutes % 60} мин`
  return `${Math.floor(hours / 24)} д ${hours % 24} ч`
}
function statusClass(status?: string) { return status === 'completed' ? 'status-ok' : status === 'failed' ? 'status-failed' : 'status-running' }
function catalogStatusClass(catalog?: SourceCatalogSnapshot) { return !catalog ? '' : catalog.isHealthy ? 'status-ok' : catalog.isComplete ? 'status-running' : 'status-failed' }
function statusLabel(status?: string) { return status === 'completed' ? 'успешно' : status === 'failed' ? 'ошибка' : status === 'running' ? 'выполняется' : 'нет данных' }
function backupDelivery(run: BackupRun) { return run.sentToTelegram ? 'доставлен в Telegram' : run.telegramConfigured ? 'Telegram не доставлен' : 'только локально' }
function planLabel(plan?: string) { return plan === 'unlimited' ? 'Unlimited' : plan === 'pro' ? 'Pro' : 'Free' }
function money(minor:number,currency:string){return new Intl.NumberFormat('ru-RU',{style:'currency',currency}).format(minor/100)}
function providerLabel(provider:string){return ({yookassa:'ЮKassa',cloudpayments:'CloudPayments',robokassa:'Robokassa',tbank:'Т-Банк',stripe:'Stripe'} as Record<string,string>)[provider]??provider}
function paymentStatusLabel(status:string){return ({pending:'Ожидает оплаты',paid:'Оплачен',failed:'Ошибка',canceled:'Отменён',refunded:'Возвращён'} as Record<string,string>)[status]??status}
function subscriptionStatusLabel(status:string){return ({active:'Активна',trialing:'Пробная',past_due:'Просрочена',canceled:'Отменена',expired:'Истекла',suspended:'Приостановлена'} as Record<string,string>)[status]??status}
function label(protocol: Protocol) { return ({Http: 'HTTP', Https: 'HTTPS', Socks4: 'SOCKS4', Socks5: 'SOCKS5'})[protocol] }
function timeAgo(value: string) { const sec = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000)); if (sec < 10) return 'только что'; if (sec < 60) return `${sec} сек назад`; if (sec < 3600) return `${Math.floor(sec / 60)} мин назад`; return `${Math.floor(sec / 3600)} ч назад` }
function timeUntil(value: string) { const sec = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000)); if (sec < 60) return `через ${sec} сек`; if (sec < 3600) return `через ${Math.ceil(sec / 60)} мин`; return `через ${Math.ceil(sec / 3600)} ч` }

async function responseMessage(response: Response, fallback: string) {
  try {
    const problem = await response.json() as { title?: string; detail?: string }
    return problem.detail || problem.title || fallback
  } catch { return fallback }
}
