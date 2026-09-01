import { lazy, Suspense, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Activity, ArrowDownToLine, ArrowRight, Ban, Bell, Bot, CalendarClock, Check, ChevronDown, Clock3, Copy, CreditCard, Database, Gauge, Globe2, HardDriveDownload, HelpCircle, LayoutDashboard, LockKeyhole, LogOut, MessageCircle, MousePointerClick, Network, Pencil, Play, Plus, Radio, Receipt, RefreshCw, Search, Send, Server, Settings2, ShieldCheck, ShieldOff, Star, Trash2, User, Users, Wifi, Workflow, X } from 'lucide-react'
import { currentLocale, LanguageSwitcher, type Language, useI18n } from './i18n'
import { StyledSelect } from './components/StyledSelect'
import { Toggle } from './components/Toggle'
import { ToastSignal } from './components/Toasts'
import { PublicPricingSection } from './PublicPricingSection'
import { publicInfoPaths } from './publicInfoRoutes'
import { analyticsAllowed, privacyPreferenceChanged } from './privacyPreferences'
import { useSiteSettings } from './siteSettingsContext'
import { sectionHref, siteSectionCodes, siteSectionLabels, type SiteSettings } from './siteSettingsModel'

type Protocol = 'Http' | 'Https' | 'Socks4' | 'Socks5'
type Proxy = { host: string; port: number; protocol: Protocol; url: string; latencyMs: number; successRate: number; exitIp?: string; countryCode?: string; lastCheckedAt: string; firstAliveAt?: string; lastAliveAt?: string; activeSince?: string; activeForSeconds?: number }
type ProxyCountry = { code: string; count: number }
type PagedResult<T> = { items: T[]; page: number; pageSize: number; total: number; fullAccess?:boolean; accessible?:number; limited?:boolean; message?:string; upgradeUrl?:string }
type CommerceAvailability = { available:boolean;showOffer:boolean;fullAccess:boolean;paymentProviders:number;telegram:boolean;accountUrl:string }
type Stats = { alive: number; staleAlive: number; pending: number; dead: number; dueForCheck: number; checksInProgress: number; scheduledChecks: number; averageLatencyMs: number | null; sources: number; failingSources: number; repeatedlyFailingSources: number; truncatedSources: number; byProtocol: { protocol: Protocol; count: number }[]; lastRun?: { startedAt: string; candidatesFound: number; newProxies: number; sourcesTruncated: number; candidateLimitReached: boolean; status: string } }
type Source = { id: string; name: string; url: string; defaultProtocol: Protocol; enabled: boolean; priority: number; lastItemCount: number; lastResultTruncated: boolean; lastFetchedAt?: string; lastSucceededAt?: string; lastContentFetchedAt?: string; nextFetchAt?: string; consecutiveFailures: number; lastError?: string; isBuiltIn: boolean; provider?: string; providerIdentity?: string; catalogRank?: number }
type CollectionRun = { id: string; startedAt: string; finishedAt?: string; sourcesProcessed: number; sourcesSucceeded: number; sourcesFailed: number; sourcesSkipped: number; sourcesTruncated: number; candidatesFound: number; candidateLimitReached: boolean; newProxies: number; status: string; error?: string }
type ValidationRun = { id: string; startedAt: string; finishedAt?: string; claimed: number; checked: number; alive: number; deferred: number; status: string; error?: string }
type BackupRun = { id: string; startedAt: string; finishedAt?: string; status: string; fileName?: string; sizeBytes: number; telegramConfigured: boolean; sentToTelegram: boolean; objectStorageConfigured?:boolean; sentToObjectStorage?:boolean; objectStorageKey?:string; error?: string }
type BackupFile = BackupRun & { available: boolean }
type BackupSettings = { enabled:boolean;intervalHours:number;retentionDays:number;historyRetentionDays:number;maxTelegramFileSizeMb:number;sendToTelegram:boolean;telegramBotConfigured:boolean;telegramRecipientId?:string;telegramRecipientDisplayName?:string;telegramRecipientUsername?:string;sendToObjectStorage:boolean;objectStorageEndpoint:string;objectStorageRegion:string;objectStorageBucket:string;objectStoragePrefix:string;objectStorageUsePathStyle:boolean;objectStorageCredentialsConfigured:boolean;objectStorageAccessKey:string;objectStorageSecretKey:string;clearObjectStorageCredentials:boolean;encryptionConfigured:boolean;format:string }
type TelegramBackupRecipient = { id:string;displayName:string;username?:string;lastInteractionAt:string;isDefault:boolean }
type SourceCatalogSnapshot = { lastAuditedOn: string; expectedSources: number; presentSources: number; enabledSources: number; healthySources: number; failingSources: number; neverAuditedSources: number; staleSources: number; truncatedSources: number; expectedProviders: number; presentProviders: number; enabledProviders: number; isComplete: boolean; isHealthy: boolean }
type Diagnostics = {
  serverTime: string
  databaseBytes: number
  vpnEndpoints: number
  validationQueue?: { total: number; everAlive: number; historicalDead: number; leased: number; neverChecked: number; neverAttempted: number; due: number; scheduled: number; repeatedlyFailing: number; staleUnseen: number; attemptsLastFiveMinutes: number; checkedLastFiveMinutes: number; aliveLastFiveMinutes: number; deferredLastFiveMinutes: number; failedRunsLastFiveMinutes: number; activeRuns: number; concurrencyLimit: number; batchSize: number; checksPerSecond: number; estimatedDrainSeconds?: number; lastAttemptAt?: string }
  sourceCatalog?: SourceCatalogSnapshot
  recentRuns: CollectionRun[]
  recentValidationRuns?: ValidationRun[]
  recentBackups: BackupRun[]
}

const API = import.meta.env.VITE_API_URL ?? ''
const APP_VERSION = import.meta.env.VITE_APP_VERSION ?? '0.0.0-local'
const AuthRoutePage = lazy(() => import('./AuthPages'))
const PublicInfoPage = lazy(() => import('./PublicInfo').then(module => ({default: module.PublicInfoPage})))
const AdminSiteSettingsPage = lazy(() => import('./AdminSiteSettingsPage'))
const protocols: Protocol[] = ['Http', 'Https', 'Socks4', 'Socks5']
type AdminSection = 'overview' | 'operations' | 'checkers' | 'proxies' | 'vpn' | 'sources' | 'backups' | 'users' | 'payments' | 'telegram' | 'subscriptions' | 'access' | 'site'
type CheckerNode = {id:string;name:string;host:string;sshPort:number;sshUsername:string;enabled:boolean;concurrency:number;batchSize:number;createdAt:string;updatedAt:string;lastHeartbeatAt?:string;lastLeaseAt?:string;lastCompletedAt?:string;currentLeaseId?:string;currentLeaseUntil?:string;agentVersion?:string;remoteAddress?:string;deploymentStatus:string;lastError?:string;completedChecks:number;aliveChecks:number;hostKeyFingerprint?:string;online:boolean;busy:boolean}
type CheckerNodeList = {image:string;nativeAssetBaseUrl:string;items:CheckerNode[]}
type AdminProxyStatus = 'Pending' | 'Alive' | 'Dead'
type AdminProxy = { id:string;host:string;port:number;protocol:Protocol;status:AdminProxyStatus;latencyMs?:number;exitIp?:string;countryCode?:string;isAnonymous:boolean;firstSeenAt:string;lastSeenAt:string;lastCheckedAt?:string;firstAliveAt?:string;lastAliveAt?:string;currentAliveSince?:string;activeForSeconds?:number;lastValidationAttemptAt?:string;lastValidationDeferred:boolean;nextCheckAt?:string;successfulChecks:number;failedChecks:number;consecutiveFailedChecks:number;successRate:number;lastError?:string }
type AdminProxyPage = PagedResult<AdminProxy> & { summary:{total:number;alive:number;freshAlive:number;staleAlive:number;pending:number;dead:number;everAlive:number;averageAliveLatencyMs?:number;countries:number;longestActiveSeconds?:number};countries:ProxyCountry[] }
type AccountApiToken = { id:string;name:string;displaySuffix:string;scopes:string[];createdAt:string;lastUsedAt?:string;revokedAt?:string;active:boolean }
type ApiTokenRequest = {id:number;token:{userApiTokenId:string;name:string;displaySuffix:string;revokedAt?:string};ipAddress:string;method:string;path:string;query?:string;statusCode:number;itemCount?:number;durationMs:number;requestedAt:string}
type ApiTokenHistoryPage = PagedResult<ApiTokenRequest>
type ReferralReward = { id:string;kind:'signup'|'purchase';daysGranted:number;createdAt:string;productCode?:string;durationDays?:number }
type ReferralItem = { id:string;createdAt:string;user:{userName:string;email:string;displayName?:string};rewards:ReferralReward[] }
type ReferralPage = PagedResult<ReferralItem>
type ReferralSummary = { code:string;link:string;telegramLink?:string;invited:number;remaining:number;maximum:number;rewardDays:number }
type AdminReferralItem = { id:string;createdAt:string;referrer:{referrerUserId:string;userName:string;email:string;displayName?:string};referred:{referredUserId:string;userName:string;email:string;displayName?:string};rewardDays:number;rewards:ReferralReward[] }
type AdminReferralPage = PagedResult<AdminReferralItem> & {summary:{referrals:number;rewardDays:number;purchaseRewards:number}}
type AccountProfile = { id: string; userName: string; email: string; displayName?: string; preferredLanguage: Language; createdAt: string; lastLoginAt?: string; referralCode:string; referral:ReferralSummary; roles: string[]; subscription?: { plan: string; status: string; startedAt: string; expiresAt?: string }; entitlements:{unlimitedProxyAccess:boolean;apiTokens:boolean};apiTokens:AccountApiToken[] }
type PaymentCatalog = { enabled: boolean; products: PaymentProduct[]; providers: PaymentProvider[] }
type PaymentProduct = { code: string; name: string; plan: string; durationDays: number; amountMinor: number; discountPercent:number; fullDailyPriceMinor:number; savingsMinor:number; currency: string; description: string }
type PaymentProvider = { code: string; name: string; available: boolean }
type PaymentOrder = { id: string; productCode: string; plan: string; provider: string; paymentMethod:string; paymentInstrument?:string; amountMinor: number; currency: string; status: string; createdAt: string; paidAt?: string }
type AdminPaymentProduct = PaymentProduct & { enabled: boolean }
type AdminPaymentProviderOperational = { state:'disabled'|'configuration_required'|'healthy'|'pending'|'awaiting_first_payment'|'retest_required'|'webhook_attention'|'no_successful_payments'; attention?:string; totalOrders:number; pendingOrders:number; paidOrders:number; paidAfterConfigurationUpdate:number; failedOrders:number; canceledOrders:number; refundedOrders:number; lastOrderAt?:string; lastPaidAt?:string; webhookRequired:boolean; directReconciliationSupported:boolean }
type AdminPaymentProvider = { code: string; name: string; enabled: boolean; merchantId: string; publicId: string; testMode: boolean; secretConfigured: boolean; secondarySecretConfigured: boolean; ready: boolean; webhookUrl: string; operational?:AdminPaymentProviderOperational }
type AdminPaymentSettings = { enabled: boolean; configurationUpdatedAt?:string; products: AdminPaymentProduct[]; providers: AdminPaymentProvider[] }
type AdminPaymentProviderDraft = AdminPaymentProvider & { secretKey: string; secondarySecret: string; clearSecretKey: boolean; clearSecondarySecret: boolean }
type AdminInvoice = PaymentOrder & { userId:string; userName:string; email:string; providerPaymentId?:string; updatedAt:string }
type InvoicePage = PagedResult<AdminInvoice> & { summary:{status:string;count:number;amountMinor:number}[] }
type AdminSubscription = { id:string;userId:string;userName:string;email:string;displayName?:string;plan:string;status:string;startedAt:string;expiresAt?:string;updatedAt:string }
type SubscriptionPage = PagedResult<AdminSubscription> & { summary:{active:number;trialing:number;suspended:number;expiringSoon:number} }
type AccessClient = {ipAddress:string;userId?:string;userName?:string;email?:string;displayName?:string;requests:number;blockedRequests:number;proxyItems:number;bytesSent:number;lastSeenAt:string;isBlocked:boolean}
type AccessRule = {id:string;kind:string;value:string;userId?:string;reason:string;enabled:boolean;expiresAt?:string;createdAt:string;updatedAt:string}
type AccessPage = PagedResult<AccessClient> & {summary:{requests:number;proxyItems:number;uniqueIps:number;activeRules:number}}
type AccessRulePage = PagedResult<AccessRule>
type SiteVisitor = {ipAddress:string;userId?:string;userName?:string;email?:string;displayName?:string;pageViews:number;pages:number;firstSeenAt:string;lastSeenAt:string;isBlocked:boolean}
type SiteVisitorPage = PagedResult<SiteVisitor> & {summary:{pageViews:number;uniqueVisitors:number;authenticatedVisitors:number;active24Hours:number};retentionDays:number}
type SiteVisit = {id:number;ipAddress:string;userId?:string;userName?:string;email?:string;displayName?:string;page:string;visitedAt:string}
type SiteVisitPage = PagedResult<SiteVisit> & {retentionDays:number}
type AdminUser = AccountProfile & { isActive: boolean }
type UserAccessDraft = { isActive: boolean; administrator: boolean; subscriber: boolean; plan: string; status: string; expiresAt: string }
type SourceDraft = { name: string; url: string; protocol: Protocol; priority: number; enabled: boolean }
type VpnProtocol = 'OpenVpn'|'WireGuard'|'Vless'|'Vmess'|'Trojan'|'Shadowsocks'|'Hysteria2'|'Tuic'
type VpnStatus = 'Pending'|'Reachable'|'Unreachable'|'UnsupportedTransport'
type VpnEndpoint = {id:string;host:string;port:number;countryCode?:string;protocol:VpnProtocol;transport:'tcp'|'udp';status:VpnStatus;latencyMs?:number;firstSeenAt:string;lastSeenAt:string;lastCheckedAt?:string;nextCheckAt?:string;successfulChecks:number;failedChecks:number;successRate:number;knownForSeconds:number;lastError?:string;connectionUri?:string}
type AdminVpnPage = PagedResult<VpnEndpoint> & {summary:{total:number;reachable:number;pending:number;unreachable:number;unsupportedTransport:number;everReachable:number;averageReachableLatencyMs?:number;countries:number;longestKnownSeconds?:number};countries:ProxyCountry[]}
type VpnSource = {id:string;name:string;provider:string;url:string;defaultProtocol:VpnProtocol;enabled:boolean;priority:number;license:string;lastFetchedAt?:string;lastSucceededAt?:string;lastItemCount:number;consecutiveFailures:number;lastError?:string;isBuiltIn:boolean}
type TelegramStats = { users:number;activeUsers30d:number;notificationsEnabled:number;marketingConsents:number;blocked:number;paidOrders:number;starsRevenue:number;queued:number;failed:number }
type TelegramProxy = {id:string;host:string;port:number;username:string;password?:string;passwordConfigured:boolean}
type TelegramSettings = { enabled:boolean;marketingBroadcastsEnabled:boolean;updateMode:'webhook'|'polling';transportMode:'auto'|'proxy'|'direct';proxies:TelegramProxy[];name:string;description:string;shortDescription:string;supportText:string;proxyFileMaxItems:number;webhookMaxConnections:number;productStars:Record<string,number>;automaticProductCodes:string[];rublesPerStar:number;starsRoundingStep:number;effectiveProductStars:Record<string,number>;tokenConfigured:boolean;botId?:number;botUsername?:string;provisionedAt?:string;updatedAt?:string;webhookUrl:string;avatarUrl:string;stats:TelegramStats }
type TelegramChat = { id:string;chatId:number;telegramUserId:number;username?:string;displayName:string;languageCode?:string;notificationsEnabled:boolean;marketingNotificationsEnabled:boolean;marketingConsentGrantedAt?:string;marketingConsentVersion?:string;marketingConsentWithdrawnAt?:string;isBlocked:boolean;createdAt:string;lastInteractionAt:string;subscription:{plan:string;status:string;expiresAt?:string};messages:number }
type TelegramMessage = { id:string;direction:'inbound'|'bot'|'admin';text:string;createdAt:string }

const emptySourceDraft: SourceDraft = { name: '', url: '', protocol: 'Http', priority: 100, enabled: true }

function isAbortError(reason: unknown) {
  return reason instanceof Error && reason.name === 'AbortError'
}

/** Копирует готовую конфигурацию и сохраняет работу на браузерах без Clipboard API. */
async function copyText(value: string) {
  // Chromium и мобильные браузеры иногда предоставляют Clipboard API, но
  // отклоняют запись из-за политики разрешений. В таком случае бесшовно
  // используем совместимый запасной способ вместо неработающей кнопки.
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(value)
      return
    } catch {
      // Продолжаем через скрытое поле ниже.
    }
  }
  const input = document.createElement('textarea')
  input.value = value
  input.style.position = 'fixed'
  input.style.opacity = '0'
  document.body.append(input)
  input.select()
  const copied = document.execCommand('copy')
  input.remove()
  if (!copied) throw new Error('Browser rejected clipboard access')
}

/** Основная панель: публичный каталог и компактное администрирование в одном интерфейсе. */
export default function App() {
  const { t } = useI18n()
  const { settings: siteSettings, loading: siteSettingsLoading } = useSiteSettings()
  const [stats, setStats] = useState<Stats | null>(null)
  const [proxies, setProxies] = useState<Proxy[]>([])
  const [protocol, setProtocol] = useState<Protocol | 'All'>('All')
  const [maxLatency, setMaxLatency] = useState(2000)
  const [countries, setCountries] = useState<ProxyCountry[]>([])
  const [selectedCountries, setSelectedCountries] = useState<string[]>([])
  const [loading, setLoading] = useState(true)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [total, setTotal] = useState(0)
  const [catalogAccess, setCatalogAccess] = useState<{fullAccess:boolean;limited:boolean;message?:string;accessible?:number;upgradeUrl?:string}>({fullAccess:false,limited:false})
  const [commerce, setCommerce] = useState<CommerceAvailability | null>(null)
  const [apiError, setApiError] = useState('')
  const currentPath = window.location.pathname.replace(/\/+$/, '') || '/'
  const loginPage = currentPath === '/admin/login' || currentPath === '/login'
  const registerPage = currentPath === '/register'
  const forgotPasswordPage = currentPath === '/forgot-password'
  const resetPasswordPage = currentPath === '/reset-password'
  const accountOpen = currentPath === '/account' || currentPath === '/account/profile'
  const publicInfoPage = publicInfoPaths[currentPath]
  const adminOpen = currentPath === '/admin' || currentPath.startsWith('/admin/') && !loginPage
  const adminSection: AdminSection = currentPath === '/admin/proxies' ? 'proxies'
    : currentPath === '/admin/vpn' ? 'vpn'
    : currentPath === '/admin/checkers' ? 'checkers'
    : currentPath === '/admin/sources' ? 'sources'
    : currentPath === '/admin/operations' ? 'operations'
      : currentPath === '/admin/backups' ? 'backups'
        : currentPath === '/admin/users' ? 'users'
          : currentPath === '/admin/payments' ? 'payments'
            : currentPath === '/admin/telegram' ? 'telegram'
              : currentPath === '/admin/subscriptions' ? 'subscriptions'
              : currentPath === '/admin/access' ? 'access'
                : currentPath === '/admin/site' ? 'site' : 'overview'
  const [adminAuthenticated, setAdminAuthenticated] = useState(false)
  const [adminError, setAdminError] = useState('')
  const [sources, setSources] = useState<Source[]>([])
  const [sourcePage, setSourcePage] = useState(1)
  const [sourcePageSize, setSourcePageSize] = useState(10)
  const [sourceTotal, setSourceTotal] = useState(0)
  const [sourceSearchDraft, setSourceSearchDraft] = useState('')
  const [sourceSearch, setSourceSearch] = useState('')
  const [diagnostics, setDiagnostics] = useState<Diagnostics | null>(null)
  const [backups, setBackups] = useState<BackupFile[]>([])
  const [backupPage, setBackupPage] = useState(1)
  const [backupPageSize, setBackupPageSize] = useState(10)
  const [backupTotal, setBackupTotal] = useState(0)
  const [backupBusy, setBackupBusy] = useState('')
  const [backupDeleteTarget, setBackupDeleteTarget] = useState<BackupFile | null>(null)
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
  const backupRequestIdRef = useRef(0)
  const backupAbortRef = useRef<AbortController | null>(null)
  const adminSessionIdRef = useRef(0)
  const adminMutationAbortRefs = useRef(new Set<AbortController>())
  const recordedVisitRef = useRef('')
  const [analyticsConsentRevision,setAnalyticsConsentRevision] = useState(0)

  useEffect(() => {
    const refresh = () => setAnalyticsConsentRevision(value => value + 1)
    window.addEventListener(privacyPreferenceChanged, refresh)
    return () => window.removeEventListener(privacyPreferenceChanged, refresh)
  }, [])

  // Beacon не задерживает переход со страницы. Сервер принимает только pathname,
  // сводит его к фиксированному коду и не сохраняет query/fragment или tracking cookies.
  useEffect(() => {
    if (!siteSettings.analytics.firstPartyEnabled ||
      !analyticsAllowed(siteSettings.cookieConsentRevision) ||
      recordedVisitRef.current === currentPath ||
      typeof navigator.sendBeacon !== 'function') return
    recordedVisitRef.current = currentPath
    const payload = new Blob([JSON.stringify({ path: currentPath })], { type: 'application/json' })
    navigator.sendBeacon(`${API}/api/v1/telemetry/visit`, payload)
  }, [currentPath, analyticsConsentRevision, siteSettings.analytics.firstPartyEnabled, siteSettings.cookieConsentRevision])

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
      selectedCountries.forEach(country => query.append('country', country))
      const [statsResponse, proxyResponse, countriesResponse] = await Promise.all([
        fetch(`${API}/api/v1/stats`, { signal: controller.signal }),
        includeCatalog ? fetch(`${API}/api/v1/proxies?${query}`, { signal: controller.signal, credentials:'include', headers:{'Accept-Language':currentLocale()} }) : Promise.resolve(null),
        includeCatalog ? fetch(`${API}/api/v1/proxies/countries`, { signal: controller.signal }) : Promise.resolve(null),
      ])
      if (!statsResponse.ok || (proxyResponse && !proxyResponse.ok) || (countriesResponse && !countriesResponse.ok)) throw new Error('API пока недоступен')
      const statsSnapshot = await statsResponse.json()
      if (requestId !== publicRequestIdRef.current) return
      setStats(statsSnapshot)
      if (proxyResponse && catalogRequestId === catalogRequestIdRef.current) {
        const snapshot = await proxyResponse.json() as PagedResult<Proxy>
        setProxies(snapshot.items)
        setTotal(snapshot.total)
        setCatalogAccess({fullAccess:snapshot.fullAccess??!snapshot.limited,limited:Boolean(snapshot.limited),message:snapshot.message,accessible:snapshot.accessible,upgradeUrl:snapshot.upgradeUrl})
        if (snapshot.limited) {
          if (page !== 1) setPage(1)
        } else {
          const availablePages = Math.max(1, Math.ceil(snapshot.total / pageSize))
          if (page > availablePages) setPage(availablePages)
        }
      }
      if (countriesResponse && catalogRequestId === catalogRequestIdRef.current) {
        const countrySnapshot = await countriesResponse.json() as unknown
        setCountries(Array.isArray(countrySnapshot) ? countrySnapshot as ProxyCountry[] : [])
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
  }, [protocol, maxLatency, page, pageSize, selectedCountries])

  useEffect(() => {
    if (currentPath !== '/') return
    const controller = new AbortController()
    void fetch(`${API}/api/v1/commerce/availability`, {credentials:'include',signal:controller.signal})
      .then(response => response.ok ? response.json() as Promise<CommerceAvailability> : null)
      .then(snapshot => { if (snapshot && !controller.signal.aborted) setCommerce(snapshot) })
      .catch(reason => { if (!isAbortError(reason)) setCommerce(null) })
    return () => controller.abort()
  }, [currentPath])

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

  const changeCountries = (values: string[]) => {
    catalogRequestIdRef.current++
    setLoading(true)
    setPage(1)
    setSelectedCountries(values)
  }

  const loadAdminData = useCallback(async (
    focusFirstAction = false,
    requestedSourcePage = sourcePage,
    requestedSourcePageSize = sourcePageSize,
    requestedSourceSearch = sourceSearch,
  ) => {
    const requestId = ++adminRequestIdRef.current
    adminAbortRef.current?.abort()
    const controller = new AbortController()
    adminAbortRef.current = controller
    setAdminLoading(true)
    try {
      const requestOptions = { credentials: 'include' as const, signal: controller.signal }
      const sourceQuery = new URLSearchParams({ page: String(requestedSourcePage), pageSize: String(requestedSourcePageSize) })
      if (requestedSourceSearch) sourceQuery.set('search', requestedSourceSearch)
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
  }, [sourcePage, sourcePageSize, sourceSearch])

  /** Загружает отдельный реестр backup: audit-история может быть длиннее diagnostics-снимка. */
  const loadBackups = useCallback(async (
    requestedPage = backupPage,
    requestedPageSize = backupPageSize,
  ) => {
    const requestId = ++backupRequestIdRef.current
    backupAbortRef.current?.abort()
    const controller = new AbortController()
    backupAbortRef.current = controller
    try {
      const query = new URLSearchParams({ page: String(requestedPage), pageSize: String(requestedPageSize) })
      const response = await fetch(`${API}/api/v1/admin/backups?${query}`, { credentials: 'include', signal: controller.signal })
      if (requestId !== backupRequestIdRef.current) return
      if (response.status === 401) { setAdminAuthenticated(false); window.location.replace('/login'); return }
      if (response.status === 403) { window.location.replace('/account'); return }
      if (!response.ok) throw new Error(await responseMessage(response, 'История резервных копий недоступна'))
      const snapshot = await response.json() as PagedResult<BackupFile>
      if (requestId !== backupRequestIdRef.current) return
      setBackups(snapshot.items)
      setBackupTotal(snapshot.total)
      const availablePages = Math.max(1, Math.ceil(snapshot.total / requestedPageSize))
      if (requestedPage > availablePages) setBackupPage(availablePages)
    } catch (reason) {
      if (!isAbortError(reason) && requestId === backupRequestIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Не удалось загрузить резервные копии')
    } finally {
      if (requestId === backupRequestIdRef.current) backupAbortRef.current = null
    }
  }, [backupPage, backupPageSize])

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

  useEffect(() => {
    if (adminSection === 'backups' && adminAuthenticated) void loadBackups()
  }, [adminSection, adminAuthenticated, loadBackups])

  /** Отменяет все чтения и мутации, принадлежавшие предыдущей admin-сессии. */
  const invalidateAdminSession = useCallback(() => {
    adminSessionIdRef.current++
    adminRequestIdRef.current++
    adminAbortRef.current?.abort()
    backupRequestIdRef.current++
    backupAbortRef.current?.abort()
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
    setBackups([])
    setBackupTotal(0)
    setBackupBusy('')
    setBackupDeleteTarget(null)
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
      await Promise.all([load(), loadAdminData(), name === 'backup' ? loadBackups(1, backupPageSize) : Promise.resolve()])
      if (name === 'backup') setBackupPage(1)
    } catch (reason) {
      if (!isAbortError(reason) && sessionId === adminSessionIdRef.current)
        setAdminError(reason instanceof Error ? reason.message : 'Административная операция не выполнена')
    } finally {
      adminMutationAbortRefs.current.delete(controller)
      if (sessionId === adminSessionIdRef.current) setAction('')
    }
  }

  /** Удаляет файл и audit-запись только после подтверждения в фирменном диалоге. */
  const deleteBackup = async () => {
    if (!backupDeleteTarget || backupBusy) return
    const target = backupDeleteTarget
    setBackupBusy(target.id)
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/backups/${target.id}`, {
        method: 'DELETE', credentials: 'include',
      })
      if (response.status === 401) { setAdminAuthenticated(false); window.location.replace('/login'); return }
      if (!response.ok) throw new Error(await responseMessage(response, 'Не удалось удалить резервную копию'))
      setBackupDeleteTarget(null)
      const remainingTotal = Math.max(0, backupTotal - 1)
      const nextPage = Math.min(backupPage, Math.max(1, Math.ceil(remainingTotal / backupPageSize)))
      setBackupPage(nextPage)
      await Promise.all([loadBackups(nextPage, backupPageSize), loadAdminData()])
    } catch (reason) {
      setAdminError(reason instanceof Error ? reason.message : 'Не удалось удалить резервную копию')
    } finally {
      setBackupBusy('')
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
    selectedCountries.forEach(country => query.append('country', country))
    return query.toString()
  }, [protocol, maxLatency, selectedCountries])
  const freshness = stats?.lastRun?.startedAt ? timeAgo(stats.lastRun.startedAt) : '—'
  const latestCollection = diagnostics?.recentRuns[0]
  const latestValidation = diagnostics?.recentValidationRuns?.[0]
  const latestBackup = diagnostics?.recentBackups[0]
  const adminMutationBusy = !!action || !!sourceBusy || !!backupBusy
  const totalPages = Math.max(1, Math.ceil(total / pageSize))
  const sourceTotalPages = Math.max(1, Math.ceil(sourceTotal / sourcePageSize))
  const backupTotalPages = Math.max(1, Math.ceil(backupTotal / backupPageSize))

  const routeFallback = <main className="route-loading" aria-live="polite"><RefreshCw className="spin"/> Загружаем страницу…</main>
  if (loginPage) return <Suspense fallback={routeFallback}><AuthRoutePage kind="login"/></Suspense>
  if (registerPage) return <Suspense fallback={routeFallback}><AuthRoutePage kind="register"/></Suspense>
  if (forgotPasswordPage) return <Suspense fallback={routeFallback}><AuthRoutePage kind="forgot-password"/></Suspense>
  if (resetPasswordPage) return <Suspense fallback={routeFallback}><AuthRoutePage kind="reset-password"/></Suspense>
  if (accountOpen) return <AccountPage/>
  if (publicInfoPage && siteSettingsLoading) return routeFallback
  if (publicInfoPage && !siteSettings.sections[publicInfoPage].published) return <NotFoundPage/>
  if (publicInfoPage) return <Suspense fallback={routeFallback}><PublicInfoPage kind={publicInfoPage} apiBaseUrl={API}/></Suspense>
  if (currentPath !== '/' && !adminOpen) return <NotFoundPage/>

  return <div className="app-shell">
    {!adminOpen && <><header>
      <a className="brand" href="#top" aria-label="ProxyHarbor"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
      <nav><a href="#catalog">{t('proxies')}</a><a href="#vpn-catalog">{t('vpn')}</a>{siteSettings.sections.pricing.published&&siteSettings.sections.pricing.showInNavigation&&<a href="/pricing">Тарифы</a>}<a href="#api">API</a><LanguageSwitcher compact/><a className="admin-link" href="/login"><LockKeyhole size={15}/> {t('signIn')}</a></nav>
      <a className="mobile-admin" aria-label={t('signIn')} href="/login"><LockKeyhole size={17}/></a>
      <div className={`live-pill ${apiError ? 'offline' : ''}`} aria-live="polite"><span/> {loading ? t('systemChecking') : apiError ? t('systemOffline') : t('systemActive')}</div>
    </header>

    <main id="top">
      <section className="hero">
        <div className="eyebrow"><ShieldCheck size={15}/> {t('verifiedRealtime')}</div>
        <h1>{t('heroTitle')}<br/><em>{t('heroAccent')}</em></h1>
        <p>{t('heroText')}</p>
        <div className="hero-actions"><a className="primary" href="#catalog"><Wifi size={18}/> {t('openCatalog')}</a><a className="secondary" href="#api"><ArrowDownToLine size={18}/> {t('export')}</a></div>
        <div className="pulse-card">
          <div className="pulse-head"><span>{t('networkState')}</span><span className="updated"><RefreshCw size={13}/> {freshness}</span></div>
          <div className="pulse-number">{formatNumber(stats?.alive)}<small> {t('alive')}</small></div>
          <div className="protocol-strip">{protocols.map((item, index) => <div key={item} style={{'--share': `${Math.max(7, ((protocolCounts[item] ?? 0) / Math.max(1, stats?.alive ?? 1)) * 100)}%`, '--delay': `${index * 70}ms`} as React.CSSProperties}><span>{label(item)}</span><b>{formatNumber(protocolCounts[item] ?? 0)}</b></div>)}</div>
        </div>
      </section>

      <section className="metrics" aria-label={t('networkState')}>
        <Metric icon={<Activity/>} label={t('aliveAddresses')} value={formatNumber(stats?.alive)} note={stats?.staleAlive ? t('hiddenStale',{count:formatNumber(stats.staleAlive)}) : t('freshCheck')}/>
        <Metric icon={<Gauge/>} label={t('averageLatency')} value={stats?.averageLatencyMs ? `${Math.round(stats.averageLatencyMs)} ms` : '—'} note={t('controlHttps')}/>
        <Metric icon={<Database/>} label={t('dataFlow')} value="24 / 7" note={t('continuousCollection')}/>
        <Metric icon={<Clock3/>} label={t('readyForCheck')} value={formatNumber(stats?.dueForCheck)} note={t('queuedChecks',{running:formatNumber(stats?.checksInProgress),scheduled:formatNumber(stats?.scheduledChecks)})}/>
      </section>

      {commerce?.showOffer&&<SubscriptionOffer availability={commerce}/>}

      <section id="catalog" className="catalog">
        <div className="section-heading"><div><span className="kicker">LIVE CATALOG</span><h2>{t('liveCatalog')}</h2></div><p>{t('serverSelection',{count:formatNumber(total)})}</p></div>
        <div className="filters"><div className="tabs" aria-label={t('protocol')}><button aria-pressed={protocol === 'All'} className={protocol === 'All' ? 'active' : ''} onClick={() => changeProtocol('All')}>{t('all')}</button>{protocols.map(x => <button key={x} aria-pressed={protocol === x} className={protocol === x ? 'active' : ''} onClick={() => changeProtocol(x)}>{label(x)}</button>)}</div><div className="filter-tools"><CountryFilter countries={countries} selected={selectedCountries} onChange={changeCountries}/><label>{t('upTo')} <b>{maxLatency} ms</b><input type="range" min="200" max="5000" step="100" value={maxLatency} onChange={e => changeMaxLatency(Number(e.target.value))}/></label></div></div>
        <ToastSignal kind="error" message={apiError} action={{label:t('retry'),run:()=>{setApiError('');setLoading(true);void load()}}}/>
        <div className="proxy-table" role="table" aria-label={t('proxies')} aria-busy={loading}>
          <div role="rowgroup"><div className="table-row table-head" role="row"><span role="columnheader">{t('address')}</span><span role="columnheader">{t('country')}</span><span role="columnheader">{t('protocol')}</span><span role="columnheader">{t('latency')}</span><span role="columnheader">{t('reliability')}</span><span role="columnheader">{t('active')}</span><span role="columnheader">{t('checked')}</span></div></div>
          <div role="rowgroup">
            {loading ? <div role="row"><div className="empty" role="cell" aria-live="polite"><RefreshCw className="spin"/> {t('loadingCatalog')}</div></div> : proxies.length === 0 ? <div role="row"><div className="empty" role="cell" aria-live="polite"><Server/> {t('emptyCatalog')}</div></div> : proxies.map(proxy => <div className="table-row" role="row" key={proxy.url}><code role="cell">{proxy.host}<i>:</i>{proxy.port}</code><span role="cell" className="country-cell" title={proxy.countryCode ? countryName(proxy.countryCode) : t('countryPending')}>{proxy.countryCode ? <><CountryFlag code={proxy.countryCode}/><b>{countryName(proxy.countryCode)}</b></> : <em>—</em>}</span><span role="cell" className={`badge ${proxy.protocol.toLowerCase()}`}>{label(proxy.protocol)}</span><span role="cell" className="latency"><i className={proxy.latencyMs < 800 ? 'fast' : proxy.latencyMs < 1800 ? 'medium' : 'slow'}/>{proxy.latencyMs} ms</span><span role="cell">{proxy.successRate}%</span><span role="cell">{formatActiveDuration(proxy.activeForSeconds)}</span><span role="cell">{timeAgo(proxy.lastCheckedAt)}</span></div>)}
          </div>
        </div>
        {!loading&&catalogAccess.limited&&<AccessLimitNotice message={catalogAccess.message} accessible={catalogAccess.accessible} total={total} upgradeUrl={catalogAccess.upgradeUrl}/>}
        {!loading && total > 0 && catalogAccess.fullAccess && <ProxyPagination
          page={page} pageSize={pageSize} total={total} totalPages={totalPages}
          onPageChange={next => { setLoading(true); setPage(next); document.getElementById('catalog')?.scrollIntoView?.({ behavior: 'smooth' }) }}
          onPageSizeChange={size => { setLoading(true); setPageSize(size); setPage(1) }}/>
        }
      </section>

      <PublicVpnCatalog/>

      <PublicPricingSection apiBaseUrl={API} compact/>

      <section id="api" className="api-panel"><div><span className="kicker">ONE-CLICK EXPORT</span><h2>{t('exportTitle')}</h2><p>{t('exportText')}</p><small className="geo-attribution">IP geolocation: <a href="https://db-ip.com" target="_blank" rel="noreferrer">DB-IP</a></small></div><div className="export-grid">{['json','xml','txt','csv'].map(format => <a key={format} href={`${API}/api/v1/export/${format}?${exportQuery}`}><span>Proxy .{format}</span><ArrowDownToLine size={18}/></a>)}{['json','txt'].map(format=><a key={`vpn-${format}`} href={`${API}/api/v1/vpn/export/${format}`}><span>VPN .{format}</span><ArrowDownToLine size={18}/></a>)}</div><div className="endpoint"><span>GET</span><code>/api/v1/vpn?protocol=Vless&amp;country=DE</code></div></section>
    </main>

    <footer><div className="brand"><span className="brand-mark"><Network size={18}/></span><span>Proxy<span>Harbor</span></span></div><PublicSiteNavigation settings={siteSettings.sections}/><span>v{APP_VERSION} · © {new Date().getFullYear()}</span></footer></>}

    {adminOpen && <main className="admin-workspace">
      <aside className="admin-sidebar" aria-label="Навигация по панели управления">
        <a className="brand admin-brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
        <nav className="admin-nav-group" aria-label="Разделы админ-панели"><span>{t('management')}</span>
          <a className={adminSection === 'overview' ? 'active' : ''} aria-current={adminSection === 'overview' ? 'page' : undefined} href="/admin"><LayoutDashboard/>{t('overview')}</a>
          <a className={adminSection === 'operations' ? 'active' : ''} aria-current={adminSection === 'operations' ? 'page' : undefined} href="/admin/operations"><Workflow/>{t('operations')}</a>
          <a className={adminSection === 'checkers' ? 'active' : ''} aria-current={adminSection === 'checkers' ? 'page' : undefined} href="/admin/checkers"><Network/>Узлы проверки</a>
          <a className={adminSection === 'proxies' ? 'active' : ''} aria-current={adminSection === 'proxies' ? 'page' : undefined} href="/admin/proxies"><Wifi/>{t('proxies')} <b>{diagnostics?.validationQueue?.total || '—'}</b></a>
          <a className={adminSection === 'vpn' ? 'active' : ''} aria-current={adminSection === 'vpn' ? 'page' : undefined} href="/admin/vpn"><Radio/>VPN <b>{formatNumber(diagnostics?.vpnEndpoints)}</b></a>
          <a className={adminSection === 'sources' ? 'active' : ''} aria-current={adminSection === 'sources' ? 'page' : undefined} href="/admin/sources"><Server/>{t('sources')} <b>{sourceTotal || '—'}</b></a>
          <a className={adminSection === 'backups' ? 'active' : ''} aria-current={adminSection === 'backups' ? 'page' : undefined} href="/admin/backups"><HardDriveDownload/>{t('backups')}</a>
          <a className={adminSection === 'users' ? 'active' : ''} aria-current={adminSection === 'users' ? 'page' : undefined} href="/admin/users"><Users/>{t('users')}</a>
          <a className={adminSection === 'payments' ? 'active' : ''} aria-current={adminSection === 'payments' ? 'page' : undefined} href="/admin/payments"><CreditCard/>{t('payments')}</a>
          <a className={adminSection === 'telegram' ? 'active' : ''} aria-current={adminSection === 'telegram' ? 'page' : undefined} href="/admin/telegram"><Bot/>{t('telegramBot')}</a>
          <a className={adminSection === 'subscriptions' ? 'active' : ''} aria-current={adminSection === 'subscriptions' ? 'page' : undefined} href="/admin/subscriptions"><CalendarClock/>{t('subscriptions')}</a>
          <a className={adminSection === 'access' ? 'active' : ''} aria-current={adminSection === 'access' ? 'page' : undefined} href="/admin/access"><ShieldOff/>{t('accessIp')}</a>
          <a className={adminSection === 'site' ? 'active' : ''} aria-current={adminSection === 'site' ? 'page' : undefined} href="/admin/site"><Globe2/>Сайт и документы</a>
        </nav>
        <div className="admin-sidebar-foot"><LanguageSwitcher/><a href="/account"><User/>{t('profile')}</a><a href="/"><ArrowRight/>{t('home')}</a><button onClick={logoutAdmin}><LogOut/>{t('logout')}</button></div>
      </aside>

      <section className="admin-content">
        <header className="admin-content-header"><div><span className="kicker">ADMIN CONSOLE</span><strong>{t('panel')}</strong></div><div className="admin-session"><span/><div><b>{t('administrator')}</b><small>{t('secureSession')}</small></div></div></header>
        <ToastSignal kind="error" message={adminError}/>
        {adminLoading && !adminAuthenticated ? <div className="admin-initial-loading"><RefreshCw className="spin"/><span>Загружаем панель…</span></div> : <>
          {adminSection === 'overview' && <section className="admin-section" aria-labelledby="admin-overview-title">
            <AdminPageHeader id="admin-overview-title" title="Обзор"><button className="icon-button" aria-label="Обновить диагностику" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></AdminPageHeader>
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
            <AdminPageHeader id="admin-operations-title" title="Операции"><button className="icon-button" aria-label="Обновить диагностику" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></AdminPageHeader>
            <div className="operation-actions">
              <button ref={firstAdminActionRef} onClick={() => runAdminAction('collect')} disabled={!adminAuthenticated || adminMutationBusy}><span><Play/></span><div><b>{action === 'collect' ? 'Собираем…' : 'Запустить сбор'}</b><small>Получить свежие адреса из всех активных источников</small></div><ArrowRight/></button>
              <button onClick={() => runAdminAction('validate')} disabled={!adminAuthenticated || adminMutationBusy}><span><Check/></span><div><b>{action === 'validate' ? 'Проверяем…' : 'Проверить пакет'}</b><small>Немедленно запустить очередную порцию проверок</small></div><ArrowRight/></button>
            </div>
            <section className="admin-card queue-card"><div className="card-heading"><div><span className="kicker">ВАЛИДАТОР</span><h2>Очередь проверки</h2></div><strong>{formatNumber(diagnostics?.validationQueue?.due)}</strong></div><div className="queue-metrics"><div><span>Скорость</span><b>{formatRate(diagnostics?.validationQueue?.checksPerSecond)}</b></div><div><span>Попыток за 5 минут</span><b>{formatNumber(diagnostics?.validationQueue?.attemptsLastFiveMinutes)}</b></div><div><span>Живых за 5 минут</span><b>{formatNumber(diagnostics?.validationQueue?.aliveLastFiveMinutes)}</b></div><div><span>Оценка завершения</span><b>{formatDuration(diagnostics?.validationQueue?.estimatedDrainSeconds)}</b></div></div></section>
            <div className="history-columns"><AdminRunHistory title="Последние сборы" empty="Сборы ещё не запускались.">{diagnostics?.recentRuns.slice(0, 8).map(run => <HistoryRow key={run.id} status={run.candidateLimitReached || run.sourcesTruncated ? 'attention' : run.status} title={`${formatNumber(run.sourcesSucceeded)}/${formatNumber(run.sourcesProcessed)} источников`} detail={`${formatNumber(run.candidatesFound)} кандидатов · +${formatNumber(run.newProxies)} новых`} time={run.startedAt}/>)}</AdminRunHistory><AdminRunHistory title="Последние проверки" empty="Проверки ещё не запускались.">{(diagnostics?.recentValidationRuns ?? []).slice(0, 8).map(run => <HistoryRow key={run.id} status={run.status} title={`${formatNumber(run.checked + run.deferred)}/${formatNumber(run.claimed)} попыток`} detail={`${formatNumber(run.alive)} живых · ${run.finishedAt ? formatDuration((new Date(run.finishedAt).getTime() - new Date(run.startedAt).getTime()) / 1000) : 'выполняется'}`} time={run.startedAt}/>)}</AdminRunHistory></div>
          </section>}

          {adminSection === 'sources' && <section className="admin-section" aria-labelledby="admin-sources-title">
            <AdminPageHeader id="admin-sources-title" title="Источники"><button className="primary-admin-button" onClick={openNewSource} disabled={!adminAuthenticated || adminMutationBusy}><Plus/>Добавить источник</button></AdminPageHeader>
            <nav className="admin-tabs source-type-tabs" aria-label="Тип источников"><a className="active" aria-current="page" href="/admin/sources">Прокси</a><a href="/admin/vpn?tab=sources">VPN</a></nav>
            <section className="admin-card source-catalog-card"><div className="card-heading"><div><span className="kicker">ВСЕ ИСТОЧНИКИ</span><h2>Каталог <em>{sourceTotal}</em></h2></div><button className="icon-button" aria-label="Обновить источники" onClick={() => void loadAdminData()} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></div><form className="source-search" role="search" onSubmit={event => { event.preventDefault(); const nextSearch = sourceSearchDraft.trim(); setSourceSearch(nextSearch); setSourcePage(1); void loadAdminData(false, 1, sourcePageSize, nextSearch) }}><Search aria-hidden="true"/><input type="search" aria-label="Поиск источников" maxLength={200} placeholder="Название, провайдер или адрес feed" value={sourceSearchDraft} onChange={event => setSourceSearchDraft(event.target.value)}/>{sourceSearchDraft && <button type="button" className="source-search-clear" aria-label="Очистить поиск" onClick={() => { setSourceSearchDraft(''); setSourceSearch(''); setSourcePage(1); void loadAdminData(false, 1, sourcePageSize, '') }}><X/></button>}<button type="submit" className="source-search-submit" disabled={adminLoading}>Найти</button></form><div className="source-list">{sources.length === 0 ? <div className="source-search-empty"><Search/>По вашему запросу источники не найдены.</div> : sources.map(source => <article key={source.id}>
              <div><b>{source.name}</b><small>{source.defaultProtocol} · {source.lastItemCount.toLocaleString('ru-RU')} адресов{source.lastContentFetchedAt ? ` · полный feed ${new Date(source.lastContentFetchedAt).toLocaleString('ru-RU')}` : ' · полный feed ещё не получен'}{source.lastResultTruncated ? ' · результат усечён' : ''}{source.consecutiveFailures > 0 ? ` · сбоев подряд: ${source.consecutiveFailures}` : ''}{source.nextFetchAt ? ` · повтор ${timeUntil(source.nextFetchAt)}` : ''}</small></div>
              <div className="source-controls"><span title={source.isBuiltIn ? `Встроенный источник · ${source.provider} · ${source.providerIdentity} · ранг ${source.catalogRank}` : 'Пользовательский источник'} className="source-kind">{source.isBuiltIn ? source.provider : 'свой'}</span><span title={source.lastError} className={source.lastError ? 'source-error' : 'source-ok'}>{source.lastError ? 'ошибка' : source.enabled ? 'активен' : 'пауза'}</span><button className="source-edit-button" disabled={adminMutationBusy} onClick={() => openSourceEditor(source)}><Pencil/>Изменить</button></div>
            </article>)}</div>{sourceTotal > 0 && <ProxyPagination page={sourcePage} pageSize={sourcePageSize} total={sourceTotal} totalPages={sourceTotalPages} onPageChange={next => { setSourcePage(next); void loadAdminData(false, next, sourcePageSize, sourceSearch); document.getElementById('admin-sources-title')?.scrollIntoView?.({behavior:'smooth'}) }} onPageSizeChange={size => { setSourcePageSize(size); setSourcePage(1); void loadAdminData(false, 1, size, sourceSearch) }}/>}</section>
          </section>}

          {adminSection === 'backups' && <section className="admin-section" aria-labelledby="admin-backups-title">
            <AdminPageHeader id="admin-backups-title" title="Резервные копии"><button className="primary-admin-button" onClick={() => runAdminAction('backup')} disabled={!adminAuthenticated || adminMutationBusy}><Database/>{action === 'backup' ? 'Создаём…' : 'Создать backup'}</button></AdminPageHeader>
            <div className="backup-summary"><article><span><Database/></span><div><small>Размер базы</small><strong>{formatBytes(diagnostics?.databaseBytes)}</strong></div></article><article><span><HardDriveDownload/></span><div><small>Последняя копия</small><strong>{latestBackup ? formatBytes(latestBackup.sizeBytes) : '—'}</strong></div></article><article><span><ShieldCheck/></span><div><small>Доставка</small><strong>{latestBackup ? backupDelivery(latestBackup) : 'Нет данных'}</strong></div></article></div>
            <AdminBackupSettings onError={setAdminError}/>
            <section className="admin-card backup-registry"><div className="card-heading"><div><span className="kicker">ИСТОРИЯ</span><h2>Резервные копии <em>{backupTotal}</em></h2></div><button className="icon-button" aria-label="Обновить резервные копии" onClick={() => void loadBackups()} disabled={!!backupBusy}><RefreshCw/></button></div><div className="backup-list">{backups.length === 0 ? <p className="empty-state">Резервные копии ещё не создавались.</p> : backups.map(run => <article key={run.id}><i className={statusClass(run.status)}/><div><div className="backup-file-heading"><b>{run.fileName ?? 'Резервная копия'}</b><time dateTime={run.startedAt}>{formatDateTime(run.startedAt)}</time></div><small>{formatBytes(run.sizeBytes)} · {backupDelivery(run)}{run.fileName && !run.available ? ' · локальный архив удалён по настроенному сроку хранения; запись аудита сохранена' : ''}</small></div><time className="backup-relative-time" dateTime={run.startedAt}>{timeAgo(run.startedAt)}</time><div className="backup-actions">{run.available ? <a className="backup-action-icon" data-tooltip="Скачать зашифрованный архив" aria-label={`Скачать ${run.fileName ?? 'резервную копию'}`} href={`${API}/api/v1/admin/backups/${run.id}/download`} download={run.fileName}><ArrowDownToLine/></a> : <button className="backup-action-icon" data-tooltip="Архив уже удалён по политике хранения" aria-label="Архив недоступен для скачивания" disabled><ArrowDownToLine/></button>}<button className="backup-action-icon danger" data-tooltip="Удалить архив с сервера и запись истории" aria-label={`Удалить ${run.fileName ?? 'резервную копию'}`} disabled={run.status === 'running' || !!backupBusy} onClick={() => setBackupDeleteTarget(run)}><Trash2/></button></div></article>)}</div>{backupTotal > 0 && <ProxyPagination page={backupPage} pageSize={backupPageSize} total={backupTotal} totalPages={backupTotalPages} onPageChange={next => { setBackupPage(next); document.getElementById('admin-backups-title')?.scrollIntoView?.({behavior:'smooth'}) }} onPageSizeChange={size => { setBackupPageSize(size); setBackupPage(1) }}/>}</section>
          </section>}
          {adminSection === 'users' && <AdminUsersPage/>}
          {adminSection === 'checkers' && <AdminCheckerNodesPage/>}
          {adminSection === 'proxies' && <AdminProxiesPage/>}
          {adminSection === 'vpn' && <AdminVpnPage/>}
          {adminSection === 'payments' && <AdminPaymentsPage/>}
          {adminSection === 'telegram' && <AdminTelegramPage/>}
          {adminSection === 'subscriptions' && <AdminSubscriptionsPage/>}
          {adminSection === 'access' && <AdminAccessPage/>}
          {adminSection === 'site' && <Suspense fallback={<div className="admin-initial-loading"><RefreshCw className="spin"/><span>Загружаем настройки…</span></div>}><AdminSiteSettingsPage/></Suspense>}
        </>}
      </section>
    </main>}

    {sourceEditorOpen && <div className="source-editor-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget) closeSourceEditor() }}>
      <section className="source-editor-modal" role="dialog" aria-modal="true" aria-labelledby="source-editor-title">
        <div className="source-editor-heading"><div><span className="kicker">{editingSource ? 'НАСТРОЙКА FEED' : 'НОВЫЙ FEED'}</span><h2 id="source-editor-title">{editingSource ? 'Редактировать источник' : 'Добавить источник'}</h2><p>{editingSource?.isBuiltIn ? 'У встроенного источника можно изменить только активность.' : 'Укажите публичный HTTPS-адрес и параметры разбора списка прокси.'}</p></div><button type="button" className="icon-button" aria-label="Закрыть редактор источника" onClick={closeSourceEditor} disabled={!!sourceBusy}><X/></button></div>
        <ToastSignal kind="error" message={adminError}/>
        <form className="source-editor-form" onSubmit={saveSource}>
          <label>Название<input autoFocus required minLength={2} maxLength={120} disabled={editingSource?.isBuiltIn} value={sourceDraft.name} onChange={event => setSourceDraft({...sourceDraft, name:event.target.value})}/></label>
          <label>HTTPS URL<input required type="url" maxLength={2048} pattern="https://.*" disabled={editingSource?.isBuiltIn} placeholder="https://example.org/proxies.txt" value={sourceDraft.url} onChange={event => setSourceDraft({...sourceDraft, url:event.target.value})}/></label>
          <div className="source-editor-grid"><label>Протокол<StyledSelect ariaLabel="Протокол источника" disabled={editingSource?.isBuiltIn} value={sourceDraft.protocol} onChange={protocol => setSourceDraft({...sourceDraft, protocol:protocol as Protocol})} options={protocols.map(item=>[item,label(item)] as const)}/></label><label>Приоритет<input type="number" min={-10000} max={10000} disabled={editingSource?.isBuiltIn} value={sourceDraft.priority} onChange={event => setSourceDraft({...sourceDraft, priority:Number(event.target.value)})}/></label></div>
          <label className="source-enabled"><input className="ui-checkbox-input" type="checkbox" checked={sourceDraft.enabled} onChange={event => setSourceDraft({...sourceDraft, enabled:event.target.checked})}/><CheckboxMark/><span className="source-enabled-copy"><b>Источник активен</b><small>Активные источники участвуют в очередном цикле сбора.</small></span></label>
          {sourceDeleteConfirm && editingSource && <div className="source-delete-confirm" role="alert"><div><b>{editingSource.isBuiltIn ? 'Отключить встроенный источник?' : 'Удалить источник безвозвратно?'}</b><p>{editingSource.isBuiltIn ? 'Он останется в каталоге и его можно будет включить позже.' : 'Запись источника будет удалена. Уже собранные прокси сохранятся в базе.'}</p></div><button type="button" onClick={() => setSourceDeleteConfirm(false)} disabled={!!sourceBusy}>Отмена</button><button type="button" className="danger" onClick={() => void removeSource(editingSource)} disabled={!!sourceBusy}>{sourceBusy ? 'Выполняем…' : editingSource.isBuiltIn ? 'Отключить' : 'Удалить'}</button></div>}
          <div className="source-editor-actions">{editingSource && !sourceDeleteConfirm && <button type="button" className="danger-link" onClick={() => setSourceDeleteConfirm(true)} disabled={!!sourceBusy}><Trash2/>{editingSource.isBuiltIn ? 'Отключить источник' : 'Удалить источник'}</button>}<span/><button type="button" className="secondary-admin-button" onClick={closeSourceEditor} disabled={!!sourceBusy}>Отмена</button><button type="submit" className="primary-admin-button" disabled={!adminAuthenticated || adminMutationBusy}>{sourceBusy ? 'Сохраняем…' : editingSource ? 'Сохранить изменения' : 'Добавить источник'}</button></div>
        </form>
      </section>
    </div>}
    {backupDeleteTarget && <div className="source-editor-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !backupBusy) setBackupDeleteTarget(null) }}><section className="source-editor-modal backup-delete-modal" role="dialog" aria-modal="true" aria-labelledby="backup-delete-title"><div className="source-editor-heading"><div><span className="kicker">ОПАСНОЕ ДЕЙСТВИЕ</span><h2 id="backup-delete-title">Удалить резервную копию?</h2><p>Файл <b>{backupDeleteTarget.fileName ?? 'без имени'}</b> и его запись в истории будут удалены без возможности восстановления.</p></div><button className="icon-button" aria-label="Отмена" onClick={() => setBackupDeleteTarget(null)} disabled={!!backupBusy}><X/></button></div><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={() => setBackupDeleteTarget(null)} disabled={!!backupBusy}>Отмена</button><button className="danger-link" onClick={() => void deleteBackup()} disabled={!!backupBusy}><Trash2/>{backupBusy ? 'Удаляем…' : 'Удалить навсегда'}</button></div></section></div>}
  </div>
}

function PublicSiteNavigation({settings}:{settings:SiteSettings['sections']}) {
  const visible = siteSectionCodes.filter(code => settings[code].published && settings[code].showInNavigation)
  return <nav aria-label="Информация о сервисе">{visible.map(code=><a key={code} href={sectionHref(code)}>{siteSectionLabels[code]}</a>)}</nav>
}

function NotFoundPage() {
  return <main className="login-page not-found-page">
    <a className="brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
    <section className="login-card account-auth-card" aria-labelledby="not-found-title">
      <span className="kicker">404 · NOT FOUND</span>
      <h1 id="not-found-title">Страница не найдена</h1>
      <p>Адрес мог измениться или был введён с ошибкой.</p>
      <a className="primary" href="/">Вернуться на главную</a>
    </section>
  </main>
}

/** Публичный VPN-каталог отдаёт готовые опубликованные URI без раскрытия внутренних feed. */
function PublicVpnCatalog() {
  const { t } = useI18n()
  const protocols: VpnProtocol[] = ['OpenVpn','WireGuard','Vless','Vmess','Trojan','Shadowsocks','Hysteria2','Tuic']
  const [items,setItems] = useState<VpnEndpoint[]>([])
  const [countries,setCountries] = useState<ProxyCountry[]>([])
  const [selectedCountries,setSelectedCountries] = useState<string[]>([])
  const [protocol,setProtocol] = useState<VpnProtocol|'All'>('All')
  const [page,setPage] = useState(1)
  const [pageSize,setPageSize] = useState(10)
  const [total,setTotal] = useState(0)
  const [access,setAccess] = useState<{fullAccess:boolean;limited:boolean;message?:string;accessible?:number;upgradeUrl?:string}>({fullAccess:false,limited:false})
  const [copied,setCopied] = useState('')
  const [loading,setLoading] = useState(true)
  const [error,setError] = useState('')

  const load = useCallback(async (signal?:AbortSignal) => {
    setLoading(true)
    try {
      const query = new URLSearchParams({page:String(page),pageSize:String(pageSize),status:'Reachable'})
      if (protocol !== 'All') query.set('protocol',protocol)
      selectedCountries.forEach(country=>query.append('country',country))
      const [response,countriesResponse] = await Promise.all([
        fetch(`${API}/api/v1/vpn?${query}`,{signal,credentials:'include',headers:{'Accept-Language':currentLocale()}}),
        fetch(`${API}/api/v1/vpn/countries`,{signal})
      ])
      if (!response.ok||!countriesResponse.ok) throw new Error(await responseMessage(response,t('vpnCatalogUnavailable')))
      const [snapshot,countrySnapshot] = await Promise.all([
        response.json() as Promise<PagedResult<VpnEndpoint>>,
        countriesResponse.json() as Promise<ProxyCountry[]>
      ])
      if (signal?.aborted) return
      setItems(snapshot.items)
      setTotal(snapshot.total)
      setCountries(countrySnapshot)
      setAccess({fullAccess:snapshot.fullAccess??!snapshot.limited,limited:Boolean(snapshot.limited),message:snapshot.message,accessible:snapshot.accessible,upgradeUrl:snapshot.upgradeUrl})
      if (snapshot.limited) {
        if (page!==1) setPage(1)
      } else {
        const availablePages = Math.max(1,Math.ceil(snapshot.total/pageSize))
        if (page > availablePages) setPage(availablePages)
      }
      setError('')
    } catch(reason) {
      if (!isAbortError(reason)) setError(reason instanceof Error ? reason.message : t('vpnCatalogUnavailable'))
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  },[page,pageSize,protocol,selectedCountries,t])

  useEffect(()=>{
    const controller = new AbortController()
    const timer = window.setTimeout(()=>void load(controller.signal),0)
    return()=>{window.clearTimeout(timer);controller.abort()}
  },[load])

  const totalPages = Math.max(1,Math.ceil(total/pageSize))
  return <section id="vpn-catalog" className="catalog public-vpn-catalog" aria-labelledby="vpn-catalog-title">
    <div className="section-heading"><div><span className="kicker">VPN CATALOG</span><h2 id="vpn-catalog-title">{t('vpnCatalog')}</h2><p className="vpn-catalog-description">{t('vpnCatalogText')}</p></div><p>{t('vpnServerSelection',{count:formatNumber(total)})}</p></div>
    <div className="vpn-public-toolbar"><div className="vpn-filter-group"><StyledSelect ariaLabel={t('vpnProtocolFilter')} value={protocol} onChange={value=>{setProtocol(value as VpnProtocol|'All');setPage(1)}} options={[['All',t('allVpnProtocols')],...protocols.map(value=>[value,value] as [string,string])]}/><CountryFilter countries={countries} selected={selectedCountries} onChange={value=>{setSelectedCountries(value);setPage(1)}}/></div><span className="vpn-safe-status"><ShieldCheck/>{t('available')}</span></div>
    <ToastSignal kind="error" message={error} action={{label:t('retry'),run:()=>void load()}}/>
    <div className="public-vpn-table" role="table" aria-label={t('vpnCatalog')} aria-busy={loading}>
      <div role="rowgroup"><div className="vpn-public-row vpn-public-head" role="row"><span role="columnheader">{t('address')}</span><span role="columnheader">{t('country')}</span><span role="columnheader">{t('protocol')}</span><span role="columnheader">{t('latency')}</span><span role="columnheader">{t('checked')}</span><span role="columnheader">{t('vpnConfig')}</span></div></div>
      <div role="rowgroup">{loading?<div role="row"><div className="empty" role="cell" aria-live="polite"><RefreshCw className="spin"/> {t('loadingVpnCatalog')}</div></div>:items.length===0?<div role="row"><div className="empty" role="cell" aria-live="polite"><Radio/> {t('emptyVpnCatalog')}</div></div>:items.map(item=><div className="vpn-public-row" role="row" key={item.id}><code role="cell">{item.host}<i>:</i>{item.port}</code><span role="cell" className="country-cell" title={item.countryCode?countryName(item.countryCode):t('countryPending')}>{item.countryCode?<><CountryFlag code={item.countryCode}/><b>{countryName(item.countryCode)}</b></>:<em>—</em>}</span><span role="cell" className="vpn-protocol-badge">{item.protocol}</span><span role="cell" className="latency"><i className={item.latencyMs!=null&&item.latencyMs<800?'fast':item.latencyMs!=null&&item.latencyMs<1800?'medium':'slow'}/>{item.latencyMs!=null?`${item.latencyMs} ms`:'—'}</span><time role="cell" dateTime={item.lastCheckedAt}>{item.lastCheckedAt?timeAgo(item.lastCheckedAt):'—'}</time><button role="cell" className="vpn-copy-button" disabled={!item.connectionUri} data-tooltip={item.connectionUri?t('copyVpn'):t('vpnLinkUnavailable')} aria-label={item.connectionUri?t('copyVpn'):t('vpnLinkUnavailable')} onClick={()=>{if(!item.connectionUri)return;void copyText(item.connectionUri).then(()=>{setCopied(item.id);window.setTimeout(()=>setCopied(current=>current===item.id?'':current),1800)})}}>{copied===item.id?<Check/>:<Copy/>}<span>{copied===item.id?t('copied'):t('copy')}</span></button></div>)}</div>
    </div>
    {!loading&&access.limited&&<AccessLimitNotice message={access.message} accessible={access.accessible} total={total} upgradeUrl={access.upgradeUrl}/>}
    {!loading&&total>0&&access.fullAccess&&<ProxyPagination page={page} pageSize={pageSize} total={total} totalPages={totalPages} onPageChange={next=>{setPage(next);document.getElementById('vpn-catalog')?.scrollIntoView?.({behavior:'smooth'})}} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}
  </section>
}

/** Коммерческое предложение появляется только когда сервер подтвердил готовый способ оплаты. */
function SubscriptionOffer({availability}:{availability:CommerceAvailability}) {
  const {t}=useI18n()
  return <section className="subscription-offer" aria-labelledby="subscription-offer-title">
    <div className="subscription-offer-icon"><Star/></div>
    <div><span className="kicker">PREMIUM ACCESS</span><h2 id="subscription-offer-title">{t('subscriptionOfferTitle')}</h2><p>{t('subscriptionOfferText')}</p><ul><li><Check/>{t('subscriptionBenefitCatalog')}</li><li><Check/>{t('subscriptionBenefitExports')}</li><li><Check/>{t('subscriptionBenefitCountries')}</li></ul></div>
    <div className="subscription-offer-action"><small>{availability.telegram&&availability.paymentProviders>0?t('paymentWebAndTelegram'):availability.telegram?t('paymentTelegram'):t('paymentWeb')}</small><a className="primary" href={availability.accountUrl}><CreditCard/>{t('getSubscription')}</a></div>
  </section>
}

/** Единое объяснение ограничения бесплатного тарифа для обоих каталогов. */
function AccessLimitNotice({message,accessible,total,upgradeUrl}:{message?:string;accessible?:number;total:number;upgradeUrl?:string}) {
  const {t}=useI18n()
  return <aside className="catalog-access-notice" role="note"><div><LockKeyhole/><p><strong>{t('freeCatalog')}</strong><span>{message??t('subscriptionUnlocks',{count:formatNumber(total)})}</span><small>{t('availableNow',{accessible:formatNumber(accessible??0),total:formatNumber(total)})}</small></p></div><a className="primary" href={upgradeUrl??'/account'}>{t('getSubscription')}<ArrowRight/></a></aside>
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
function AccountPage() {
  const { language, setLanguage, t } = useI18n()
  const [accountTab, setAccountTab] = useState<'overview'|'profile'|'tokens'|'referrals'|'billing'>('overview')
  const [profile, setProfile] = useState<AccountProfile | null>(null)
  const [displayName, setDisplayName] = useState('')
  const [passwords, setPasswords] = useState({currentPassword: '', newPassword: ''})
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [payments, setPayments] = useState<PaymentCatalog | null>(null)
  const [orders, setOrders] = useState<PaymentOrder[]>([])
  const [checkoutBusy, setCheckoutBusy] = useState('')
  const [tokenName,setTokenName] = useState('Основной API-токен')
  const [issuedToken,setIssuedToken] = useState('')
  const [tokenBusy,setTokenBusy] = useState('')
  const [tokenHistory,setTokenHistory] = useState<ApiTokenHistoryPage|null>(null)
  const [tokenHistoryPage,setTokenHistoryPage] = useState(1)
  const [referrals,setReferrals] = useState<ReferralPage|null>(null)
  const [referralPage,setReferralPage] = useState(1)
  const loadProfile = useCallback(async () => {
    const [response, catalogResponse, ordersResponse] = await Promise.all([
      fetch(`${API}/api/v1/account/profile`, {credentials: 'include'}),
      fetch(`${API}/api/v1/payments/catalog`, {credentials: 'include'}),
      fetch(`${API}/api/v1/payments/orders`, {credentials: 'include'}),
    ])
    if (response.status === 401) { window.location.replace('/login'); return }
    if (!response.ok) { setError(await responseMessage(response, 'Профиль недоступен')); return }
    const data = await response.json() as AccountProfile; setProfile(data); setDisplayName(data.displayName ?? ''); setLanguage(data.preferredLanguage)
    if (catalogResponse.ok) setPayments(await catalogResponse.json() as PaymentCatalog)
    if (ordersResponse.ok) setOrders(await ordersResponse.json() as PaymentOrder[])
  }, [setLanguage])
  useEffect(() => { const initial = window.setTimeout(() => void loadProfile(), 0); return () => window.clearTimeout(initial) }, [loadProfile])
  const loadReferrals = useCallback(async () => {
    const response = await fetch(`${API}/api/v1/account/referrals?page=${referralPage}&pageSize=10`, {credentials:'include'})
    if (!response.ok) { setError(await responseMessage(response,'История приглашений недоступна')); return }
    setReferrals(await response.json() as ReferralPage)
  }, [referralPage])
  useEffect(()=>{if(accountTab!=='referrals')return;const timer=window.setTimeout(()=>void loadReferrals(),0);return()=>window.clearTimeout(timer)},[accountTab,loadReferrals])
  const loadTokenHistory = useCallback(async () => {
    const response = await fetch(`${API}/api/v1/account/api-tokens/history?page=${tokenHistoryPage}&pageSize=10`, {credentials:'include'})
    if (!response.ok) { setError(await responseMessage(response,t('tokenHistoryUnavailable'))); return }
    setTokenHistory(await response.json() as ApiTokenHistoryPage)
  }, [t,tokenHistoryPage])
  useEffect(()=>{if(accountTab!=='tokens')return;const timer=window.setTimeout(()=>void loadTokenHistory(),0);return()=>window.clearTimeout(timer)},[accountTab,loadTokenHistory])
  const saveProfile = async (event: React.FormEvent) => { event.preventDefault(); setError(''); const response = await fetch(`${API}/api/v1/account/profile`, {method:'PUT', credentials:'include', headers:{'Content-Type':'application/json'}, body:JSON.stringify({displayName,preferredLanguage:language})}); if (!response.ok) {setError(await responseMessage(response,'Не удалось сохранить профиль'));return} setNotice(t('profileSaved')); await loadProfile() }
  const changePassword = async (event: React.FormEvent) => { event.preventDefault(); setError(''); const response = await fetch(`${API}/api/v1/account/change-password`, {method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(passwords)}); if(!response.ok){setError(await responseMessage(response,'Не удалось изменить пароль'));return} setPasswords({currentPassword:'',newPassword:''});setNotice('Пароль изменён. Другие сессии будут отозваны.') }
  const logout = async () => { await fetch(`${API}/api/v1/auth/logout`, {method:'POST',credentials:'include'}); window.location.replace('/login') }
  const checkout = async (productCode:string, provider:string) => { const key=`${productCode}:${provider}`;setCheckoutBusy(key);setError('');const response=await fetch(`${API}/api/v1/payments/checkout`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({productCode,provider})});if(!response.ok){setError(await responseMessage(response,'Не удалось открыть оплату'));setCheckoutBusy('');return}const result=await response.json() as {checkoutUrl:string};window.location.assign(result.checkoutUrl) }
  const issueApiToken=async()=>{setTokenBusy('issue');setError('');const response=await fetch(`${API}/api/v1/account/api-tokens`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({name:tokenName})});if(!response.ok){setError(await responseMessage(response,'Не удалось создать токен'));setTokenBusy('');return}const created=await response.json() as {token:string};setIssuedToken(created.token);setNotice(t('tokenShownOnce'));setTokenBusy('');await loadProfile()}
  const revokeApiToken=async(id:string)=>{setTokenBusy(id);setError('');const response=await fetch(`${API}/api/v1/account/api-tokens/${id}`,{method:'DELETE',credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Не удалось отозвать токен'));setTokenBusy('');return}setTokenBusy('');await loadProfile();await loadTokenHistory()}
  const products = [...(payments?.products ?? [])].sort((left,right)=>left.durationDays-right.durationDays)
  const availableProviders = payments?.providers.filter(provider=>provider.available) ?? []
  const subscriptionActive = profile?.subscription?.status?.toLowerCase() === 'active'
  const selectAccountTab = (tab: typeof accountTab) => { setAccountTab(tab); setNotice(''); setError('') }
  return <main className="account-page"><header><a className="brand" href="/"><span className="brand-mark"><Network/></span><span>Proxy<span>Harbor</span></span></a><div><LanguageSwitcher compact/><a href="/">{t('home')}</a>{profile?.roles.includes('Administrator') && <a href="/admin">{t('admin')}</a>}<button onClick={logout}><LogOut/>{t('logout')}</button></div></header><section className="account-container"><div className="admin-section-heading"><div><span className="kicker">PERSONAL ACCOUNT</span><h1>{t('profile')}</h1><p>{t('profileText')}</p></div></div><ToastSignal kind="error" message={error}/><ToastSignal kind="success" message={notice}/>
    <nav className="account-tabs" role="tablist" aria-label={t('accountSections')}>
      <button role="tab" aria-selected={accountTab==='overview'} className={accountTab==='overview'?'active':''} onClick={()=>selectAccountTab('overview')}><LayoutDashboard/>{t('accountOverview')}</button>
      <button role="tab" aria-selected={accountTab==='profile'} className={accountTab==='profile'?'active':''} onClick={()=>selectAccountTab('profile')}><User/>{t('personalData')}</button>
      <button role="tab" aria-selected={accountTab==='tokens'} className={accountTab==='tokens'?'active':''} onClick={()=>selectAccountTab('tokens')}><ShieldCheck/>{t('apiAccess')}</button>
      <button role="tab" aria-selected={accountTab==='referrals'} className={accountTab==='referrals'?'active':''} onClick={()=>selectAccountTab('referrals')}><Workflow/>Рефералы</button>
      <button role="tab" aria-selected={accountTab==='billing'} className={accountTab==='billing'?'active':''} onClick={()=>selectAccountTab('billing')}><CreditCard/>{t('plansAndPayments')}</button>
    </nav>
    {accountTab==='overview'&&<div className="account-overview" role="tabpanel">
      <div className="account-profile-grid"><section className="admin-card profile-card"><div className="account-identity"><div className="profile-avatar"><User/></div><div><span className="kicker">{t('yourAccount')}</span><h2>{profile?.displayName || profile?.userName || t('loading')}</h2><p>{profile?.email}</p></div></div><div className="role-badges">{profile?.roles.map(role => <span key={role}>{role==='Administrator'?t('administrator'):role}</span>)}</div><dl className="account-facts"><div><dt>{t('login')}</dt><dd>{profile?.userName ?? '—'}</dd></div><div><dt>{t('accountCreated')}</dt><dd>{profile?.createdAt?formatDateTime(profile.createdAt):'—'}</dd></div><div><dt>{t('lastLogin')}</dt><dd>{profile?.lastLoginAt?timeAgo(profile.lastLoginAt):'—'}</dd></div></dl></section><section className={`admin-card subscription-card${subscriptionActive?' active':''}`}><span className="kicker">{t('subscription')}</span><CreditCard/><strong>{planLabel(profile?.subscription?.plan)}</strong><span className="subscription-state"><i/>{profile?.subscription?localizedSubscriptionStatusLabel(profile.subscription.status,language):t('loading')}</span><dl><div><dt>{t('validUntil')}</dt><dd>{profile?.subscription?.expiresAt?formatDateTime(profile.subscription.expiresAt):t('noExpiry')}</dd></div><div><dt>{t('catalogAccess')}</dt><dd>{profile?.entitlements.unlimitedProxyAccess?t('unlimitedAccess'):t('freeAccess')}</dd></div></dl></section></div>
      <section className="account-shortcuts" aria-label={t('quickActions')}><button onClick={()=>selectAccountTab('profile')}><span><User/></span><div><b>{t('manageProfile')}</b><small>{t('manageProfileHint')}</small></div><ArrowRight/></button><button onClick={()=>selectAccountTab('tokens')}><span><ShieldCheck/></span><div><b>{t('manageTokens')}</b><small>{t('manageTokensHint')}</small></div><ArrowRight/></button><button onClick={()=>selectAccountTab('billing')}><span><CreditCard/></span><div><b>{t('choosePlan')}</b><small>{t('choosePlanHint')}</small></div><ArrowRight/></button></section>
    </div>}
    {accountTab==='profile'&&<div className="account-forms" role="tabpanel"><section className="admin-card account-form-card"><div className="card-heading"><div><span className="kicker">ACCOUNT</span><h2>{t('personalData')}</h2><p>{t('personalDataHint')}</p></div><User/></div><form onSubmit={saveProfile}><label htmlFor="profile-login">{t('login')}</label><input id="profile-login" disabled value={profile?.userName ?? ''}/><label htmlFor="profile-email">Email</label><input id="profile-email" disabled value={profile?.email ?? ''}/><label htmlFor="profile-name">{t('displayName')}</label><input id="profile-name" maxLength={120} value={displayName} onChange={event=>setDisplayName(event.target.value)}/><label>{t('preferredLanguage')}<LanguageSwitcher/></label><small>{t('languageHint')}</small><button>{t('save')}</button></form></section><section className="admin-card account-form-card"><div className="card-heading"><div><span className="kicker">SECURITY</span><h2>{t('security')}</h2><p>{t('securityHint')}</p></div><LockKeyhole/></div><form onSubmit={changePassword}><label htmlFor="current-password">{t('currentPassword')}</label><input id="current-password" required type="password" autoComplete="current-password" value={passwords.currentPassword} onChange={event=>setPasswords({...passwords,currentPassword:event.target.value})}/><label htmlFor="new-password">{t('newPassword')}</label><input id="new-password" required minLength={12} type="password" autoComplete="new-password" value={passwords.newPassword} onChange={event=>setPasswords({...passwords,newPassword:event.target.value})}/><small>{t('passwordHint')}</small><button>{t('changePassword')}</button></form></section></div>}
    {accountTab==='tokens'&&<section className="admin-card api-token-card" role="tabpanel"><div className="card-heading"><div><span className="kicker">SECURE API ACCESS</span><h2>{t('apiTokens')}</h2><p>{t('apiTokenProfileHint')}</p></div><ShieldCheck/></div>{!profile?.entitlements.apiTokens?<div className="token-locked"><LockKeyhole/><span>{t('tokenRequiresSubscription')}</span><button onClick={()=>selectAccountTab('billing')}>{t('choosePlan')}</button></div>:<><div className="token-issue"><label htmlFor="token-name">{t('tokenName')}</label><div><input id="token-name" maxLength={80} value={tokenName} onChange={event=>setTokenName(event.target.value)}/><button className="primary-admin-button" disabled={tokenBusy==='issue'||!tokenName.trim()} onClick={()=>void issueApiToken()}><Plus/>{t('issueToken')}</button></div></div>{issuedToken&&<div className="issued-token" role="status"><div><ShieldCheck/><strong>{t('tokenShownOnce')}</strong><button className="icon-button" data-tooltip={t('copy')} aria-label={t('copy')} onClick={()=>void copyText(issuedToken)}><Copy/></button></div><code>{issuedToken}</code><button className="token-dismiss" onClick={()=>setIssuedToken('')}>{t('ready')}</button></div>}</>}
      <div className="api-token-list">{profile?.apiTokens.length===0&&<div className="token-empty"><ShieldOff/>{t('noActiveTokens')}</div>}{profile?.apiTokens.filter(token=>token.active).map(token=><article key={token.id}><span><b>{token.name}</b><code>ph_live_••••{token.displaySuffix}</code></span><small>{token.lastUsedAt?`${t('lastUsed')}: ${timeAgo(token.lastUsedAt)}`:t('neverUsed')}</small><em>{t('active')}</em><button className="icon-button danger" data-tooltip={t('revokeToken')} aria-label={t('revokeToken')} disabled={tokenBusy===token.id} onClick={()=>void revokeApiToken(token.id)}><Trash2/></button></article>)}</div>
      <section className="token-history"><div className="token-history-heading"><div><span className="kicker">API AUDIT</span><h3>{t('tokenRequestHistory')}</h3><p>{t('tokenHistoryHint')}</p></div><button className="icon-button" data-tooltip={t('retry')} aria-label={t('retry')} onClick={()=>void loadTokenHistory()}><RefreshCw/></button></div><div className="token-history-table"><div className="token-history-head"><span>{t('apiToken')}</span><span>{t('request')}</span><span>IP</span><span>{t('result')}</span><span>{t('checked')}</span></div>{tokenHistory?.items.length===0&&<div className="token-empty"><Clock3/>{t('noTokenRequests')}</div>}{tokenHistory?.items.map(item=><article key={item.id}><span><b>{item.token.name}</b><code>••••{item.token.displaySuffix}{item.token.revokedAt?` · ${t('revoked')}`:''}</code></span><span><b>{item.method} {item.path}</b><code>{item.query?`?${item.query}`:'—'}</code></span><code>{item.ipAddress}</code><span><b className={item.statusCode<400?'request-ok':'request-error'}>{item.statusCode}</b><small>{item.itemCount!==undefined?`${item.itemCount} ${t('items')}`:'—'} · {item.durationMs} ms</small></span><time>{formatDateTime(item.requestedAt)}</time></article>)}</div>{tokenHistory&&tokenHistory.total>0&&<ProxyPagination page={tokenHistoryPage} pageSize={10} total={tokenHistory.total} totalPages={Math.max(1,Math.ceil(tokenHistory.total/10))} onPageChange={setTokenHistoryPage} onPageSizeChange={()=>{}}/>}</section>
    </section>}
    {accountTab==='referrals'&&<section className="admin-card referral-account-card" role="tabpanel"><div className="card-heading"><div><span className="kicker">REFERRAL PROGRAM</span><h2>Приглашайте пользователей</h2><p>За регистрацию вы получаете 1 день. За покупки реферала: месяц — 1 день, квартал — 7 дней, полгода — 30 дней, год — 90 дней.</p></div><Workflow/></div><div className="referral-summary"><article><small>Приглашено</small><strong>{profile?.referral.invited??0} / {profile?.referral.maximum??10}</strong></article><article><small>Начислено</small><strong>{profile?.referral.rewardDays??0} дней</strong></article><article><small>Осталось мест</small><strong>{profile?.referral.remaining??0}</strong></article></div><div className="referral-channel-links"><article><span className="referral-channel-icon"><Globe2/></span><div><small>Регистрация на сайте</small><code>{profile?.referral.link??'—'}</code></div><button className="icon-button" type="button" data-tooltip="Скопировать ссылку для сайта" aria-label="Скопировать ссылку для сайта" disabled={!profile?.referral.link} onClick={()=>profile?.referral.link&&void copyText(profile.referral.link)}><Copy/></button></article><article className={!profile?.referral.telegramLink?'unavailable':''}><span className="referral-channel-icon"><Bot/></span><div><small>Переход через Telegram-бота</small><code>{profile?.referral.telegramLink??'Бот ещё не подключён'}</code></div><button className="icon-button" type="button" data-tooltip="Скопировать ссылку для Telegram" aria-label="Скопировать ссылку для Telegram" disabled={!profile?.referral.telegramLink} onClick={()=>profile?.referral.telegramLink&&void copyText(profile.referral.telegramLink)}><Copy/></button></article></div><div className="admin-data-table referral-table"><div className="admin-data-head"><span>Пользователь</span><span>Регистрация</span><span>Начисления</span></div>{referrals?.items.length===0&&<div className="empty-state"><Users/>По вашей ссылке пока никто не зарегистрировался.</div>}{referrals?.items.map(item=><article key={item.id}><span><b>{item.user.displayName||item.user.userName}</b><small>{item.user.email}</small></span><time>{formatDateTime(item.createdAt)}</time><span>{item.rewards.map(reward=><small key={reward.id}>+{reward.daysGranted} дн. · {reward.kind==='signup'?'регистрация':`${reward.durationDays??'—'} дней тарифа`} · {formatDateTime(reward.createdAt)}</small>)}</span></article>)}</div>{referrals&&referrals.total>0&&<ProxyPagination page={referralPage} pageSize={10} total={referrals.total} totalPages={Math.max(1,Math.ceil(referrals.total/10))} onPageChange={setReferralPage} onPageSizeChange={()=>{}}/>}</section>}
    {accountTab==='billing'&&<section className="admin-card billing-card" role="tabpanel"><div className="card-heading"><div><span className="kicker">BILLING</span><h2>{t('plansAndPayments')}</h2><p>{t('billingSecurityHint')}</p></div><CreditCard/></div>
      <div className="payment-products">{products.map(product=><article key={product.code} className={product.durationDays===30?'featured':''}>{product.durationDays===30&&<span className="tariff-popular">{t('popularChoice')}</span>}<div><span className="tariff-duration">{t('daysCount',{count:product.durationDays})}</span>{product.discountPercent>0&&<em className="tariff-discount">{t('discount',{percent:Math.round(product.discountPercent)})}</em>}<strong>{product.name}</strong><p>{product.description}</p><b>{money(product.amountMinor,product.currency)}</b>{product.savingsMinor>0&&<small className="tariff-saving"><s>{money(product.fullDailyPriceMinor,product.currency)}</s> · {t('savings',{amount:money(product.savingsMinor,product.currency)})}</small>}<small className="tariff-per-day">{t('perDay',{amount:money(Math.round(product.amountMinor/product.durationDays),product.currency)})}</small></div>{availableProviders.length>0&&<div className="payment-providers">{availableProviders.map(provider=><button key={provider.code} disabled={!!checkoutBusy} title={t('securePayment')} onClick={()=>void checkout(product.code,provider.code)}>{checkoutBusy===`${product.code}:${provider.code}`?t('openingPayment'):provider.name}</button>)}</div>}</article>)}</div>
      {!payments?.enabled&&<div className="billing-pending"><Clock3/>{t('billingPending')}</div>}
      {orders.length>0&&<div className="payment-history"><h3>{t('paymentHistory')}</h3>{orders.map(order=><div key={order.id}><span>{planLabel(order.plan)} · {providerLabel(order.provider)}<small>{order.paymentInstrument||order.paymentMethod}</small></span><b>{money(order.amountMinor,order.currency)}</b><em className={`payment-status ${order.status}`}>{paymentStatusLabel(order.status)}</em><time>{new Date(order.createdAt).toLocaleDateString(currentLocale())}</time></div>)}</div>}
    </section>}
  </section></main>
}

/** Полный административный реестр прокси с накопленной историей проверок. */
/** Отдельный VPN-каталог: безопасные endpoint-метаданные и управляемые лицензированные feed'ы. */
function AdminVpnPage() {
  const protocols: VpnProtocol[] = ['OpenVpn','WireGuard','Vless','Vmess','Trojan','Shadowsocks','Hysteria2','Tuic']
  const [tab,setTab] = useState<'endpoints'|'sources'>(() => new URLSearchParams(window.location.search).get('tab') === 'sources' ? 'sources' : 'endpoints')
  const [endpointData,setEndpointData] = useState<AdminVpnPage|null>(null)
  const [sources,setSources] = useState<VpnSource[]>([])
  const [page,setPage] = useState(1), [pageSize,setPageSize] = useState(10), [total,setTotal] = useState(0)
  const [protocol,setProtocol] = useState<VpnProtocol|'All'>('All')
  const [status,setStatus] = useState<VpnStatus|'All'>('Reachable')
  const [transport,setTransport] = useState<'All'|'tcp'|'udp'>('All')
  const [country,setCountry] = useState('')
  const [sort,setSort] = useState('lastChecked'), [order,setOrder] = useState('desc')
  const [search,setSearch] = useState(''), [searchDraft,setSearchDraft] = useState('')
  const [busy,setBusy] = useState(false), [error,setError] = useState('')
  const [editor,setEditor] = useState<VpnSource|null|false>(false)
  const [draft,setDraft] = useState({name:'',provider:'',url:'',protocol:'Vless' as VpnProtocol,priority:100,license:'Public repository',enabled:true})

  const load = useCallback(async () => {
    setBusy(true); setError('')
    try {
      const query = new URLSearchParams({page:String(page),pageSize:String(pageSize)})
      if (tab === 'endpoints') {
        if (protocol !== 'All') query.set('protocol',protocol)
        if (status !== 'All') query.set('status',status)
        if (transport !== 'All') query.set('transport',transport)
        if (country) query.set('country',country)
        if (search) query.set('query',search)
        query.set('sort',sort); query.set('order',order)
      }
      else if (search) query.set('search',search)
      const response = await fetch(`${API}/api/v1/admin/vpn/${tab}?${query}`,{credentials:'include'})
      if (!response.ok) throw new Error(await responseMessage(response,'Не удалось загрузить VPN-каталог'))
      const data = await response.json() as AdminVpnPage|PagedResult<VpnSource>
      if (tab === 'endpoints') { setEndpointData(data as AdminVpnPage); setSources([]) }
      else { setSources(data.items as VpnSource[]); setEndpointData(null) }
      setTotal(data.total)
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Не удалось загрузить VPN-каталог') }
    finally { setBusy(false) }
  },[page,pageSize,protocol,status,transport,country,sort,order,tab,search])
  useEffect(()=>{ queueMicrotask(()=>void load()) },[load])

  const selectTab = (next:'endpoints'|'sources') => { setTab(next); setPage(1); setSearch(''); setSearchDraft(''); history.replaceState(null,'',`/admin/vpn${next === 'sources' ? '?tab=sources' : ''}`) }
  const openEditor = (source?:VpnSource) => {
    setEditor(source ?? null)
    setDraft(source ? {name:source.name,provider:source.provider,url:source.url,protocol:source.defaultProtocol,priority:source.priority,license:source.license,enabled:source.enabled} : {name:'',provider:'',url:'',protocol:'Vless',priority:100,license:'Public repository',enabled:true})
  }
  const save = async (event:React.FormEvent) => {
    event.preventDefault(); setBusy(true); setError('')
    try {
      const sourceId = editor && typeof editor === 'object' ? editor.id : ''
      const response = await fetch(`${API}/api/v1/admin/vpn/sources${sourceId ? `/${sourceId}` : ''}`,{method:sourceId?'PUT':'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(draft)})
      if (!response.ok) throw new Error(await responseMessage(response,'Не удалось сохранить VPN-источник'))
      setEditor(false); await load()
    } catch(reason) { setError(reason instanceof Error ? reason.message : 'Не удалось сохранить VPN-источник'); setBusy(false) }
  }
  const run = async (action:'collect'|'validate') => {
    setBusy(true); setError('')
    try { const response=await fetch(`${API}/api/v1/admin/vpn/${action}`,{method:'POST',credentials:'include'}); if(!response.ok) throw new Error(await responseMessage(response,'Операция VPN не выполнена')); await load() }
    catch(reason){setError(reason instanceof Error?reason.message:'Операция VPN не выполнена');setBusy(false)}
  }
  const totalPages=Math.max(1,Math.ceil(total/pageSize))
  const summary=endpointData?.summary
  const countries=[['','Все страны'] as const,...(endpointData?.countries??[]).map(item=>[item.code,`${countryName(item.code)} · ${formatNumber(item.count)}`,<CountryFlag code={item.code} key={item.code}/>] as const)]
  const changeSort=(field:string,nextOrder:string)=>{setSort(field);setOrder(nextOrder);setPage(1)}
  const endpointStatusLabel=(value:VpnStatus)=>value==='Reachable'?'Рабочий':value==='Unreachable'?'Нерабочий':value==='UnsupportedTransport'?'Проверка транспорта недоступна':'Ожидает'
  return <section className="admin-section admin-wide-section" aria-labelledby="admin-vpn-title">
    <AdminPageHeader id="admin-vpn-title" title="VPN-каталог"><div className="vpn-header-actions"><button className="secondary-admin-button" disabled={busy} onClick={()=>void run(tab==='sources'?'collect':'validate')}><RefreshCw className={busy?'spin':''}/>{tab==='sources'?'Собрать сейчас':'Проверить сейчас'}</button>{tab==='sources'&&<button className="primary-admin-button" onClick={()=>openEditor()}><Plus/>Добавить feed</button>}</div></AdminPageHeader>
    <p className="admin-page-description">OpenVPN, WireGuard, VLESS, VMess и другие явно опубликованные конфигурации. Готовые URI сохраняются для копирования и API-выдачи.</p>
    <nav className="admin-tabs" aria-label="Раздел VPN"><button className={tab==='endpoints'?'active':''} onClick={()=>selectTab('endpoints')}>VPN-узлы</button><button className={tab==='sources'?'active':''} onClick={()=>selectTab('sources')}>Источники VPN</button></nav>
    <ToastSignal kind="error" message={error}/>
    {tab==='endpoints'&&<div className="admin-summary-grid compact-summary vpn-summary"><article><span className="summary-icon"><Radio/></span><div><small>Работают сейчас</small><strong>{formatNumber(summary?.reachable)}</strong><p>{formatNumber(summary?.pending)} ожидают проверки</p></div></article><article><span className="summary-icon"><ShieldCheck/></span><div><small>Работали хотя бы раз</small><strong>{formatNumber(summary?.everReachable)}</strong><p>из {formatNumber(summary?.total)} известных</p></div></article><article><span className="summary-icon"><Gauge/></span><div><small>Средняя задержка</small><strong>{summary?.averageReachableLatencyMs==null?'—':`${formatNumber(summary.averageReachableLatencyMs)} мс`}</strong><p>по рабочим VPN-узлам</p></div></article><article><span className="summary-icon"><Globe2/></span><div><small>География</small><strong>{formatNumber(summary?.countries)}</strong><p>стран · до {formatActiveDuration(summary?.longestKnownSeconds)} в каталоге</p></div></article></div>}
    <section className={`admin-card vpn-catalog-card${tab==='endpoints'?' vpn-inventory-card':' source-catalog-card vpn-source-catalog-card'}`}>
      {tab==='endpoints'?<><div className="vpn-admin-filters"><form onSubmit={event=>{event.preventDefault();setSearch(searchDraft.trim());setPage(1)}}><input aria-label="Поиск VPN" maxLength={128} placeholder="IP или имя VPN-узла" value={searchDraft} onChange={event=>setSearchDraft(event.target.value)}/><button type="submit">Найти</button></form><StyledSelect ariaLabel="Статус VPN" value={status} onChange={value=>{setStatus(value as VpnStatus|'All');setPage(1)}} options={([['All','Все состояния'],['Reachable','Рабочие'],['Pending','Ожидают проверки'],['Unreachable','Нерабочие'],['UnsupportedTransport','Проверка недоступна']] as const)}/><StyledSelect ariaLabel="VPN протокол" value={protocol} onChange={value=>{setProtocol(value as VpnProtocol|'All');setPage(1)}} options={['All',...protocols].map(value=>[value,value==='All'?'Все протоколы':value] as const)}/><StyledSelect ariaLabel="Транспорт VPN" value={transport} onChange={value=>{setTransport(value as 'All'|'tcp'|'udp');setPage(1)}} options={[['All','TCP и UDP'],['tcp','TCP'],['udp','UDP']]}/><StyledSelect ariaLabel="Страна VPN" value={country} onChange={value=>{setCountry(value);setPage(1)}} options={countries}/></div><div className="proxy-filter-summary"><span>Рабочие: <b>{formatNumber(summary?.reachable)}</b></span><span>Ожидают: <b>{formatNumber(summary?.pending)}</b></span><span>Нерабочие: <b>{formatNumber(summary?.unreachable)}</b></span><strong>Найдено по фильтру: {formatNumber(endpointData?.total)}</strong></div></>:<><div className="card-heading vpn-source-heading"><div><span className="kicker">ВСЕ VPN-ИСТОЧНИКИ</span><h2>Каталог <em>{formatNumber(total)}</em></h2><p>Провайдеры, протоколы и результат последнего сбора.</p></div><button className="icon-button" aria-label="Обновить VPN-источники" disabled={busy} onClick={()=>void load()}><RefreshCw className={busy?'spin':''}/></button></div><form className="source-search" role="search" onSubmit={event=>{event.preventDefault();setSearch(searchDraft.trim());setPage(1)}}><Search aria-hidden="true"/><input aria-label="Поиск VPN-источников" maxLength={200} type="search" placeholder="Название, провайдер или адрес feed" value={searchDraft} onChange={event=>setSearchDraft(event.target.value)}/>{searchDraft&&<button type="button" className="source-search-clear" aria-label="Очистить поиск VPN-источников" onClick={()=>{setSearchDraft('');setSearch('');setPage(1)}}><X/></button>}<button className="source-search-submit" disabled={busy}>Найти</button></form></>}
      {tab==='endpoints'?<div className="admin-data-table vpn-table vpn-admin-table"><div className="admin-table-head"><SortHeader field="address" label="Адрес / страна" sort={sort} order={order} onChange={changeSort}/><SortHeader field="protocol" label="Протокол" sort={sort} order={order} onChange={changeSort}/><SortHeader field="status" label="Состояние" sort={sort} order={order} onChange={changeSort}/><SortHeader field="quality" label="Качество" sort={sort} order={order} onChange={changeSort}/><SortHeader field="firstSeen" label="В каталоге" sort={sort} order={order} onChange={changeSort}/><SortHeader field="lastChecked" label="История" sort={sort} order={order} onChange={changeSort}/></div>{!endpointData&&busy?<div className="empty-state"><RefreshCw className="spin"/>Загружаем VPN…</div>:endpointData?.items.length===0?<div className="empty-state"><Radio/>По выбранным фильтрам VPN не найдены.</div>:endpointData?.items.map(item=><article key={item.id}><span className="admin-vpn-address"><code>{item.host}:{item.port}</code><small>{item.countryCode?<><CountryFlag code={item.countryCode}/>{countryName(item.countryCode)}</>:'Страна не определена'}</small></span><span><b className="vpn-protocol-badge">{item.protocol}</b><small>{item.transport.toUpperCase()}</small></span><span><em className={`proxy-state vpn-${item.status.toLowerCase()}`}>{endpointStatusLabel(item.status)}</em><small title={item.lastError}>{item.lastError||`Следующая проверка ${item.nextCheckAt?timeUntil(item.nextCheckAt):'по очереди'}`}</small></span><span><b>{item.latencyMs==null?'—':`${formatNumber(item.latencyMs)} мс`}</b><small>{item.successRate}% · {item.successfulChecks} успешных / {item.failedChecks} ошибок</small></span><span><b>{formatActiveDuration(item.knownForSeconds)}</b><small>с {formatDateTime(item.firstSeenAt)}</small></span><span className="vpn-history-cell"><span><b>{item.lastCheckedAt?timeAgo(item.lastCheckedAt):'Не проверялся'}</b><small>в feed {timeAgo(item.lastSeenAt)}</small></span>{item.connectionUri&&<button className="icon-button" aria-label="Копировать конфигурацию VPN" data-tooltip="Копировать полную конфигурацию" onClick={()=>void copyText(item.connectionUri!)}><Copy/></button>}</span></article>)}</div>:<div className="source-list vpn-source-list">{busy&&sources.length===0?<div className="source-search-empty"><RefreshCw className="spin"/>Загружаем VPN-источники…</div>:sources.length===0?<div className="source-search-empty"><Search/>По вашему запросу VPN-источники не найдены.</div>:sources.map(source=><article key={source.id}><div className="vpn-source-main"><b>{source.name}</b><small><span className="vpn-source-protocol">{source.defaultProtocol}</span><span>{formatNumber(source.lastItemCount)} адресов</span><span>{source.lastSucceededAt?`успешно собрано ${timeAgo(source.lastSucceededAt)}`:'успешных сборов ещё не было'}</span>{source.consecutiveFailures>0&&<span className="vpn-source-failures">сбоев подряд: {source.consecutiveFailures}</span>}</small><code title={source.url}>{source.url}</code></div><div className="source-controls"><span title={`${source.provider} · ${source.isBuiltIn?'встроенный':'пользовательский'} источник · ${source.license} · приоритет ${source.priority}`} className="source-kind">{source.isBuiltIn?source.provider:'свой'}</span><span title={source.lastError} className={source.lastError?'source-error':'source-ok'}>{source.lastError?'ошибка':source.enabled?'активен':'пауза'}</span><button className="source-edit-button" disabled={busy} onClick={()=>openEditor(source)}><Pencil/>Изменить</button></div></article>)}</div>}
      {tab==='endpoints'&&!busy&&total===0&&<p className="empty-state">Записей пока нет. Запустите сбор активных VPN-источников.</p>}
      {total>0&&<ProxyPagination page={page} pageSize={pageSize} total={total} totalPages={totalPages} onPageChange={next=>{setPage(next);document.getElementById('admin-vpn-title')?.scrollIntoView?.({behavior:'smooth'})}} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}
    </section>
    {editor!==false&&<div className="source-editor-backdrop" role="presentation" onMouseDown={event=>{if(event.target===event.currentTarget&&!busy)setEditor(false)}}><section className="source-editor-modal" role="dialog" aria-modal="true" aria-labelledby="vpn-source-editor-title"><div className="source-editor-heading"><div><span className="kicker">VPN FEED</span><h2 id="vpn-source-editor-title">{editor?'Изменить источник':'Добавить VPN-источник'}</h2><p>Добавляйте только явно опубликованные HTTPS-feed с разрешением на использование.</p></div><button className="icon-button" aria-label="Закрыть" onClick={()=>setEditor(false)}><X/></button></div><form className="source-editor-form" onSubmit={save}><div className="source-editor-grid"><label>Название<input required minLength={2} maxLength={120} disabled={!!editor?.isBuiltIn} value={draft.name} onChange={e=>setDraft({...draft,name:e.target.value})}/></label><label>Провайдер<input required minLength={2} maxLength={120} disabled={!!editor?.isBuiltIn} value={draft.provider} onChange={e=>setDraft({...draft,provider:e.target.value})}/></label></div><label>HTTPS URL<input required type="url" pattern="https://.*" disabled={!!editor?.isBuiltIn} value={draft.url} onChange={e=>setDraft({...draft,url:e.target.value})}/></label><div className="source-editor-grid"><label>Протокол<StyledSelect ariaLabel="Протокол VPN feed" disabled={!!editor?.isBuiltIn} value={draft.protocol} onChange={value=>setDraft({...draft,protocol:value as VpnProtocol})} options={protocols.map(value=>[value,value] as const)}/></label><label>Лицензия<input required maxLength={80} disabled={!!editor?.isBuiltIn} value={draft.license} onChange={e=>setDraft({...draft,license:e.target.value})}/></label></div><label className="source-enabled"><input className="ui-checkbox-input" type="checkbox" checked={draft.enabled} onChange={e=>setDraft({...draft,enabled:e.target.checked})}/><CheckboxMark/><span className="source-enabled-copy"><b>Источник активен</b><small>Будет участвовать в цикле сбора каждые 5 минут.</small></span></label><div className="source-editor-actions"><span/><button type="button" className="secondary-admin-button" onClick={()=>setEditor(false)}>Отмена</button><button className="primary-admin-button" disabled={busy}>{busy?'Сохраняем…':'Сохранить'}</button></div></form></section></div>}
  </section>
}

function AdminProxiesPage() {
  const [data,setData]=useState<AdminProxyPage|null>(null)
  const [page,setPage]=useState(1)
  const [pageSize,setPageSize]=useState(10)
  const [status,setStatus]=useState('Alive')
  const [protocol,setProtocol]=useState('')
  const [country,setCountry]=useState('')
  const [sort,setSort]=useState('lastChecked')
  const [search,setSearch]=useState('')
  const [appliedSearch,setAppliedSearch]=useState('')
  const [loading,setLoading]=useState(false)
  const [error,setError]=useState('')
  const load=useCallback(async(signal?:AbortSignal)=>{
    setLoading(true)
    const query=new URLSearchParams({page:String(page),pageSize:String(pageSize),sort})
    if(status)query.set('status',status);if(protocol)query.set('protocol',protocol);if(country)query.set('country',country);if(appliedSearch)query.set('query',appliedSearch)
    try{const response=await fetch(`${API}/api/v1/admin/proxies?${query}`,{credentials:'include',signal});if(!response.ok){setError(await responseMessage(response,'Реестр прокси недоступен'));return}setData(await response.json() as AdminProxyPage);setError('')}
    catch(reason){if(!isAbortError(reason))setError('Не удалось получить реестр прокси.')}
    finally{if(!signal?.aborted)setLoading(false)}
  },[page,pageSize,status,protocol,country,sort,appliedSearch])
  useEffect(()=>{const controller=new AbortController();const timer=window.setTimeout(()=>void load(controller.signal),0);return()=>{window.clearTimeout(timer);controller.abort()}},[load])
  const changeFilter=(setter:(value:string)=>void,value:string)=>{setter(value);setPage(1)}
  const summary=data?.summary
  const countries=[["","Все страны"] as const,...(data?.countries??[]).map(item=>[item.code,`${countryName(item.code)} · ${formatNumber(item.count)}`,<CountryFlag code={item.code} key={item.code}/>] as const)]
  return <section className="admin-section proxy-admin-section" aria-labelledby="admin-proxies-title">
    <AdminPageHeader id="admin-proxies-title" title="Прокси"><button className="icon-button" aria-label="Обновить реестр прокси" disabled={loading} onClick={()=>void load()}><RefreshCw className={loading?'spin':''}/></button></AdminPageHeader>
    <ToastSignal kind="error" message={error}/>
    <div className="admin-summary-grid compact-summary proxy-summary"><article><span className="summary-icon"><Activity/></span><div><small>Работают сейчас</small><strong>{formatNumber(summary?.freshAlive)}</strong><p>{formatNumber(summary?.staleAlive)} ожидают свежей проверки</p></div></article><article><span className="summary-icon"><ShieldCheck/></span><div><small>Работали хотя бы раз</small><strong>{formatNumber(summary?.everAlive)}</strong><p>из {formatNumber(summary?.total)} известных</p></div></article><article><span className="summary-icon"><Gauge/></span><div><small>Средняя задержка</small><strong>{summary?.averageAliveLatencyMs==null?'—':`${formatNumber(summary.averageAliveLatencyMs)} мс`}</strong><p>по свежим рабочим адресам</p></div></article><article><span className="summary-icon"><Clock3/></span><div><small>Самый долгоживущий</small><strong>{formatActiveDuration(summary?.longestActiveSeconds)}</strong><p>{formatNumber(summary?.countries)} стран выхода</p></div></article></div>
    <section className="admin-card admin-registry proxy-inventory-card">
      <div className="proxy-admin-filters"><form onSubmit={event=>{event.preventDefault();setAppliedSearch(search.trim());setPage(1)}}><input aria-label="Поиск прокси" maxLength={128} value={search} onChange={event=>setSearch(event.target.value)} placeholder="IP, хост или IP выхода"/><button type="submit">Найти</button></form><StyledSelect ariaLabel="Статус прокси" value={status} onChange={value=>changeFilter(setStatus,value)} options={[["","Все состояния"],["Alive","Рабочие"],["Pending","Ожидают проверки"],["Dead","Нерабочие"]]}/><StyledSelect ariaLabel="Протокол прокси" value={protocol} onChange={value=>changeFilter(setProtocol,value)} options={[["","Все протоколы"],...[...protocols].map(item=>[item,label(item)] as [string,string])]}/><StyledSelect ariaLabel="Страна прокси" value={country} onChange={value=>changeFilter(setCountry,value)} options={countries}/><StyledSelect ariaLabel="Сортировка прокси" value={sort} onChange={value=>changeFilter(setSort,value)} options={[["lastChecked","Недавно проверенные"],["active","Дольше работают"],["latency","Самые быстрые"],["lastSeen","Недавно найдены"]]}/></div>
      <div className="proxy-filter-summary"><span>Рабочие: <b>{formatNumber(summary?.alive)}</b></span><span>Ожидают: <b>{formatNumber(summary?.pending)}</b></span><span>Нерабочие: <b>{formatNumber(summary?.dead)}</b></span><strong>Найдено по фильтру: {formatNumber(data?.total)}</strong></div>
      <div className="admin-data-table admin-proxy-table"><div className="admin-data-head"><span>Адрес / страна</span><span>Состояние</span><span>Качество</span><span>Непрерывно работает</span><span>История</span></div>
        {!data||loading&&!data?<div className="empty-state"><RefreshCw className="spin"/>Загружаем прокси…</div>:data.items.length===0?<div className="empty-state"><Wifi/>По выбранным фильтрам прокси не найдены.</div>:data.items.map(item=><article key={item.id}>
          <span className="admin-proxy-address"><code>{item.host}:{item.port}</code><small><b className={`badge ${item.protocol.toLowerCase()}`}>{label(item.protocol)}</b>{item.countryCode?<><CountryFlag code={item.countryCode}/>{countryName(item.countryCode)}</>:'Страна не определена'}{item.exitIp&&item.exitIp!==item.host?` · выход ${item.exitIp}`:''}</small></span>
          <span><em className={`proxy-state ${item.status.toLowerCase()}`}>{adminProxyStatusLabel(item.status)}</em><small title={item.lastError}>{item.lastValidationDeferred?'Результат отложен':item.lastError||`Следующая проверка ${item.nextCheckAt?timeUntil(item.nextCheckAt):'по очереди'}`}</small></span>
          <span><b>{item.latencyMs==null?'—':`${item.latencyMs} мс`}</b><small>{item.successRate}% · {item.successfulChecks} успешных / {item.failedChecks} ошибок</small></span>
          <span><b>{item.status==='Alive'?formatActiveDuration(item.activeForSeconds):'Не работает'}</b><small>{item.currentAliveSince?`с ${new Date(item.currentAliveSince).toLocaleString('ru-RU')}`:item.lastAliveAt?`последний раз ${timeAgo(item.lastAliveAt)}`:'ещё не работал'}</small></span>
          <span><b>{item.lastCheckedAt?timeAgo(item.lastCheckedAt):'Не проверялся'}</b><small>впервые найден {new Date(item.firstSeenAt).toLocaleString('ru-RU')} · в feed {timeAgo(item.lastSeenAt)}</small></span>
        </article>)}</div>
      {data&&data.total>0&&<ProxyPagination page={page} pageSize={pageSize} total={data.total} totalPages={Math.max(1,Math.ceil(data.total/pageSize))} onPageChange={next=>{setPage(next);document.getElementById('admin-proxies-title')?.scrollIntoView?.({behavior:'smooth'})}} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}
    </section>
  </section>
}

/** Серверный реестр остаётся быстрым при сотнях тысяч аккаунтов. */
function AdminUsersPage() {
  const [data, setData] = useState<PagedResult<AdminUser> | null>(null)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState(10)
  const [searchDraft, setSearchDraft] = useState('')
  const [search, setSearch] = useState('')
  const [activityFilter, setActivityFilter] = useState('')
  const [planFilter, setPlanFilter] = useState('')
  const [editing, setEditing] = useState<AdminUser | null>(null)
  const [draft, setDraft] = useState<UserAccessDraft | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [referralData,setReferralData] = useState<AdminReferralPage|null>(null)
  const [referralPage,setReferralPage] = useState(1)

  const loadUsers = useCallback(async (signal?: AbortSignal) => {
    setLoading(true)
    const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
    if (search) query.set('search', search)
    if (activityFilter) query.set('activity', activityFilter)
    if (planFilter) query.set('plan', planFilter)
    try {
      const response = await fetch(`${API}/api/v1/admin/users?${query}`, { credentials: 'include', signal })
      if (!response.ok) { setError(await responseMessage(response, 'Пользователи недоступны')); return }
      setData(await response.json() as PagedResult<AdminUser>)
      setError('')
    } catch (reason) {
      if (!isAbortError(reason)) setError(reason instanceof Error ? reason.message : 'Пользователи недоступны')
    } finally {
      if (!signal?.aborted) setLoading(false)
    }
  }, [activityFilter, page, pageSize, planFilter, search])

  useEffect(() => {
    const controller = new AbortController()
    const timer = window.setTimeout(() => void loadUsers(controller.signal), 0)
    return () => { window.clearTimeout(timer); controller.abort() }
  }, [loadUsers])

  const loadReferrals = useCallback(async()=>{
    const response=await fetch(`${API}/api/v1/admin/referrals?page=${referralPage}&pageSize=10`,{credentials:'include'})
    if(!response.ok){setError(await responseMessage(response,'Реферальный журнал недоступен'));return}
    setReferralData(await response.json() as AdminReferralPage)
  },[referralPage])
  useEffect(()=>{const timer=window.setTimeout(()=>void loadReferrals(),0);return()=>window.clearTimeout(timer)},[loadReferrals])

  const openEditor = (user: AdminUser) => {
    setEditing(user)
    setDraft({
      isActive: user.isActive,
      administrator: user.roles.includes('Administrator'),
      subscriber: user.roles.includes('Subscriber'),
      plan: user.subscription?.plan ?? 'free',
      status: user.subscription?.status ?? 'active',
      expiresAt: user.subscription?.expiresAt?.slice(0, 10) ?? '',
    })
  }

  const save = async () => {
    if (!editing || !draft) return
    setBusy(true)
    setError('')
    const roles = ['User', ...(draft.subscriber ? ['Subscriber'] : []), ...(draft.administrator ? ['Administrator'] : [])]
    const response = await fetch(`${API}/api/v1/admin/users/${editing.id}`, {
      method: 'PUT', credentials: 'include', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        isActive: draft.isActive, roles, plan: draft.plan, status: draft.status,
        expiresAt: draft.expiresAt ? new Date(`${draft.expiresAt}T23:59:59Z`).toISOString() : null,
      }),
    })
    if (!response.ok) setError(await responseMessage(response, 'Не удалось обновить права'))
    else { setEditing(null); setDraft(null); await loadUsers() }
    setBusy(false)
  }

  const totalPages = Math.max(1, Math.ceil((data?.total ?? 0) / pageSize))
  return <section className="admin-section users-admin-section" aria-labelledby="admin-users-title">
    <AdminPageHeader id="admin-users-title" title="Пользователи"/>
    <ToastSignal kind="error" message={error}/>
    <section className="admin-card users-registry">
      <div className="user-toolbar">
        <form onSubmit={event => { event.preventDefault(); setSearch(searchDraft.trim()); setPage(1) }}>
          <Search/><input aria-label="Поиск пользователей" value={searchDraft} onChange={event => setSearchDraft(event.target.value)} placeholder="Имя, логин или почта…"/>
          {searchDraft && <button type="button" aria-label="Очистить поиск пользователей" onClick={() => { setSearchDraft(''); setSearch(''); setPage(1) }}><X/></button>}
          <button type="submit">Найти</button>
        </form>
        <label><span>Активность</span><StyledSelect ariaLabel="Фильтр активности" value={activityFilter} onChange={value => { setActivityFilter(value); setPage(1) }} options={[["","Все"],["active","Активные"],["disabled","Отключённые"]]} /></label>
        <label><span>Тариф</span><StyledSelect ariaLabel="Фильтр тарифа" value={planFilter} onChange={value => { setPlanFilter(value); setPage(1) }} options={[["","Все"],["free","Free"],["pro","Pro"],["unlimited","Unlimited"]]} /></label>
      </div>
      <div className="admin-data-table users-table">
        <div className="admin-data-head"><span>Пользователь</span><span>Доступ</span><span>Подписка</span><span>Последний вход</span><span/></div>
        {loading && !data ? <div className="empty-state"><RefreshCw className="spin"/>Загружаем пользователей…</div>
          : data?.items.length === 0 ? <div className="empty-state"><Users/>По заданным условиям пользователи не найдены.</div>
            : data?.items.map(user => <article key={user.id}>
              <span className="user-identity"><i><User/></i><span><b>{user.displayName || user.userName}</b><small>{user.email}</small><small>@{user.userName} · создан {new Date(user.createdAt).toLocaleDateString('ru-RU')}</small></span></span>
              <span className="user-role-cell"><em className={`state-pill ${user.isActive ? 'active' : ''}`}>{user.isActive ? 'Активен' : 'Отключён'}</em><small>{user.roles.includes('Administrator') ? 'Администратор' : user.roles.includes('Subscriber') ? 'Подписчик' : 'Пользователь'}</small></span>
              <span><b>{planLabel(user.subscription?.plan)}</b><small>{subscriptionStatusLabel(user.subscription?.status ?? 'active')}{user.subscription?.expiresAt ? ` · до ${new Date(user.subscription.expiresAt).toLocaleDateString('ru-RU')}` : ' · бессрочно'}</small></span>
              <time>{user.lastLoginAt ? timeAgo(user.lastLoginAt) : 'Ещё не входил'}</time>
              <button className="table-action" onClick={() => openEditor(user)}><Pencil/>Управлять</button>
            </article>)}
      </div>
      {data && data.total > 0 && <ProxyPagination
        page={page} pageSize={pageSize} total={data.total} totalPages={totalPages}
        onPageChange={setPage}
        onPageSizeChange={size => { setPageSize(size); setPage(1) }}
      />}
    </section>
    <section className="admin-card users-registry admin-referral-registry"><div className="card-heading"><div><span className="kicker">REFERRAL AUDIT</span><h2>Реферальная программа</h2><p>Кто кого пригласил и сколько дней подписки было начислено.</p></div><Workflow/></div><div className="referral-summary"><article><small>Регистраций</small><strong>{referralData?.summary.referrals??'—'}</strong></article><article><small>Начислено дней</small><strong>{referralData?.summary.rewardDays??'—'}</strong></article><article><small>Бонусов за покупки</small><strong>{referralData?.summary.purchaseRewards??'—'}</strong></article></div><div className="admin-data-table admin-referral-table"><div className="admin-data-head"><span>Пригласил</span><span>Новый пользователь</span><span>Дата</span><span>Начислено</span></div>{referralData?.items.length===0&&<div className="empty-state"><Workflow/>Реферальных регистраций пока нет.</div>}{referralData?.items.map(item=><article key={item.id}><span><b>{item.referrer.displayName||item.referrer.userName}</b><small>{item.referrer.email}</small></span><span><b>{item.referred.displayName||item.referred.userName}</b><small>{item.referred.email}</small></span><time>{formatDateTime(item.createdAt)}</time><span><b>+{item.rewardDays} дн.</b>{item.rewards.map(reward=><small key={reward.id}>{reward.kind==='signup'?'Регистрация':`Оплата тарифа на ${reward.durationDays??'—'} дней`}: +{reward.daysGranted} дн. · {formatDateTime(reward.createdAt)}</small>)}</span></article>)}</div>{referralData&&referralData.total>0&&<ProxyPagination page={referralPage} pageSize={10} total={referralData.total} totalPages={Math.max(1,Math.ceil(referralData.total/10))} onPageChange={setReferralPage} onPageSizeChange={()=>{}}/>}</section>
    {editing && draft && <div className="source-editor-backdrop" role="presentation" onMouseDown={event => { if (event.target === event.currentTarget && !busy) { setEditing(null); setDraft(null) } }}>
      <section className="source-editor-modal user-editor-modal" role="dialog" aria-modal="true" aria-label={`Управление пользователем ${editing.displayName || editing.userName}`}>
        <div className="source-editor-heading"><div><span className="kicker">USER ACCESS</span><h2>{editing.displayName || editing.userName}</h2><p>{editing.email}. Изменение ролей завершит активные сессии пользователя.</p></div><button className="icon-button" aria-label="Закрыть управление пользователем" disabled={busy} onClick={() => { setEditing(null); setDraft(null) }}><X/></button></div>
        <div className="user-editor-switches"><Toggle checked={draft.isActive} onChange={isActive => setDraft({...draft,isActive})} label="Аккаунт активен"/><Toggle checked={draft.subscriber} onChange={subscriber => setDraft({...draft,subscriber})} label="Роль подписчика"/><Toggle checked={draft.administrator} onChange={administrator => setDraft({...draft,administrator})} label="Права администратора"/></div>
        <div className="source-editor-grid"><label>Тариф<StyledSelect ariaLabel="Тариф пользователя" value={draft.plan} onChange={plan => setDraft({...draft,plan})} options={[["free","Free"],["pro","Pro"],["unlimited","Unlimited"]]}/></label><label>Статус подписки<StyledSelect ariaLabel="Статус подписки пользователя" value={draft.status} onChange={status => setDraft({...draft,status})} options={[["active","Активна"],["trialing","Пробная"],["past_due","Просрочена"],["canceled","Отменена"],["expired","Истекла"],["suspended","Приостановлена"]]}/></label><label>Действует до<input type="date" value={draft.expiresAt} onChange={event => setDraft({...draft,expiresAt:event.target.value})}/></label></div>
        <div className="source-editor-actions"><span/><button className="secondary-admin-button" disabled={busy} onClick={() => { setEditing(null); setDraft(null) }}>Отмена</button><button className="primary-admin-button" disabled={busy} onClick={() => void save()}><ShieldCheck/>{busy ? 'Сохраняем…' : 'Сохранить доступ'}</button></div>
      </section>
    </div>}
  </section>
}

/** Управление Telegram Bot API, Stars и встроенной CRM из отдельного раздела панели. */
function AdminTelegramPage(){
  const [tab,setTab]=useState<'overview'|'settings'|'proxies'|'chats'>('overview')
  const [settings,setSettings]=useState<TelegramSettings|null>(null)
  const [draft,setDraft]=useState<TelegramSettings|null>(null)
  const [products,setProducts]=useState<AdminPaymentProduct[]>([])
  const [tokenValue,setTokenValue]=useState('')
  const [chats,setChats]=useState<PagedResult<TelegramChat>|null>(null)
  const [page,setPage]=useState(1);const [pageSize,setPageSize]=useState(10);const [query,setQuery]=useState('')
  const [selected,setSelected]=useState<TelegramChat|null>(null)
  const [messages,setMessages]=useState<TelegramMessage[]>([])
  const addTelegramProxy=()=>setDraft(current=>current?{...current,proxies:[...current.proxies,{id:crypto.randomUUID(),host:'',port:1080,username:'',password:'',passwordConfigured:false}]}:current)
  const updateTelegramProxy=(id:string,patch:Partial<TelegramProxy>)=>setDraft(current=>current?{...current,proxies:current.proxies.map(proxy=>proxy.id===id?{...proxy,...patch}:proxy)}:current)
  const removeTelegramProxy=(id:string)=>setDraft(current=>current?{...current,proxies:current.proxies.filter(proxy=>proxy.id!==id)}:current)
  const [message,setMessage]=useState('');const [broadcast,setBroadcast]=useState('')
  const [busy,setBusy]=useState('');const [error,setError]=useState('');const [notice,setNotice]=useState('')
  const load=useCallback(async()=>{try{const [configResponse,paymentResponse]=await Promise.all([fetch(`${API}/api/v1/admin/telegram`,{credentials:'include'}),fetch(`${API}/api/v1/admin/payments`,{credentials:'include'})]);if(!configResponse.ok)throw new Error(await responseMessage(configResponse,'Настройки Telegram недоступны'));const value=await configResponse.json() as Partial<TelegramSettings>;const config={...value,transportMode:value.transportMode??'auto',proxies:value.proxies??[],automaticProductCodes:value.automaticProductCodes??[],rublesPerStar:value.rublesPerStar??1.68,starsRoundingStep:value.starsRoundingStep??5,effectiveProductStars:value.effectiveProductStars??value.productStars??{},avatarUrl:value.avatarUrl??'/api/v1/admin/telegram/avatar'} as TelegramSettings;setSettings(config);setDraft(config);if(paymentResponse.ok){const payment=await paymentResponse.json() as AdminPaymentSettings;setProducts(payment.products.filter(x=>x.enabled))}setError('')}catch(reason){setError(reason instanceof Error?reason.message:'Настройки Telegram недоступны')}},[])
  const loadChats=useCallback(async()=>{const params=new URLSearchParams({page:String(page),pageSize:String(pageSize)});if(query.trim())params.set('query',query.trim());const response=await fetch(`${API}/api/v1/admin/telegram/chats?${params}`,{credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Диалоги Telegram недоступны'));return}setChats(await response.json() as PagedResult<TelegramChat>)},[page,pageSize,query])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load]);useEffect(()=>{if(tab!=='chats')return;const timer=window.setTimeout(()=>void loadChats(),0);return()=>window.clearTimeout(timer)},[tab,loadChats])
  const save=async()=>{if(!draft)return;setBusy('save');setNotice('');const response=await fetch(`${API}/api/v1/admin/telegram`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:draft.enabled,updateMode:draft.updateMode,transportMode:draft.transportMode,proxies:draft.proxies.map(proxy=>({id:proxy.id,host:proxy.host,port:proxy.port,username:proxy.username,password:proxy.password?.trim()||null})),name:draft.name,description:draft.description,shortDescription:draft.shortDescription,supportText:draft.supportText,proxyFileMaxItems:draft.proxyFileMaxItems,webhookMaxConnections:draft.webhookMaxConnections,productStars:draft.productStars,automaticProductCodes:draft.automaticProductCodes,rublesPerStar:draft.rublesPerStar,starsRoundingStep:draft.starsRoundingStep,botToken:tokenValue.trim()||null})});if(!response.ok)setError(await responseMessage(response,'Telegram-бот не сохранён'));else{const value=await response.json() as TelegramSettings;setSettings(value);setDraft({...value,proxies:value.proxies??[]});setTokenValue('');setError('');setNotice('Профиль, команды, изображение, цены Stars и режим доставки настроены автоматически.')}setBusy('')}
  const provision=async()=>{setBusy('provision');const response=await fetch(`${API}/api/v1/admin/telegram/provision`,{method:'POST',credentials:'include'});if(!response.ok)setError(await responseMessage(response,'Повторная настройка не выполнена'));else{await load();setNotice('Настройки Telegram применены повторно.')}setBusy('')}
  const openChat=async(chat:TelegramChat)=>{setSelected(chat);setMessages([]);const response=await fetch(`${API}/api/v1/admin/telegram/chats/${chat.id}/messages?take=100`,{credentials:'include'});if(response.ok)setMessages(await response.json() as TelegramMessage[])}
  const send=async(isBroadcast:boolean)=>{const text=isBroadcast?broadcast:message;if(!text.trim())return;setBusy(isBroadcast?'broadcast':'message');const response=await fetch(`${API}/api/v1/admin/telegram/messages`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({chatId:isBroadcast?null:selected?.id,broadcast:isBroadcast,text})});if(!response.ok)setError(await responseMessage(response,'Сообщение не поставлено в очередь'));else{if(isBroadcast){setBroadcast('');setNotice('Рассылка поставлена в безопасную очередь с соблюдением лимитов Telegram.')}else{setMessage('');if(selected)await openChat(selected)}}setBusy('')}
  const updateChat=async(chat:TelegramChat,patch:Partial<TelegramChat>)=>{const next={...chat,...patch};const response=await fetch(`${API}/api/v1/admin/telegram/chats/${chat.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({notificationsEnabled:next.notificationsEnabled,isBlocked:next.isBlocked})});if(response.ok){setSelected(next);await loadChats()}else setError(await responseMessage(response,'Состояние чата не обновлено'))}
  const automaticStars=(product:AdminPaymentProduct)=>{if(!draft||draft.rublesPerStar<=0)return 0;const raw=product.amountMinor/100/draft.rublesPerStar;return Math.ceil(raw/draft.starsRoundingStep)*draft.starsRoundingStep}
  const setAutomatic=(product:AdminPaymentProduct,automatic:boolean)=>{if(!draft)return;const codes=automatic?[...new Set([...draft.automaticProductCodes,product.code])]:draft.automaticProductCodes.filter(code=>code!==product.code);const prices={...draft.productStars};if(!automatic&&!prices[product.code])prices[product.code]=automaticStars(product);setDraft({...draft,automaticProductCodes:codes,productStars:prices})}
  const stats=settings?.stats
  return <section className="admin-section telegram-admin" aria-labelledby="admin-telegram-title">
    <AdminPageHeader id="admin-telegram-title" title="Telegram-бот"><span className={`telegram-health ${settings?.enabled?'online':''}`}><Radio/>{settings?.enabled?'Работает':'Отключён'}</span></AdminPageHeader>
    <ToastSignal kind="error" message={error}/><ToastSignal kind="success" message={notice}/>
    <AdminTabs value={tab} onChange={value=>setTab(value as typeof tab)} items={[["overview","Обзор"],["settings","Настройки"],["proxies","Прокси Telegram"],["chats","CRM и сообщения"]]}/>
    {tab==='proxies'&&draft&&<section className="admin-card telegram-proxy-card"><div className="card-heading"><div><span className="kicker">TELEGRAM NETWORK</span><h2>Маршруты SOCKS5</h2><p>Запросы выполняются по списку сверху вниз. Пароли зашифрованы на сервере и обратно в браузер не выдаются.</p></div><button className="secondary-admin-button" type="button" disabled={draft.proxies.length>=10} onClick={addTelegramProxy}><Plus/>Добавить прокси</button></div><label className="telegram-transport-mode">Политика подключения<StyledSelect value={draft.transportMode} onChange={value=>setDraft({...draft,transportMode:value as TelegramSettings['transportMode']})} options={[["auto","Прокси → прямой резерв"],["proxy","Только SOCKS5-прокси"],["direct","Только прямое соединение"]]}/></label><div className="telegram-proxy-list">{draft.proxies.length===0?<div className="empty-state"><Network/>Прокси не настроены. Для VPS с недоступным Telegram добавьте хотя бы один SOCKS5-маршрут.</div>:draft.proxies.map((proxy,index)=><article key={proxy.id}><span className="proxy-order">{index+1}</span><label>Хост<input value={proxy.host} placeholder="203.0.113.10" onChange={event=>updateTelegramProxy(proxy.id,{host:event.target.value})}/></label><label>Порт<input type="number" min={1} max={65535} value={proxy.port} onChange={event=>updateTelegramProxy(proxy.id,{port:Number(event.target.value)})}/></label><label>Логин<input autoComplete="off" value={proxy.username} onChange={event=>updateTelegramProxy(proxy.id,{username:event.target.value})}/></label><label>Пароль<input type="password" autoComplete="new-password" value={proxy.password??''} placeholder={proxy.passwordConfigured?'Сохранён — оставьте пустым':'Введите пароль'} onChange={event=>updateTelegramProxy(proxy.id,{password:event.target.value})}/></label><button className="icon-button danger" type="button" data-tooltip="Удалить маршрут" aria-label={`Удалить SOCKS5-прокси ${index+1}`} onClick={()=>removeTelegramProxy(proxy.id)}><Trash2/></button></article>)}</div><p className="telegram-proxy-note"><ShieldCheck/>Сохранение сразу проверит Bot API через настроенные маршруты. Секреты не попадут в Git, логи или ответы API.</p><div className="telegram-proxy-actions"><button className="primary-admin-button" type="button" disabled={!!busy} onClick={()=>void save()}><ShieldCheck/>{busy==='save'?'Проверяем и сохраняем…':'Сохранить маршруты'}</button></div></section>}
    {tab==='overview'&&<><div className="admin-summary-grid telegram-summary"><article><span className="summary-icon"><Users/></span><div><small>Пользователи</small><strong>{stats?.users??'—'}</strong><p>{stats?.activeUsers30d??'—'} активны за 30 дней</p></div></article><article><span className="summary-icon"><Star/></span><div><small>Выручка Stars</small><strong>{stats?.starsRevenue??'—'} ⭐</strong><p>{stats?.paidOrders??'—'} успешных оплат</p></div></article><article><span className="summary-icon"><Bell/></span><div><small>Согласия на рекламу</small><strong>{stats?.marketingConsents??'—'}</strong><p>{stats?.notificationsEnabled??'—'} получают сервисные сообщения</p></div></article><article><span className="summary-icon"><Send/></span><div><small>Очередь</small><strong>{stats?.queued??'—'}</strong><p>{stats?.failed??'—'} требуют внимания · {stats?.blocked??'—'} остановили бота</p></div></article></div><section className="admin-card telegram-status-card"><div><span className="kicker">ПОДКЛЮЧЕНИЕ</span><h2>{settings?.botUsername?`@${settings.botUsername}`:'Бот ещё не подключён'}</h2><p>{settings?.tokenConfigured?'Token надёжно зашифрован и не передаётся в браузер.':'Откройте настройки и укажите token из BotFather.'}</p></div><dl><div><dt>Доставка</dt><dd>{settings?.updateMode==='webhook'?'Webhook':'Long polling'}</dd></div><div><dt>Webhook</dt><dd>{settings?.webhookUrl||'—'}</dd></div><div><dt>Последняя настройка</dt><dd>{settings?.provisionedAt?new Date(settings.provisionedAt).toLocaleString('ru-RU'):'—'}</dd></div></dl><button className="secondary-admin-button" disabled={!settings?.tokenConfigured||!!busy} onClick={()=>void provision()}><RefreshCw className={busy==='provision'?'spin':''}/>Применить заново</button></section></>}
    {tab==='settings'&&draft&&<section className="admin-card telegram-settings-card"><div className="card-heading"><div><span className="kicker">BOT API</span><h2>Профиль и автоматизация</h2></div><Toggle checked={draft.enabled} onChange={enabled=>setDraft({...draft,enabled})} label={draft.enabled?'Бот включён':'Бот выключен'}/></div><div className="telegram-profile-media"><img src={`${API}${draft.avatarUrl}`} alt="Аватар Telegram-бота ProxyHarbor"/><div><span className="kicker">ИЗОБРАЖЕНИЕ ПРОФИЛЯ</span><h3>Встроенный аватар ProxyHarbor</h3><p>Это изображение автоматически загружается в профиль бота при сохранении и при команде «Применить заново».</p><small>{draft.provisionedAt?`Последнее успешное применение: ${new Date(draft.provisionedAt).toLocaleString('ru-RU')}`:'Будет применено после первого сохранения настроек.'}</small></div><em className={draft.provisionedAt?'applied':''}>{draft.provisionedAt?'Применено':'Ожидает'}</em></div><div className="telegram-form"><label>Token BotFather<input type="password" autoComplete="new-password" value={tokenValue} placeholder={draft.tokenConfigured?'Сохранён — оставьте пустым':'123456:ABC…'} onChange={event=>setTokenValue(event.target.value)}/><small>После сохранения система проверит token и сама установит имя, описание, аватар, команды и webhook.</small></label><label>Режим доставки<StyledSelect value={draft.updateMode} onChange={value=>setDraft({...draft,updateMode:value as 'webhook'|'polling'})} options={[["webhook","Webhook — рекомендуется"],["polling","Long polling"]]}/></label><label>Имя<input minLength={2} maxLength={64} value={draft.name} onChange={event=>setDraft({...draft,name:event.target.value})}/></label><label>Короткое описание<input minLength={5} maxLength={120} value={draft.shortDescription} onChange={event=>setDraft({...draft,shortDescription:event.target.value})}/></label><label className="wide">Полное описание<textarea minLength={10} maxLength={512} value={draft.description} onChange={event=>setDraft({...draft,description:event.target.value})}/></label><label className="wide">Ответ поддержки<textarea minLength={5} maxLength={1000} value={draft.supportText} onChange={event=>setDraft({...draft,supportText:event.target.value})}/></label><label>Прокси в TXT-файле<input type="number" min={1} max={10000} value={draft.proxyFileMaxItems} onChange={event=>setDraft({...draft,proxyFileMaxItems:Number(event.target.value)})}/></label><label>Webhook connections<input type="number" min={1} max={100} value={draft.webhookMaxConnections} onChange={event=>setDraft({...draft,webhookMaxConnections:Number(event.target.value)})}/></label></div><div className="telegram-stars"><div className="telegram-stars-heading"><div><h3>Цены Telegram Stars</h3><p>Автоматическая цена следует за стоимостью тарифа из раздела «Оплата». Ручной режим сохраняет указанное число Stars.</p></div><div className="stars-formula-controls"><label>Цена 1 Star, ₽<input type="number" min={0.01} max={1000} step={0.01} value={draft.rublesPerStar} onChange={event=>setDraft({...draft,rublesPerStar:Number(event.target.value)})}/></label><label>Округлять вверх до<input type="number" min={1} max={1000} value={draft.starsRoundingStep} onChange={event=>setDraft({...draft,starsRoundingStep:Number(event.target.value)})}/></label></div></div><div className="stars-explanation"><Star/><p><b>Как считается:</b> цена тарифа делится на ориентировочную стоимость одной Star, затем результат округляется вверх. По курсу 1,68 ₽/Star тарифы 99 ₽, 350 ₽, 690 ₽ и 1 890 ₽ стоят 60, 210, 415 и 1 125 Stars. Telegram может менять итоговую цену пополнения из-за платформы, налогов и региона; неиспользованные Stars остаются на балансе пользователя.</p></div>{products.length===0?<p>Сначала включите тарифы в разделе «Оплата».</p>:products.map(product=>{const automatic=draft.automaticProductCodes.includes(product.code);const calculated=automaticStars(product);return <article className="telegram-star-product" key={product.code}><span><b>{product.name}</b><small>{product.durationDays} дней · {(product.amountMinor/100).toLocaleString('ru-RU')} {product.currency} · {product.code}</small></span><div className="star-price-mode" role="group" aria-label={`Режим цены ${product.name}`}><button className={automatic?'active':''} onClick={()=>setAutomatic(product,true)}>Авто</button><button className={!automatic?'active':''} onClick={()=>setAutomatic(product,false)}>Вручную</button></div><label><span className="sr-only">Цена {product.name} в Stars</span><input type="number" min={1} max={1000000} disabled={automatic} value={automatic?calculated:(draft.productStars[product.code]??'')} onChange={event=>{const value=Number(event.target.value);const next={...draft.productStars};if(value>0)next[product.code]=value;else delete next[product.code];setDraft({...draft,productStars:next})}}/></label><small className="star-calculation">{automatic?`${(product.amountMinor/100).toLocaleString('ru-RU')} ${product.currency} ÷ ${draft.rublesPerStar} ₽ = ${calculated} ⭐ после округления`:'Фиксированная цена не изменится вместе с тарифом.'}</small></article>})}</div><div className="telegram-save"><button className="primary-admin-button" disabled={!!busy} onClick={()=>void save()}><Bot/>{busy==='save'?'Настраиваем…':'Сохранить и настроить'}</button></div></section>}
    {tab==='chats'&&<><section className="admin-card telegram-broadcast"><div><span className="kicker">BROADCAST</span><h2>Рекламная рассылка</h2><p>{settings?.marketingBroadcastsEnabled?'Получат только пользователи с действующим отдельным согласием на акции и предложения; отправка идёт через очередь.':'Отключена deploy-политикой до правовой квалификации сервиса по требованиям РФ. Служебные ответы клиентам остаются доступны.'}</p></div><textarea maxLength={4096} disabled={!settings?.marketingBroadcastsEnabled} value={broadcast} placeholder={settings?.marketingBroadcastsEnabled?'Текст рекламной рассылки…':'Рекламные рассылки заблокированы'} onChange={event=>setBroadcast(event.target.value)}/><button className="primary-admin-button" disabled={!settings?.marketingBroadcastsEnabled||!broadcast.trim()||!!busy} onClick={()=>void send(true)}><Send/>{busy==='broadcast'?'Ставим в очередь…':settings?.marketingBroadcastsEnabled?'Отправить согласившимся':'Рассылка отключена'}</button></section><section className="admin-card telegram-chat-list"><div className="card-heading"><div><span className="kicker">CRM</span><h2>Диалоги <em>{chats?.total??0}</em></h2></div><form onSubmit={event=>{event.preventDefault();setPage(1);void loadChats()}}><input value={query} placeholder="Имя, username или ID" onChange={event=>setQuery(event.target.value)}/><button className="icon-button" aria-label="Найти"><RefreshCw/></button></form></div>{chats?.items.map(chat=><button key={chat.id} className="telegram-chat-row" onClick={()=>void openChat(chat)}><span className="summary-icon"><MessageCircle/></span><span><b>{chat.displayName||chat.username||chat.chatId}</b><small>{chat.username?`@${chat.username} · `:''}{chat.messages} сообщений · {chat.subscription.plan}</small></span><em className={chat.isBlocked?'blocked':'active'}>{chat.isBlocked?'заблокирован':chat.marketingNotificationsEnabled?'реклама разрешена':chat.notificationsEnabled?'только сервисные':'без уведомлений'}</em><time>{new Date(chat.lastInteractionAt).toLocaleString('ru-RU')}</time></button>)}{chats&&chats.total>0&&<ProxyPagination page={page} pageSize={pageSize} total={chats.total} totalPages={Math.ceil(chats.total/pageSize)} onPageChange={setPage} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}</section></>}
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
  const updateProduct=(code:string,patch:Partial<AdminPaymentProduct>)=>setSettings(current=>{
    if(!current)return current
    const patched=current.products.map(product=>product.code===code?{...product,...patch}:product)
    const daily=patched.find(product=>product.durationDays===1)?.amountMinor??0
    const products=patched.map(product=>{const amountMinor=product.durationDays===1?daily:calculateSubscriptionPrice(daily,product.durationDays,product.discountPercent);return{...product,amountMinor,fullDailyPriceMinor:daily*product.durationDays,savingsMinor:Math.max(0,daily*product.durationDays-amountMinor)}})
    return {...current,products}
  })
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
    <AdminPageHeader id="admin-payments-title" title="Оплата"><button className="primary-admin-button" disabled={!settings||busy} onClick={()=>void save()}><ShieldCheck/>{busy?'Сохраняем…':'Сохранить изменения'}</button></AdminPageHeader>
    <ToastSignal kind="error" message={error}/><ToastSignal kind="success" message={notice}/>
    {!settings?<div className="admin-card payment-settings-loading"><RefreshCw className="spin"/>Загружаем настройки…</div>:<>
      <AdminTabs value={tab} onChange={value=>setTab(value as typeof tab)} items={[["overview","Обзор"],["providers","Провайдеры"],["invoices","Счета"],["tariffs","Тарифы"]]}/>
      {tab==='overview'&&<><section className="admin-card billing-master-switch"><div><span className="kicker">ГЛОБАЛЬНЫЙ СТАТУС</span><h2>Приём платежей</h2><p>Включайте после настройки хотя бы одного шлюза и проверки юридических данных.</p></div><Toggle checked={settings.enabled} onChange={checked=>setSettings({...settings,enabled:checked})} label={settings.enabled?'Включён':'Выключен'}/></section><div className="billing-overview-grid"><article className="admin-card"><Receipt/><small>Счетов в реестре</small><strong>{invoices?.total??'—'}</strong><button onClick={()=>setTab('invoices')}>Открыть <ArrowRight/></button></article><article className="admin-card"><CreditCard/><small>Готовых шлюзов</small><strong>{providers.filter(x=>x.ready).length} / {providers.length}</strong><button onClick={()=>setTab('providers')}>Настроить <ArrowRight/></button></article><article className="admin-card"><CalendarClock/><small>Активных тарифов</small><strong>{settings.products.filter(x=>x.enabled).length}</strong><button onClick={()=>setTab('tariffs')}>Изменить <ArrowRight/></button></article></div></>}
      {tab==='tariffs'&&<section className="admin-card payment-products-settings"><div className="card-heading"><div><span className="kicker">ТАРИФЫ</span><h2>Сроки и ценовая политика</h2><p>Базовая цена — 99 ₽ за день. Для длительных периодов действует прогрессивная скидка: 350 ₽ за неделю, 690 ₽ за месяц и 1 890 ₽ за квартал. Telegram Stars пересчитываются автоматически.</p></div></div><div>{[...settings.products].sort((a,b)=>a.durationDays-b.durationDays).map(product=><article key={product.code}><Toggle checked={product.enabled} onChange={checked=>updateProduct(product.code,{enabled:checked})} label="Доступен"/><label>Период<input disabled value={subscriptionPeriodName(product.durationDays)}/></label><label>Цена, ₽<input type="number" disabled={product.durationDays!==1} min="0.01" max="10000000" step="0.01" value={(product.amountMinor/100).toString()} onChange={event=>updateProduct(product.code,{amountMinor:Math.round(Number(event.target.value)*100)})}/></label><label>Скидка, %<input type="number" min="0" max="99" step="0.01" disabled={product.durationDays===1||product.durationDays===365} value={product.discountPercent} onChange={event=>updateProduct(product.code,{discountPercent:Number(event.target.value)})}/></label><div className="tariff-policy-summary"><b>{product.durationDays===1?'Базовая цена':money(product.amountMinor,product.currency)}</b>{product.savingsMinor>0&&<small><s>{money(product.fullDailyPriceMinor,product.currency)}</s> · экономия {money(product.savingsMinor,product.currency)}</small>}</div><label className="payment-description">Описание<input maxLength={300} value={product.description} onChange={event=>updateProduct(product.code,{description:event.target.value})}/></label></article>)}</div></section>}
      {tab==='providers'&&<ProviderCards providers={providers} onOpen={code=>{setInvoices(null);setProviderOpen(code)}}/>}
      {tab==='invoices'&&<InvoiceRegistry data={invoices} status={invoiceStatus} onStatus={value=>{setInvoiceStatus(value);setInvoicePage(1)}} page={invoicePage} onPage={setInvoicePage}/>}
      {providerOpen&&<ProviderDialog provider={providers.find(x=>x.code===providerOpen)!} invoices={invoices} busy={busy} onClose={()=>setProviderOpen(null)} onUpdate={updateProvider} onSave={()=>void save()}/>}
    </>}
  </section>
}

function needsMerchantId(code:string){return !['stripe','cloudpayments','nowpayments'].includes(code)}
function merchantFieldLabel(code:string){return code==='yookassa'?'Shop ID':code==='yoomoney'?'Номер кошелька':code==='robokassa'?'Merchant Login':code==='tbank'?'Terminal Key':code==='cryptomus'?'Merchant UUID':'Merchant ID'}
function primarySecretLabel(code:string){return code==='yookassa'?'Secret Key':code==='yoomoney'?'Секрет HTTP-уведомлений':code==='cloudpayments'?'API Secret':code==='robokassa'?'Пароль №1':code==='tbank'?'Пароль терминала':code==='cryptomus'?'Payment API key':code==='nowpayments'?'API key':'Secret Key'}
function secondarySecretLabel(code:string){return code==='robokassa'?'Пароль №2':code==='nowpayments'?'IPN secret':'Webhook Secret'}
function needsSecondarySecret(code:string){return code==='robokassa'||code==='stripe'||code==='nowpayments'}

/** Доступный переключатель вместо платформенно-зависимого checkbox. */
/** Настройки backup живут в PostgreSQL и применяются worker без рестарта контейнера. */
function AdminBackupSettings({onError}:{onError:(message:string)=>void}){
  const [draft,setDraft]=useState<BackupSettings|null>(null)
  const [recipients,setRecipients]=useState<TelegramBackupRecipient[]>([])
  const [busy,setBusy]=useState(false)
  const load=useCallback(async()=>{
    try{
      const [settingsResponse,recipientsResponse]=await Promise.all([
        fetch(`${API}/api/v1/admin/backups/settings`,{credentials:'include'}),
        fetch(`${API}/api/v1/admin/backups/telegram-recipients`,{credentials:'include'})
      ])
      if(!settingsResponse.ok)throw new Error(await responseMessage(settingsResponse,'Настройки резервного копирования недоступны'))
      if(!recipientsResponse.ok)throw new Error(await responseMessage(recipientsResponse,'Диалоги Telegram недоступны'))
      const settings=await settingsResponse.json() as BackupSettings
      const recipientItems=await recipientsResponse.json() as TelegramBackupRecipient[]
      setDraft({...settings,telegramRecipientId:settings.telegramRecipientId??recipientItems[0]?.id,sendToObjectStorage:settings.sendToObjectStorage??false,objectStorageEndpoint:settings.objectStorageEndpoint??'',objectStorageRegion:settings.objectStorageRegion??'ru-central1',objectStorageBucket:settings.objectStorageBucket??'',objectStoragePrefix:settings.objectStoragePrefix??'proxyharbor/backups',objectStorageUsePathStyle:settings.objectStorageUsePathStyle??true,objectStorageCredentialsConfigured:settings.objectStorageCredentialsConfigured??false,objectStorageAccessKey:'',objectStorageSecretKey:'',clearObjectStorageCredentials:false})
      setRecipients(recipientItems)
    }catch(reason){onError(reason instanceof Error?reason.message:'Не удалось загрузить настройки резервного копирования')}
  },[onError])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const save=async()=>{
    if(!draft)return
    setBusy(true);onError('')
    try{
      const response=await fetch(`${API}/api/v1/admin/backups/settings`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:draft.enabled,intervalHours:draft.intervalHours,retentionDays:draft.retentionDays,historyRetentionDays:draft.historyRetentionDays,maxTelegramFileSizeMb:draft.maxTelegramFileSizeMb,sendToTelegram:draft.sendToTelegram,telegramRecipientId:draft.telegramRecipientId||null,sendToObjectStorage:draft.sendToObjectStorage,objectStorageEndpoint:draft.objectStorageEndpoint||null,objectStorageRegion:draft.objectStorageRegion,objectStorageBucket:draft.objectStorageBucket||null,objectStoragePrefix:draft.objectStoragePrefix,objectStorageUsePathStyle:draft.objectStorageUsePathStyle,objectStorageAccessKey:draft.objectStorageAccessKey||null,objectStorageSecretKey:draft.objectStorageSecretKey||null,clearObjectStorageCredentials:draft.clearObjectStorageCredentials})})
      if(!response.ok)throw new Error(await responseMessage(response,'Не удалось сохранить настройки резервного копирования'))
      const saved=await response.json() as BackupSettings
      setDraft({...saved,objectStorageAccessKey:'',objectStorageSecretKey:'',clearObjectStorageCredentials:false})
    }catch(reason){onError(reason instanceof Error?reason.message:'Не удалось сохранить настройки резервного копирования')}
    finally{setBusy(false)}
  }
  if(!draft)return <section className="admin-card backup-settings-card"><div className="empty-state"><RefreshCw className="spin"/>Загружаем настройки…</div></section>
  const recipientOptions=recipients.map(recipient=>[recipient.id,`${recipient.displayName}${recipient.username?` · @${recipient.username}`:''}${recipient.isDefault?' · по умолчанию':''}`,<MessageCircle/>] as const)
  return <section className="admin-card backup-settings-card" aria-labelledby="backup-settings-title"><div className="card-heading"><div><span className="kicker">ПОЛИТИКА И ДОСТАВКА</span><h2 id="backup-settings-title">Настройки резервного копирования</h2></div><Toggle checked={draft.enabled} onChange={enabled=>setDraft({...draft,enabled})} label={draft.enabled?'Расписание включено':'Расписание выключено'}/></div><div className="backup-format-note"><ShieldCheck/><div><b>{draft.format} — зашифрованный полный снимок ProxyHarbor</b><p>Это не открытый pg_dump: архив включает данные PostgreSQL, учётные записи, источники и безопасно сохранённые настройки. Восстановление выполняет ProxyHarbor.Restore. Ключ шифрования задаётся только на сервере и в панель не передаётся.</p></div></div><div className="backup-settings-grid"><label>Интервал, часов<input type="number" min="1" max="8760" value={draft.intervalHours} onChange={event=>setDraft({...draft,intervalHours:Number(event.target.value)})}/></label><label>Архивы на сервере, дней<input type="number" min="1" max="3650" value={draft.retentionDays} onChange={event=>setDraft({...draft,retentionDays:Number(event.target.value)})}/></label><label>История аудита, дней<input type="number" min="1" max="3650" value={draft.historyRetentionDays} onChange={event=>setDraft({...draft,historyRetentionDays:Number(event.target.value)})}/></label><label>Часть Telegram, MB<input type="number" min="1" max="49" value={draft.maxTelegramFileSizeMb} onChange={event=>setDraft({...draft,maxTelegramFileSizeMb:Number(event.target.value)})}/></label></div><div className="backup-retention-note"><Clock3/><span>После {draft.retentionDays} дн. файл удаляется с серверного volume, но запись остаётся в истории {draft.historyRetentionDays} дн. Поэтому старые строки могут быть недоступны для скачивания.</span></div><div className="backup-object-settings"><div className="backup-telegram-heading"><div><b>S3-совместимое внешнее хранилище</b><small>Основной канал для больших архивов. Загружается только PHB3-ciphertext; после загрузки система проверяет размер и SHA-256 объекта.</small></div><Toggle checked={draft.sendToObjectStorage} onChange={sendToObjectStorage=>setDraft({...draft,sendToObjectStorage})} label={draft.sendToObjectStorage?'Включено':'Выключено'}/></div>{draft.sendToObjectStorage&&<><div className="backup-object-grid"><label>HTTPS endpoint<input type="url" autoComplete="off" placeholder="https://storage.yandexcloud.net" value={draft.objectStorageEndpoint} onChange={event=>setDraft({...draft,objectStorageEndpoint:event.target.value})}/></label><label>Регион<input autoComplete="off" placeholder="ru-central1" value={draft.objectStorageRegion} onChange={event=>setDraft({...draft,objectStorageRegion:event.target.value})}/></label><label>Bucket<input autoComplete="off" placeholder="proxyharbor-backups" value={draft.objectStorageBucket} onChange={event=>setDraft({...draft,objectStorageBucket:event.target.value})}/></label><label>Префикс объектов<input autoComplete="off" placeholder="proxyharbor/backups" value={draft.objectStoragePrefix} onChange={event=>setDraft({...draft,objectStoragePrefix:event.target.value})}/></label><label>Access key<input autoComplete="new-password" data-1p-ignore="true" placeholder={draft.objectStorageCredentialsConfigured?'Сохранён · введите для замены':'Введите access key'} value={draft.objectStorageAccessKey} onChange={event=>setDraft({...draft,objectStorageAccessKey:event.target.value,clearObjectStorageCredentials:false})}/></label><label>Secret key<input type="password" autoComplete="new-password" data-1p-ignore="true" placeholder={draft.objectStorageCredentialsConfigured?'Сохранён · введите для замены':'Введите secret key'} value={draft.objectStorageSecretKey} onChange={event=>setDraft({...draft,objectStorageSecretKey:event.target.value,clearObjectStorageCredentials:false})}/></label></div><div className="backup-object-options"><Toggle checked={draft.objectStorageUsePathStyle} onChange={objectStorageUsePathStyle=>setDraft({...draft,objectStorageUsePathStyle})} label="Path-style адресация"/>{draft.objectStorageCredentialsConfigured&&<Toggle danger checked={draft.clearObjectStorageCredentials} onChange={clearObjectStorageCredentials=>setDraft({...draft,clearObjectStorageCredentials,objectStorageAccessKey:'',objectStorageSecretKey:''})} label="Удалить сохранённые S3-ключи"/>}</div></>}</div><div className="backup-telegram-settings"><div className="backup-telegram-heading"><div><b>Дополнительная отправка через Telegram</b><small>Отдельный BotFather token не нужен. Для больших архивов используйте S3, а Telegram оставьте дополнительным каналом.</small></div><Toggle checked={draft.sendToTelegram} onChange={sendToTelegram=>setDraft({...draft,sendToTelegram})} label={draft.sendToTelegram?'Включена':'Выключена'}/></div>{draft.sendToTelegram&&<div className="backup-recipient-field"><label>Получатель резервных копий<StyledSelect ariaLabel="Получатель резервных копий в Telegram" disabled={recipientOptions.length===0} value={draft.telegramRecipientId??recipientOptions[0]?.[0]??''} onChange={telegramRecipientId=>setDraft({...draft,telegramRecipientId})} options={recipientOptions.length>0?recipientOptions:[["","Нет доступных диалогов"]]}/></label><p className={draft.telegramBotConfigured?'ready':'warning'}><Bot/>{draft.telegramBotConfigured?'Основной бот настроен и готов отправлять архивы.':'Проверьте, что основной бот включён и выбранный пользователь не заблокировал его.'}</p></div>}</div><div className="backup-settings-actions"><span>{draft.encryptionConfigured?'PHB3-шифрование настроено':'Ключ PHB3 не настроен на сервере'}</span><button className="primary-admin-button" onClick={()=>void save()} disabled={busy||!draft.encryptionConfigured||(draft.sendToTelegram&&recipientOptions.length===0)||(draft.enabled&&!draft.sendToTelegram&&!draft.sendToObjectStorage)}><ShieldCheck/>{busy?'Сохраняем…':'Сохранить настройки'}</button></div></section>
}

/** Единая фирменная отметка для нативных checkbox: геометрия SVG не зависит от браузера. */
function CheckboxMark(){return <span className="ui-checkbox-mark" aria-hidden="true"><Check/></span>}

/** Фирменный мультивыбор стран с поиском, клавиатурным закрытием и нативными чекбоксами. */
function CountryFilter({countries,selected,onChange}:{countries:ProxyCountry[];selected:string[];onChange:(values:string[])=>void}){
  const [open,setOpen]=useState(false)
  const [search,setSearch]=useState('')
  const rootRef=useRef<HTMLDivElement>(null)
  useEffect(()=>{
    if(!open)return
    const close=(event:MouseEvent)=>{if(!rootRef.current?.contains(event.target as Node))setOpen(false)}
    const escape=(event:KeyboardEvent)=>{if(event.key==='Escape')setOpen(false)}
    document.addEventListener('mousedown',close)
    window.addEventListener('keydown',escape)
    return()=>{document.removeEventListener('mousedown',close);window.removeEventListener('keydown',escape)}
  },[open])
  const visible=countries.filter(country=>{
    const needle=search.trim().toLocaleLowerCase('ru-RU')
    return !needle||country.code.toLowerCase().includes(needle)||countryName(country.code).toLocaleLowerCase('ru-RU').includes(needle)
  })
  const toggle=(code:string)=>onChange(selected.includes(code)?selected.filter(item=>item!==code):[...selected,code].sort())
  return <div className={`country-filter ${open?'open':''}`} ref={rootRef}>
    <button type="button" className="country-filter-trigger" aria-haspopup="dialog" aria-expanded={open} onClick={()=>setOpen(value=>!value)}><Globe2/>{selected.length===0?'Страны':`Страны · ${selected.length}`}<ChevronDown/></button>
    {open&&<div className="country-filter-menu" role="dialog" aria-label="Фильтр по странам">
      <div className="country-filter-head"><div><span className="kicker">ГЕОГРАФИЯ</span><b>Выберите страны</b></div>{selected.length>0&&<button type="button" onClick={()=>onChange([])}>Сбросить</button>}</div>
      <label className="country-search"><Globe2/><input autoFocus value={search} onChange={event=>setSearch(event.target.value)} placeholder="Поиск страны…" aria-label="Поиск страны"/>{search&&<button type="button" aria-label="Очистить поиск" onClick={()=>setSearch('')}><X/></button>}</label>
      <div className="country-options">{visible.length===0?<p>Страны появятся после определения IP.</p>:visible.map(country=><label key={country.code}><input className="ui-checkbox-input" type="checkbox" checked={selected.includes(country.code)} onChange={()=>toggle(country.code)}/><CheckboxMark/><CountryFlag code={country.code}/><b>{countryName(country.code)}</b><em>{formatNumber(country.count)}</em></label>)}</div>
      <footer><span>{selected.length===0?'Показаны все страны':`Выбрано: ${selected.length}`}</span><button type="button" onClick={()=>setOpen(false)}>Готово</button></footer>
    </div>}
  </div>
}

/** Управление внешними VPS, которые забирают непересекающиеся партии проверок. */
function AdminCheckerNodesPage(){
  const [data,setData]=useState<CheckerNodeList|null>(null);const [error,setError]=useState('');const [busy,setBusy]=useState('');const [dialog,setDialog]=useState<{mode:'add'|'deploy'|'delete';node?:CheckerNode}|null>(null)
  const load=useCallback(async()=>{try{const response=await fetch(`${API}/api/v1/admin/checker-nodes`,{credentials:'include'});if(!response.ok)throw new Error(await responseMessage(response,'Узлы проверки недоступны'));setData(await response.json() as CheckerNodeList);setError('')}catch(reason){setError(reason instanceof Error?reason.message:'Узлы проверки недоступны')}},[])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);const refresh=window.setInterval(()=>void load(),30000);return()=>{window.clearTimeout(timer);window.clearInterval(refresh)}},[load])
  const update=async(node:CheckerNode,patch:Partial<CheckerNode>)=>{setBusy(node.id);try{const next={...node,...patch};const response=await fetch(`${API}/api/v1/admin/checker-nodes/${node.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:next.enabled,concurrency:next.concurrency,batchSize:next.batchSize})});if(!response.ok)throw new Error(await responseMessage(response,'Настройки узла не сохранены'));await load()}catch(reason){setError(reason instanceof Error?reason.message:'Настройки узла не сохранены')}finally{setBusy('')}}
  const nodes=data?.items??[];const online=nodes.filter(node=>node.online).length;const active=nodes.filter(node=>node.busy).length;const checks=nodes.reduce((sum,node)=>sum+node.completedChecks,0)
  return <section className="admin-section checker-admin-section" aria-labelledby="admin-checkers-title"><AdminPageHeader id="admin-checkers-title" title="Узлы проверки"><div className="checker-heading-actions"><button className="icon-button" aria-label="Обновить узлы" onClick={()=>void load()}><RefreshCw/></button><button className="primary-admin-button" onClick={()=>setDialog({mode:'add'})}><Plus/>Подключить VPS</button></div></AdminPageHeader><ToastSignal kind="error" message={error}/>
    <div className="admin-summary-grid compact-summary checker-summary"><article><span className="summary-icon"><Server/></span><div><small>Подключено</small><strong>{nodes.length}</strong><p>можно добавлять новые VPS</p></div></article><article><span className="summary-icon"><Activity/></span><div><small>На связи</small><strong>{online}</strong><p>heartbeat не старше 2 минут</p></div></article><article><span className="summary-icon"><Workflow/></span><div><small>В работе</small><strong>{active}</strong><p>исполняют партии</p></div></article><article><span className="summary-icon"><Check/></span><div><small>Проверено узлами</small><strong>{formatNumber(checks)}</strong><p>накопительный счётчик</p></div></article></div>
    <section className="admin-card checker-registry"><div className="card-heading"><div><span className="kicker">DISTRIBUTED VALIDATION</span><h2>Внешние VPS</h2><p>SSH-пароль используется только при установке. Истёкшая аренда автоматически возвращается в очередь, а основной сервер остаётся резервным проверяющим.</p></div>{data&&<code className="checker-image">{data.image}</code>}</div><div className="checker-node-list">{data===null?<div className="empty-state"><RefreshCw className="spin"/>Загружаем узлы…</div>:nodes.length===0?<div className="empty-state"><Network/>Внешние узлы ещё не подключены.</div>:nodes.map(node=><article key={node.id} className={!node.enabled?'disabled':''}><div className="checker-node-identity"><span className={`checker-node-indicator ${node.online?'online':''}`}/><div><b>{node.name}</b><small>{node.host}:{node.sshPort} · {node.sshUsername}{node.agentVersion&&` · v${node.agentVersion}`}</small></div></div><div className="checker-node-health"><span className={`state-pill ${node.online?'active':''}`}>{node.online?node.busy?'Проверяет':'На связи':node.enabled?'Не на связи':'Отключён'}</span><small>{node.lastHeartbeatAt?`Heartbeat ${timeAgo(node.lastHeartbeatAt)}`:'Heartbeat ещё не получен'} · {node.deploymentStatus}</small>{node.lastError&&<em title={node.lastError}>{node.lastError}</em>}</div><label>Параллельно<input type="number" min="1" max="1000" value={node.concurrency} onChange={event=>setData(current=>current?{...current,items:current.items.map(item=>item.id===node.id?{...item,concurrency:Number(event.target.value)}:item)}:current)}/></label><label>Партия<input type="number" min="1" max="10000" value={node.batchSize} onChange={event=>setData(current=>current?{...current,items:current.items.map(item=>item.id===node.id?{...item,batchSize:Number(event.target.value)}:item)}:current)}/></label><div className="checker-node-stats"><b>{formatNumber(node.completedChecks)}</b><small>проверок · {formatNumber(node.aliveChecks)} рабочих</small></div><div className="checker-node-actions"><Toggle checked={node.enabled} onChange={enabled=>void update(node,{enabled})} label="Активен"/><button className="table-action" disabled={busy===node.id} onClick={()=>void update(node,{})}><Check/>Сохранить</button><button className="icon-button" data-tooltip="Переустановить агент и сменить токен" aria-label={`Переустановить агент ${node.name}`} onClick={()=>setDialog({mode:'deploy',node})}><RefreshCw/></button><button className="icon-button danger" data-tooltip="Удалить агент с VPS" aria-label={`Удалить узел ${node.name}`} onClick={()=>setDialog({mode:'delete',node})}><Trash2/></button></div></article>)}</div></section>
    {dialog&&<CheckerNodeDialog value={dialog} busy={busy==='dialog'} onClose={()=>setDialog(null)} onComplete={async()=>{setDialog(null);await load()}} onBusy={value=>setBusy(value?'dialog':'')} onError={setError}/>}</section>
}

function CheckerNodeDialog({value,busy,onClose,onComplete,onBusy,onError}:{value:{mode:'add'|'deploy'|'delete';node?:CheckerNode};busy:boolean;onClose:()=>void;onComplete:()=>Promise<void>;onBusy:(value:boolean)=>void;onError:(message:string)=>void}){
  const [form,setForm]=useState({name:'',host:'',sshPort:22,sshUsername:'root',password:'',concurrency:200,batchSize:400})
  const submit=async(event:React.FormEvent)=>{event.preventDefault();onBusy(true);onError('');try{const node=value.node;const url=value.mode==='add'?`${API}/api/v1/admin/checker-nodes`:value.mode==='deploy'?`${API}/api/v1/admin/checker-nodes/${node?.id}/deploy`:`${API}/api/v1/admin/checker-nodes/${node?.id}`;const response=await fetch(url,{method:value.mode==='add'?'POST':value.mode==='delete'?'DELETE':'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(value.mode==='add'?form:{password:form.password})});if(!response.ok)throw new Error(await responseMessage(response,value.mode==='delete'?'Узел не удалён':'Агент не развёрнут'));setForm(current=>({...current,password:''}));await onComplete()}catch(reason){onError(reason instanceof Error?reason.message:'Операция с VPS не выполнена')}finally{onBusy(false)}}
  const destructive=value.mode==='delete';const title=value.mode==='add'?'Подключить VPS':destructive?'Удалить узел':'Переустановить агент'
  return <div className="source-editor-backdrop" role="presentation" onMouseDown={event=>{if(event.target===event.currentTarget&&!busy)onClose()}}><section className="source-editor-modal checker-node-modal" role="dialog" aria-modal="true" aria-labelledby="checker-node-dialog-title"><div className="source-editor-heading"><div><span className="kicker">CHECKER NODE</span><h2 id="checker-node-dialog-title">{title}</h2><p>{value.mode==='add'?'Система проверит SSH-доступ и запустит агент в существующем Docker либо как защищённую systemd-службу. Другие сервисы VPS не изменяются.':destructive?'Контейнер и его токен будут удалены с VPS, затем запись исчезнет из панели.':'Сохранённый отпечаток SSH-хоста будет проверен, агент заменён, а токен безопасно сменён.'}</p></div><button className="icon-button" aria-label="Закрыть" disabled={busy} onClick={onClose}><X/></button></div><form className="source-editor-form" onSubmit={submit} autoComplete="off">{value.mode==='add'&&<div className="source-editor-grid"><label>Название<input autoFocus required minLength={2} maxLength={100} value={form.name} onChange={event=>setForm({...form,name:event.target.value})} placeholder="VPS Германия 1"/></label><label>Публичный IP<input required inputMode="decimal" value={form.host} onChange={event=>setForm({...form,host:event.target.value})} placeholder="203.0.113.10"/></label><label>SSH-порт<input required type="number" min="1" max="65535" value={form.sshPort} onChange={event=>setForm({...form,sshPort:Number(event.target.value)})}/></label><label>SSH-пользователь<input required maxLength={32} value={form.sshUsername} onChange={event=>setForm({...form,sshUsername:event.target.value})}/></label><label>Параллельных проверок<input required type="number" min="1" max="1000" value={form.concurrency} onChange={event=>setForm({...form,concurrency:Number(event.target.value)})}/></label><label>Размер партии<input required type="number" min="1" max="10000" value={form.batchSize} onChange={event=>setForm({...form,batchSize:Number(event.target.value)})}/></label></div>}<label>SSH-пароль<input autoFocus={value.mode!=='add'} required type="password" autoComplete="new-password" minLength={8} maxLength={512} value={form.password} onChange={event=>setForm({...form,password:event.target.value})} placeholder="Используется один раз и не сохраняется"/><small>Пароль существует только в памяти запроса и не попадает в БД, backup или журнал.</small></label><div className="source-editor-actions"><span/><button type="button" className="secondary-admin-button" disabled={busy} onClick={onClose}>Отмена</button><button className={destructive?'danger-admin-button':'primary-admin-button'} disabled={busy}>{destructive?<Trash2/>:<Server/>}{busy?'Выполняем…':title}</button></div></form></section></div>
}

/** Компактная навигация страницы освобождает место для рабочих данных. */
function AdminPageHeader({id,title,children}:{id:string;title:string;children?:React.ReactNode}){
  return <header className="admin-page-heading">
    <nav className="admin-breadcrumb" aria-label="Положение в панели управления">
      <a href="/admin">Панель управления</a><ArrowRight aria-hidden="true"/><h1 id={id}>{title}</h1>
    </nav>
    {children&&<div className="admin-heading-actions">{children}</div>}
  </header>
}

/* AdminSiteSettingsPage was moved to a lazy route chunk.
function AdminSiteSettingsPage(){
  const {refresh}=useSiteSettings()
  const [draft,setDraft]=useState<SiteSettings|null>(null)
  const [tab,setTab]=useState<'sections'|'requisites'|'cookies'|'analytics'>('sections')
  const [busy,setBusy]=useState(false)
  const [error,setError]=useState('')
  const [saved,setSaved]=useState('')

  useEffect(()=>{
    const controller=new AbortController()
    const load=async()=>{
      try{
        const response=await fetch(`${API}/api/v1/admin/site-settings`,{credentials:'include',cache:'no-store',signal:controller.signal})
        if(!response.ok)throw new Error(await responseMessage(response,'Не удалось загрузить настройки сайта'))
        setDraft(await response.json() as SiteSettings)
      }catch(reason){if(!isAbortError(reason))setError(reason instanceof Error?reason.message:'Не удалось загрузить настройки сайта')}
    }
    void load()
    return()=>controller.abort()
  },[])

  const updateSection=(code:SiteSectionCode,patch:Partial<SiteSettings['sections'][SiteSectionCode]>)=>setDraft(current=>current?{...current,sections:{...current.sections,[code]:{...current.sections[code],...patch}}}:current)
  const updateField=(code:(typeof requisiteFieldCodes)[number],patch:Partial<SiteSettings['requisites']['fields'][typeof code]>)=>setDraft(current=>current?{...current,requisites:{...current.requisites,fields:{...current.requisites.fields,[code]:{...current.requisites.fields[code],...patch}}}}:current)
  const save=async()=>{
    if(!draft||busy)return
    setBusy(true);setError('');setSaved('')
    try{
      const response=await fetch(`${API}/api/v1/admin/site-settings`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(draft)})
      if(!response.ok)throw new Error(await responseMessage(response,'Настройки сайта не сохранены'))
      const next=await response.json() as SiteSettings
      setDraft(next)
      await refresh()
      setSaved('Настройки опубликованы. Новая конфигурация применяется без перезапуска.')
    }catch(reason){setError(reason instanceof Error?reason.message:'Настройки сайта не сохранены')}
    finally{setBusy(false)}
  }

  if(!draft)return <section className="admin-section"><AdminPageHeader id="admin-site-title" title="Сайт и документы"/><div className="admin-initial-loading"><RefreshCw className="spin"/><span>Загружаем настройки…</span></div><ToastSignal kind="error" message={error}/></section>

  const tracker=(code:'yandex'|'google'|'vk',title:string,description:string,placeholder:string,help:string)=>{
    const value=draft.analytics[code]
    return <article className="admin-card site-tracker-card" key={code}><div className="site-tracker-heading"><div><span className="summary-icon"><Gauge/></span><span><b>{title}</b><small>{description}</small></span></div><Toggle checked={value.enabled} onChange={enabled=>setDraft({...draft,analytics:{...draft.analytics,[code]:{...value,enabled}}})} label={value.enabled?'Включена':'Выключена'}/></div><label>Идентификатор<input autoComplete="off" maxLength={128} placeholder={placeholder} value={value.identifier} onChange={event=>setDraft({...draft,analytics:{...draft.analytics,[code]:{...value,identifier:event.target.value}}})}/></label><a href={help} target="_blank" rel="noreferrer"><HelpCircle/>Где получить идентификатор</a><p><ShieldCheck/>Код загружается только после явного разрешения статистики.</p></article>
  }

  return <section className="admin-section site-settings-section" aria-labelledby="admin-site-title">
    <AdminPageHeader id="admin-site-title" title="Сайт и документы"><button className="primary-admin-button" disabled={busy} onClick={()=>void save()}><ShieldCheck/>{busy?'Сохраняем…':'Сохранить и опубликовать'}</button></AdminPageHeader>
    <ToastSignal kind="error" message={error}/><ToastSignal kind="success" message={saved}/>
    <AdminTabs value={tab} onChange={value=>setTab(value as typeof tab)} idPrefix="site-settings" items={[["sections","Разделы"],["requisites","Реквизиты"],["cookies","Cookies"],["analytics","Метрики"]]}/>

    {tab==='sections'&&<div id="site-settings-panel-sections" role="tabpanel" aria-labelledby="site-settings-tab-sections" className="admin-card site-section-settings"><div className="card-heading"><div><span className="kicker">PUBLICATION</span><h2>Публичные разделы</h2><p>Страницу можно снять с публикации либо оставить доступной только по прямой ссылке. Обязательные документы нельзя отключить, но можно убрать из меню.</p></div></div><div className="site-section-list">{siteSectionCodes.map(code=>{const value=draft.sections[code];const required=requiredSiteSections.has(code);return <article key={code}><div><b>{siteSectionLabels[code]}</b><small>{sectionHref(code)}{required?' · обязательный документ':''}</small></div><Toggle disabled={required} checked={value.published} onChange={published=>updateSection(code,{published,showInNavigation:published?value.showInNavigation:false})} label={value.published?'Опубликован':'Скрыт'}/><Toggle disabled={!value.published} checked={value.showInNavigation} onChange={showInNavigation=>updateSection(code,{showInNavigation})} label="В навигации"/></article>})}</div></div>}

    {tab==='requisites'&&<div id="site-settings-panel-requisites" role="tabpanel" aria-labelledby="site-settings-tab-requisites" className="site-requisites-settings"><section className="admin-card"><div className="card-heading"><div><span className="kicker">OWNER DATA</span><h2>Карточка исполнителя</h2><p>Значения используются на странице реквизитов и в юридических документах. Переключатель управляет строкой именно на странице реквизитов.</p></div></div><div className="site-copy-grid"><label>Заголовок<input maxLength={120} value={draft.requisites.introTitle} onChange={event=>setDraft({...draft,requisites:{...draft.requisites,introTitle:event.target.value}})}/></label><label className="wide">Описание<textarea maxLength={500} value={draft.requisites.introDescription} onChange={event=>setDraft({...draft,requisites:{...draft.requisites,introDescription:event.target.value}})}/></label></div><div className="site-requisite-fields">{requisiteFieldCodes.filter(code=>!bankRequisiteFields.has(code)).map(code=>{const value=draft.requisites.fields[code];return <article key={code}><label><span>{requisiteFieldLabels[code]}</span><input autoComplete="off" maxLength={500} value={value.value} onChange={event=>updateField(code,{value:event.target.value})}/></label><Toggle checked={value.visible} onChange={visible=>updateField(code,{visible})} label={value.visible?'Показывать':'Скрыто'}/></article>})}</div></section><section className="admin-card"><div className="card-heading"><div><span className="kicker">BANK DETAILS</span><h2>Банковские реквизиты</h2><p>Блок по умолчанию скрыт. Даже при включённом блоке можно скрыть отдельные строки.</p></div><Toggle checked={draft.requisites.bankSectionVisible} onChange={bankSectionVisible=>setDraft({...draft,requisites:{...draft.requisites,bankSectionVisible}})} label={draft.requisites.bankSectionVisible?'Блок опубликован':'Блок скрыт'}/></div><div className="site-requisite-fields">{requisiteFieldCodes.filter(code=>bankRequisiteFields.has(code)).map(code=>{const value=draft.requisites.fields[code];return <article key={code}><label><span>{requisiteFieldLabels[code]}</span><input autoComplete="off" maxLength={500} value={value.value} onChange={event=>updateField(code,{value:event.target.value})}/></label><Toggle disabled={!draft.requisites.bankSectionVisible} checked={value.visible} onChange={visible=>updateField(code,{visible})} label={value.visible?'Показывать':'Скрыто'}/></article>})}</div><label className="site-note-field">Пояснение<textarea maxLength={1000} value={draft.requisites.note} onChange={event=>setDraft({...draft,requisites:{...draft.requisites,note:event.target.value}})}/></label></section></div>}

    {tab==='cookies'&&<div id="site-settings-panel-cookies" role="tabpanel" aria-labelledby="site-settings-tab-cookies" className="admin-card site-cookie-settings"><div className="card-heading"><div><span className="kicker">CONSENT</span><h2>Обязательный первый выбор</h2><p>Новый посетитель не может закрыть диалог, пока не выберет только необходимые cookies либо разрешит статистику. Оферта или согласие на рекламу с этим выбором не объединяются.</p></div><span className="state-pill active">Всегда включён</span></div><div className="site-cookie-required"><LockKeyhole/><div><b>Необходимые cookies</b><p>Авторизация и выбранный язык работают независимо от разрешения статистики.</p></div></div><div className="site-copy-grid"><label>Заголовок<input maxLength={120} value={draft.cookies.bannerTitle} onChange={event=>setDraft({...draft,cookies:{...draft.cookies,bannerTitle:event.target.value}})}/></label><label className="wide">Текст диалога<textarea maxLength={1000} value={draft.cookies.bannerText} onChange={event=>setDraft({...draft,cookies:{...draft.cookies,bannerText:event.target.value}})}/></label></div><Toggle checked={draft.cookies.showSettingsButton} onChange={showSettingsButton=>setDraft({...draft,cookies:{...draft.cookies,showSettingsButton}})} label="Показывать кнопку повторной настройки"/><p className="privacy-note"><ShieldCheck/>При изменении текста или набора метрик редакция согласия повысится автоматически, и посетители сделают выбор заново.</p></div>}

    {tab==='analytics'&&<div id="site-settings-panel-analytics" role="tabpanel" aria-labelledby="site-settings-tab-analytics" className="site-analytics-settings"><section className="admin-card first-party-analytics"><div><span className="summary-icon"><MousePointerClick/></span><span><b>Статистика ProxyHarbor</b><small>Минимальные page codes и IP с ограниченным сроком хранения; без query и рекламных cookies.</small></span></div><Toggle checked={draft.analytics.firstPartyEnabled} onChange={firstPartyEnabled=>setDraft({...draft,analytics:{...draft.analytics,firstPartyEnabled}})} label={draft.analytics.firstPartyEnabled?'Включена':'Выключена'}/></section><div className="site-tracker-grid">{tracker('yandex','Яндекс Метрика','Номер счётчика; Вебвизор принудительно выключен.','12345678','https://yandex.ru/support/metrica/ru/quick-start')}{tracker('google','Google Analytics 4','Measurement ID веб-потока.','G-XXXXXXXXXX','https://support.google.com/analytics/answer/9539598?hl=ru')}{tracker('vk','VK Pixel','Идентификатор пикселя ретаргетинга.','VK-RTRG-…','https://ads.vk.com/')}</div><aside className="site-analytics-warning"><ShieldOff/><p>Внешние метрики могут означать трансграничную передачу и использование сторонних обработчиков. Включайте их после проверки политики, уведомлений Роскомнадзора и договорных оснований.</p></aside></div>}
  </section>
}

*/
function AdminTabs({value,onChange,items,ariaLabel='Разделы страницы',idPrefix}:{value:string;onChange:(value:string)=>void;items:[string,string][];ariaLabel?:string;idPrefix?:string}){
  const activate=(index:number,event:React.KeyboardEvent<HTMLButtonElement>)=>{const buttons=event.currentTarget.parentElement?.querySelectorAll<HTMLButtonElement>('[role="tab"]');const button=buttons?.[index];if(!button)return;button.focus();onChange(items[index][0])}
  return <nav className="admin-tabs" role="tablist" aria-label={ariaLabel}>{items.map(([key,label],index)=><button key={key} type="button" id={idPrefix?`${idPrefix}-tab-${key}`:undefined} role="tab" aria-selected={value===key} aria-controls={idPrefix?`${idPrefix}-panel-${key}`:undefined} tabIndex={value===key?0:-1} className={value===key?'active':''} onClick={()=>onChange(key)} onKeyDown={event=>{if(!['ArrowRight','ArrowLeft','Home','End'].includes(event.key))return;event.preventDefault();if(event.key==='ArrowRight')activate((index+1)%items.length,event);else if(event.key==='ArrowLeft')activate((index-1+items.length)%items.length,event);else if(event.key==='Home')activate(0,event);else activate(items.length-1,event)}}>{label}</button>)}</nav>
}

type PaymentProviderHelp = {accountPath:string;officialUrl:string;steps:string[];webhookNote:string}

const paymentProviderHelp:Record<string,PaymentProviderHelp>={
  yookassa:{accountPath:'Личный кабинет → Настройки → Магазин и Интеграция → Ключи API',officialUrl:'https://yookassa.ru/developers/using-api/interaction-format',steps:['Скопируйте shopId магазина.','Выпустите и сохраните секретный ключ API.','В разделе «Интеграция → HTTP-уведомления» добавьте указанный ниже URL и включите событие payment.succeeded.'],webhookNote:'ЮKassa принимает только HTTPS URL на порту 443 или 8443.'},
  yoomoney:{accountPath:'Кошелёк ЮMoney → Настройки → HTTP-уведомления',officialUrl:'https://yoomoney.ru/docs/payment-buttons/using-api/notifications',steps:['Укажите номер кошелька, на который будут поступать переводы.','Включите HTTP-уведомления и вставьте указанный ниже URL.','Скопируйте созданный секрет уведомлений в поле формы.'],webhookNote:'Для одного кошелька ЮMoney можно указать только один адрес HTTP-уведомлений.'},
  cloudpayments:{accountPath:'Back Office CloudPayments → Сайты и уведомления',officialUrl:'https://developers.cloudpayments.ru/',steps:['Скопируйте Public ID сайта и API Secret из Back Office.','Откройте настройки уведомлений и включите уведомление Pay.','Укажите URL ниже, метод POST, кодировку UTF-8 и формат CloudPayments.'],webhookNote:'API Secret одновременно используется для API-аутентификации и проверки HMAC уведомлений.'},
  robokassa:{accountPath:'Личный кабинет Robokassa → Магазины → Технические настройки',officialUrl:'https://docs.robokassa.ru/ru/',steps:['Скопируйте Merchant Login, Пароль №1 и Пароль №2.','Для Result URL укажите адрес ниже и метод POST.','Для подписей выберите алгоритм SHA-256; тестовый режим включайте только с тестовыми паролями.'],webhookNote:'Пароль №1 подписывает создание счёта, Пароль №2 проверяет ResultURL.'},
  tbank:{accountPath:'Т-Бизнес → Интернет-эквайринг → Магазины → Терминалы → Настроить',officialUrl:'https://developer.tbank.ru/eacq/intro/developer/terminal',steps:['Скопируйте TerminalKey выбранного терминала.','Скопируйте пароль терминала — он чувствителен к регистру.','NotificationURL передаётся сервисом при создании каждого платежа; при желании тот же URL можно сохранить в настройках терминала.'],webhookNote:'Терминал должен быть рабочим, а не неактивным; для проверки используйте отдельный тестовый терминал.'},
  stripe:{accountPath:'Stripe Dashboard → Developers → API keys и Webhooks',officialUrl:'https://docs.stripe.com/keys',steps:['Скопируйте серверный Secret key нужного режима: sk_test_… или sk_live_….','В разделе Webhooks создайте endpoint с URL ниже.','Подпишите endpoint на checkout.session.completed и скопируйте его Signing secret whsec_….'],webhookNote:'API Secret и Webhook Signing secret — разные ключи; тестовые и рабочие ключи нельзя смешивать.'},
  cryptomus:{accountPath:'Cryptomus → Business → Merchants → ваш проект → Settings',officialUrl:'https://doc.cryptomus.com/ru',steps:['Создайте Merchant и подтвердите домен, если сервис запросит модерацию.','Скопируйте UUID мерчанта и Payment API key.','Callback URL передаётся автоматически при создании платежа; адрес ниже показан для проверки.'],webhookNote:'Нужен именно Payment API key, а не Payout API key.'},
  nowpayments:{accountPath:'NOWPayments Account → Store Settings',officialUrl:'https://nowpayments.io/help/about-nowpayments/how-to-start/how-to-set-up-an-account',steps:['Добавьте кошелёк для получения средств и сохраните настройки магазина.','Создайте API key через Add new key.','Сгенерируйте IPN Secret и сохраните его сразу: сервис показывает секрет только один раз.'],webhookNote:'IPN callback передаётся автоматически при создании счёта; API key и IPN Secret вводятся в разные поля.'}
}

function providerOperationalLabel(provider:AdminPaymentProvider){const state=provider.operational?.state;if(state==='healthy')return `${provider.operational?.paidAfterConfigurationUpdate??provider.operational?.paidOrders??0} оплат после настройки`;if(state==='pending')return `Ожидают подтверждения: ${provider.operational?.pendingOrders??0}`;if(state==='retest_required')return 'Нужна повторная тестовая оплата';if(state==='webhook_attention')return 'Webhook требует проверки';if(state==='no_successful_payments')return 'Нет подтверждённых оплат';if(state==='awaiting_first_payment')return 'Ожидает первой оплаты';return provider.ready?'Реквизиты заполнены':'Требуется настройка'}
function providerOperationalTone(provider:AdminPaymentProvider){const state=provider.operational?.state;return state==='healthy'?'healthy':state==='retest_required'||state==='webhook_attention'||state==='no_successful_payments'?'attention':''}

function ProviderCards({providers,onOpen}:{providers:AdminPaymentProviderDraft[];onOpen:(code:string)=>void}){return <section className="provider-card-grid">{providers.map(provider=><article className="admin-card provider-card" key={provider.code}><div className="provider-card-icon"><CreditCard/></div><div><strong>{provider.name}</strong><small>{provider.ready?'Реквизиты заполнены':'Требуется настройка'}</small></div><span className={`state-pill ${provider.enabled&&provider.ready?'active':''}`}>{provider.enabled?'Включён':'Выключен'}</span><div className={`provider-operational-note ${providerOperationalTone(provider)}`}>{providerOperationalTone(provider)==='healthy'?<ShieldCheck/>:providerOperationalTone(provider)==='attention'?<ShieldOff/>:<Clock3/>}<span><b>{providerOperationalLabel(provider)}</b>{provider.operational?.attention&&<small>{provider.operational.attention}</small>}</span></div><div className="provider-card-actions"><button aria-label={`Настройки ${provider.name}`} onClick={()=>onOpen(provider.code)}><Settings2/>Настройки</button><button aria-label={`Как подключить ${provider.name}`} onClick={()=>onOpen(provider.code)}><HelpCircle/>Как подключить</button></div></article>)}</section>}

function ProviderDialog({provider,invoices,busy,onClose,onUpdate,onSave}:{provider:AdminPaymentProviderDraft;invoices:InvoicePage|null;busy:boolean;onClose:()=>void;onUpdate:(code:string,patch:Partial<AdminPaymentProviderDraft>)=>void;onSave:()=>void}){
  const help=paymentProviderHelp[provider.code]
  const ignoreAutofill={autoComplete:'off','data-1p-ignore':true,'data-lpignore':'true','data-form-type':'other'} as const
  return <div className="source-editor-backdrop" role="presentation" onMouseDown={event=>{if(event.target===event.currentTarget)onClose()}}><section className="source-editor-modal provider-modal" role="dialog" aria-modal="true" aria-label={`Настройки ${provider.name}`}><div className="source-editor-heading"><div><span className="kicker">PAYMENT PROVIDER</span><h2>{provider.name}</h2><p>Реквизиты шлюза изолированы; сохранённые секреты никогда не возвращаются в браузер.</p></div><button className="icon-button" aria-label={`Закрыть настройки ${provider.name}`} onClick={onClose}><X/></button></div>
    <section className="provider-help" aria-labelledby={`provider-help-${provider.code}`}><div className="provider-help-heading"><span><HelpCircle/></span><div><small>ПОМОЩЬ ПО ПОДКЛЮЧЕНИЮ</small><h3 id={`provider-help-${provider.code}`}>Подключение {provider.name} за 3 шага</h3><b>{help.accountPath}</b></div></div><ol>{help.steps.map(step=><li key={step}>{step}</li>)}</ol><div className="provider-help-footer"><p>{help.webhookNote}</p><a href={help.officialUrl} target="_blank" rel="noreferrer">Открыть официальную инструкцию <ArrowRight/></a></div></section>
    <div className="provider-modal-status"><Toggle checked={provider.enabled} onChange={checked=>onUpdate(provider.code,{enabled:checked})} label="Принимать платежи"/><span className={`state-pill ${provider.ready?'active':''}`}>{provider.ready?'Готов':'Не настроен'}</span></div>{provider.operational&&<div className={`provider-operational-detail ${providerOperationalTone(provider)}`}><div>{providerOperationalTone(provider)==='healthy'?<ShieldCheck/>:providerOperationalTone(provider)==='attention'?<ShieldOff/>:<Clock3/>}<span><b>{providerOperationalLabel(provider)}</b><small>Счетов: {provider.operational.totalOrders} · оплачено: {provider.operational.paidOrders} · ожидает: {provider.operational.pendingOrders}</small></span></div>{provider.operational.attention&&<p>{provider.operational.attention}</p>}</div>}
    <div className="provider-fields">{needsMerchantId(provider.code)&&<label>{merchantFieldLabel(provider.code)}<input {...ignoreAutofill} name={`gateway-${provider.code}-merchant`} spellCheck={false} maxLength={256} value={provider.merchantId} onChange={event=>onUpdate(provider.code,{merchantId:event.target.value})}/></label>}{provider.code==='cloudpayments'&&<label>Public ID<input {...ignoreAutofill} name="gateway-cloudpayments-public-id" spellCheck={false} maxLength={256} value={provider.publicId} onChange={event=>onUpdate(provider.code,{publicId:event.target.value})}/></label>}<label>{primarySecretLabel(provider.code)}<input {...ignoreAutofill} name={`gateway-${provider.code}-primary-secret`} type="password" spellCheck={false} maxLength={4096} placeholder={provider.secretConfigured?'Сохранён · введите для замены':'Введите секрет'} value={provider.secretKey} onChange={event=>onUpdate(provider.code,{secretKey:event.target.value,clearSecretKey:false})}/><small>{provider.secretConfigured?'Секрет настроен и скрыт':'Секрет ещё не задан'}</small></label>{needsSecondarySecret(provider.code)&&<label>{secondarySecretLabel(provider.code)}<input {...ignoreAutofill} name={`gateway-${provider.code}-secondary-secret`} type="password" spellCheck={false} maxLength={4096} placeholder={provider.secondarySecretConfigured?'Сохранён · введите для замены':'Введите второй секрет'} value={provider.secondarySecret} onChange={event=>onUpdate(provider.code,{secondarySecret:event.target.value,clearSecondarySecret:false})}/></label>}</div>
    {provider.code==='robokassa'&&<Toggle checked={provider.testMode} onChange={checked=>onUpdate(provider.code,{testMode:checked})} label="Тестовый режим"/>}<div className="webhook-box"><small>Webhook URL</small><code>{provider.webhookUrl}</code></div><div className="provider-secret-actions">{provider.secretConfigured&&<Toggle danger checked={provider.clearSecretKey} onChange={checked=>onUpdate(provider.code,{clearSecretKey:checked,secretKey:''})} label="Удалить основной секрет"/>}{provider.secondarySecretConfigured&&<Toggle danger checked={provider.clearSecondarySecret} onChange={checked=>onUpdate(provider.code,{clearSecondarySecret:checked,secondarySecret:''})} label="Удалить второй секрет"/>}</div><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={onClose}>Закрыть</button><button className="primary-admin-button" onClick={onSave} disabled={busy}><ShieldCheck/>{busy?'Сохраняем…':'Сохранить'}</button></div><div className="provider-invoices"><h3>Последние счета через {provider.name}</h3><InvoiceTable data={invoices}/></div></section></div>
}

function InvoiceRegistry({data,status,onStatus,page,onPage}:{data:InvoicePage|null;status:string;onStatus:(value:string)=>void;page:number;onPage:(value:number)=>void}){const totalPages=Math.max(1,Math.ceil((data?.total??0)/10));return <section className="admin-card invoice-registry"><div className="card-heading"><div><span className="kicker">ЕДИНЫЙ РЕЕСТР</span><h2>Счета</h2></div><span className="section-count">{data?.total??0}</span></div><AdminTabs value={status} onChange={onStatus} items={[["","Все"],["pending","Ожидают"],["paid","Оплачены"],["failed","Ошибки"],["canceled","Отменены"],["refunded","Возвраты"]]}/><InvoiceTable data={data}/>{data&&data.total>10&&<ProxyPagination page={page} pageSize={10} total={data.total} totalPages={totalPages} onPageChange={onPage} onPageSizeChange={()=>{}}/>}</section>}
function InvoiceTable({data}:{data:InvoicePage|null}){return <div className="admin-data-table invoice-table"><div className="admin-data-head"><span>Счёт / клиент</span><span>Способ оплаты</span><span>Сумма</span><span>Статус</span><span>Создан</span></div>{!data?<div className="empty-state"><RefreshCw className="spin"/>Загружаем счета…</div>:data.items.length===0?<div className="empty-state"><Receipt/>Счетов в этой группе пока нет.</div>:data.items.map(item=><article key={item.id}><span><b>#{item.id.slice(0,8)}</b><small>{item.email||item.userName}</small></span><span><b>{providerLabel(item.provider)}</b><small>{item.paymentInstrument||item.paymentMethod}{item.providerPaymentId?` · ID ${item.providerPaymentId}`:''}</small></span><b>{money(item.amountMinor,item.currency)}</b><em className={`payment-status ${item.status}`}>{paymentStatusLabel(item.status)}</em><time>{new Date(item.createdAt).toLocaleString('ru-RU')}</time></article>)}</div>}

/** Отдельный реестр коммерческого доступа, не смешанный с Identity-ролями. */
function AdminSubscriptionsPage(){
  const [data,setData]=useState<SubscriptionPage|null>(null);const [page,setPage]=useState(1);const [status,setStatus]=useState('');const [editing,setEditing]=useState<AdminSubscription|null>(null);const [draft,setDraft]=useState({plan:'free',status:'active',expiresAt:'',extensionDays:0,reason:''});const [error,setError]=useState('');const [busy,setBusy]=useState(false)
  const load=useCallback(async()=>{const query=new URLSearchParams({page:String(page),pageSize:'10'});if(status)query.set('status',status);const response=await fetch(`${API}/api/v1/admin/subscriptions?${query}`,{credentials:'include'});if(!response.ok){setError(await responseMessage(response,'Подписки недоступны'));return}setData(await response.json() as SubscriptionPage);setError('')},[page,status])
  useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const open=(item:AdminSubscription)=>{setEditing(item);setDraft({plan:item.plan,status:item.status,expiresAt:item.expiresAt?item.expiresAt.slice(0,10):'',extensionDays:0,reason:''})}
  const save=async()=>{if(!editing)return;setBusy(true);const response=await fetch(`${API}/api/v1/admin/subscriptions/${editing.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({...draft,expiresAt:draft.expiresAt?new Date(`${draft.expiresAt}T23:59:59Z`).toISOString():null})});if(!response.ok)setError(await responseMessage(response,'Не удалось изменить подписку'));else{setEditing(null);await load()}setBusy(false)}
  const summary=data?.summary
  return <section className="admin-section subscriptions-admin-section" aria-labelledby="admin-subscriptions-title"><AdminPageHeader id="admin-subscriptions-title" title="Подписки"/><ToastSignal kind="error" message={error}/><div className="admin-summary-grid compact-summary"><article><span className="summary-icon"><Check/></span><div><small>Активные</small><strong>{summary?.active??'—'}</strong></div></article><article><span className="summary-icon"><Clock3/></span><div><small>Истекают за 7 дней</small><strong>{summary?.expiringSoon??'—'}</strong></div></article><article><span className="summary-icon"><CalendarClock/></span><div><small>Пробные</small><strong>{summary?.trialing??'—'}</strong></div></article><article><span className="summary-icon"><Ban/></span><div><small>Приостановлены</small><strong>{summary?.suspended??'—'}</strong></div></article></div><section className="admin-card admin-registry"><AdminTabs value={status} onChange={value=>{setStatus(value);setPage(1)}} items={[["","Все"],["active","Активные"],["trialing","Пробные"],["past_due","Просрочены"],["suspended","Заблокированы"],["expired","Истекли"]]}/><div className="admin-data-table subscriptions-table"><div className="admin-data-head"><span>Пользователь</span><span>Тариф</span><span>Статус</span><span>Действует до</span><span/></div>{data?.items.map(item=><article key={item.id}><span><b>{item.displayName||item.userName}</b><small>{item.email}</small></span><b>{planLabel(item.plan)}</b><em className={`payment-status ${item.status}`}>{subscriptionStatusLabel(item.status)}</em><time>{item.expiresAt?new Date(item.expiresAt).toLocaleDateString('ru-RU'):'Бессрочно'}</time><button className="table-action" onClick={()=>open(item)}><Pencil/>Управлять</button></article>)}</div>{data&&data.total>10&&<ProxyPagination page={page} pageSize={10} total={data.total} totalPages={Math.ceil(data.total/10)} onPageChange={setPage} onPageSizeChange={()=>{}}/>}</section>{editing&&<div className="source-editor-backdrop"><section className="source-editor-modal subscription-modal"><div className="source-editor-heading"><div><span className="kicker">РУЧНОЕ УПРАВЛЕНИЕ</span><h2>{editing.displayName||editing.userName}</h2><p>{editing.email}. Каждое изменение сохраняется в неизменяемом журнале аудита.</p></div><button className="icon-button" onClick={()=>setEditing(null)}><X/></button></div><div className="source-editor-grid"><label>Тариф<StyledSelect value={draft.plan} onChange={plan=>setDraft({...draft,plan})} options={[["free","Free"],["pro","Pro"],["unlimited","Unlimited"]]}/></label><label>Статус<StyledSelect value={draft.status} onChange={statusValue=>setDraft({...draft,status:statusValue})} options={[["active","Активна"],["trialing","Пробная"],["past_due","Просрочена"],["canceled","Отменена"],["expired","Истекла"],["suspended","Приостановлена"]]}/></label><label>Действует до<input type="date" value={draft.expiresAt} onChange={e=>setDraft({...draft,expiresAt:e.target.value,extensionDays:0})}/></label><label>Продлить на дней<input type="number" min="0" max="3660" value={draft.extensionDays} onChange={e=>setDraft({...draft,extensionDays:Number(e.target.value)})}/></label></div><label className="modal-wide-label">Причина изменения<textarea maxLength={500} value={draft.reason} onChange={e=>setDraft({...draft,reason:e.target.value})} placeholder="Например: компенсация по обращению #123"/></label><div className="quick-extend"><span>Быстро продлить:</span>{[7,30,90,365].map(days=><button key={days} onClick={()=>setDraft({...draft,extensionDays:days})}>+{days} дней</button>)}</div><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={()=>setEditing(null)}>Отмена</button><button className="primary-admin-button" disabled={busy} onClick={()=>void save()}><ShieldCheck/>{busy?'Сохраняем…':'Применить'}</button></div></section></div>}</section>
}

/** Заголовок колонки управляет серверной сортировкой и явно показывает направление. */
function SortHeader({field,label,sort,order,onChange}:{field:string;label:string;sort:string;order:string;onChange:(field:string,order:string)=>void}){
  const active=sort===field
  return <button className={`sort-header${active?' active':''}`} aria-label={`${label}: сортировка ${active&&order==='asc'?'по возрастанию':'по убыванию'}`} onClick={()=>onChange(field,active&&order==='desc'?'asc':'desc')}>{label}<span aria-hidden="true">{active?(order==='asc'?'↑':'↓'):'↕'}</span></button>
}

/** Единая модалка ручной и контекстной блокировки. */
function AccessBlockModal({initialValue='',onClose,onCreated}:{initialValue?:string;onClose:()=>void;onCreated:()=>void}){
  const [draft,setDraft]=useState({kind:'ip',value:initialValue,reason:'',expiresAt:''});const [busy,setBusy]=useState(false);const [error,setError]=useState('')
  const create=async()=>{setBusy(true);const response=await fetch(`${API}/api/v1/admin/access/rules`,{method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({...draft,expiresAt:draft.expiresAt?new Date(draft.expiresAt).toISOString():null})});if(!response.ok)setError(await responseMessage(response,'Правило не создано'));else onCreated();setBusy(false)}
  return <div className="source-editor-backdrop" onMouseDown={event=>{if(event.target===event.currentTarget)onClose()}}><section className="source-editor-modal" role="dialog" aria-modal="true" aria-labelledby="access-rule-title"><div className="source-editor-heading"><div><span className="kicker">ACCESS RULE</span><h2 id="access-rule-title">Новая блокировка</h2><p>Адрес перестанет получать каталог и выгрузки сразу после сохранения.</p></div><button className="icon-button" aria-label="Закрыть" onClick={onClose}><X/></button></div><ToastSignal kind="error" message={error}/><div className="source-editor-grid"><label>Тип<StyledSelect value={draft.kind} onChange={kind=>setDraft({...draft,kind})} options={[["ip","IP-адрес"],["cidr","Подсеть CIDR"],["user","Пользователь UUID"]]}/></label><label>Значение<input value={draft.value} onChange={e=>setDraft({...draft,value:e.target.value})} placeholder={draft.kind==='cidr'?'203.0.113.0/24':draft.kind==='user'?'UUID пользователя':'203.0.113.10'}/></label><label>Действует до<input type="datetime-local" value={draft.expiresAt} onChange={e=>setDraft({...draft,expiresAt:e.target.value})}/></label></div><label className="modal-wide-label">Причина<textarea required minLength={3} maxLength={500} value={draft.reason} onChange={e=>setDraft({...draft,reason:e.target.value})} placeholder="Например: превышение лимитов выдачи"/></label><div className="source-editor-actions"><span/><button className="secondary-admin-button" onClick={onClose}>Отмена</button><button className="primary-admin-button" disabled={busy||draft.reason.trim().length<3||!draft.value.trim()} onClick={()=>void create()}><Ban/>{busy?'Создаём…':'Заблокировать'}</button></div></section></div>
}

/** Постраничный реестр выдачи, дедуплицированный по каноническому IP. */
function AdminAccessTrafficPage(){
  const [data,setData]=useState<AccessPage|null>(null);const [page,setPage]=useState(1);const [pageSize,setPageSize]=useState(10);const [sort,setSort]=useState('requests');const [order,setOrder]=useState('desc');const [blocking,setBlocking]=useState('');const [error,setError]=useState('')
  const load=useCallback(async()=>{const response=await fetch(`${API}/api/v1/admin/access?page=${page}&pageSize=${pageSize}&sort=${sort}&order=${order}`,{credentials:'include'});if(!response.ok)setError(await responseMessage(response,'Статистика доступа недоступна'));else{setData(await response.json() as AccessPage);setError('')}},[page,pageSize,sort,order]);useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const changeSort=(field:string,nextOrder:string)=>{setSort(field);setOrder(nextOrder);setPage(1)}
  return <div id="access-panel-traffic" role="tabpanel" aria-labelledby="access-tab-traffic"><div className="admin-subsection-heading"><div><span className="kicker">TRAFFIC CONTROL</span><h2>Клиенты выдачи</h2><p>Одна строка на IP независимо от анонимных и авторизованных запросов.</p></div></div><ToastSignal kind="error" message={error}/><div className="admin-summary-grid compact-summary"><article><span className="summary-icon"><Activity/></span><div><small>Запросов</small><strong>{formatNumber(data?.summary.requests)}</strong></div></article><article><span className="summary-icon"><Database/></span><div><small>Выдано адресов</small><strong>{formatNumber(data?.summary.proxyItems)}</strong></div></article><article><span className="summary-icon"><Network/></span><div><small>Уникальных IP</small><strong>{formatNumber(data?.summary.uniqueIps)}</strong></div></article><article><span className="summary-icon"><ShieldOff/></span><div><small>Активных правил</small><strong>{formatNumber(data?.summary.activeRules)}</strong></div></article></div><section className="admin-card admin-registry access-registry"><div className="admin-data-table access-table"><div className="admin-data-head"><SortHeader field="ip" label="IP / аккаунт" sort={sort} order={order} onChange={changeSort}/><SortHeader field="requests" label="Запросов" sort={sort} order={order} onChange={changeSort}/><SortHeader field="proxyItems" label="Прокси" sort={sort} order={order} onChange={changeSort}/><SortHeader field="bytesSent" label="Трафик" sort={sort} order={order} onChange={changeSort}/><SortHeader field="lastSeen" label="Последний" sort={sort} order={order} onChange={changeSort}/><span>Действие</span></div>{data?.items.map(item=><article key={item.ipAddress}><span><b>{item.ipAddress}</b><small>{item.displayName||item.userName||item.email||(item.userId?`Аккаунт ${item.userId.slice(0,8)}`:'Анонимный доступ')}</small></span><b>{formatNumber(item.requests)}</b><span>{formatNumber(item.proxyItems)}</span><span>{formatBytes(item.bytesSent)}</span><time>{timeAgo(item.lastSeenAt)}</time><button className="table-action danger" disabled={item.isBlocked} onClick={()=>setBlocking(item.ipAddress)}><Ban/>{item.isBlocked?'Заблокирован':'Блокировать'}</button></article>)}</div>{data&&<ProxyPagination page={page} pageSize={pageSize} total={data.total} totalPages={Math.max(1,Math.ceil(data.total/pageSize))} onPageChange={setPage} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}</section>{blocking&&<AccessBlockModal initialValue={blocking} onClose={()=>setBlocking('')} onCreated={()=>{setBlocking('');void load()}}/>}</div>
}

/** Правила блокировки вынесены в самостоятельный постраничный раздел. */
function AdminAccessRulesPage(){
  const [data,setData]=useState<AccessRulePage|null>(null);const [page,setPage]=useState(1);const [pageSize,setPageSize]=useState(10);const [sort,setSort]=useState('createdAt');const [order,setOrder]=useState('desc');const [modal,setModal]=useState(false);const [error,setError]=useState('')
  const load=useCallback(async()=>{const response=await fetch(`${API}/api/v1/admin/access/rules?page=${page}&pageSize=${pageSize}&sort=${sort}&order=${order}`,{credentials:'include'});if(!response.ok)setError(await responseMessage(response,'Правила недоступны'));else{setData(await response.json() as AccessRulePage);setError('')}},[page,pageSize,sort,order]);useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const toggle=async(rule:AccessRule)=>{const response=await fetch(`${API}/api/v1/admin/access/rules/${rule.id}`,{method:'PUT',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify({enabled:!rule.enabled,expiresAt:rule.expiresAt??null})});if(response.ok)await load();else setError(await responseMessage(response,'Правило не обновлено'))}
  const changeSort=(field:string,nextOrder:string)=>{setSort(field);setOrder(nextOrder);setPage(1)}
  return <div id="access-panel-rules" role="tabpanel" aria-labelledby="access-tab-rules"><div className="admin-subsection-heading"><div><span className="kicker">ACCESS RULES</span><h2>Блокировки</h2><p>IP, подсети и аккаунты, которым запрещена выдача прокси.</p></div><button className="primary-admin-button" onClick={()=>setModal(true)}><Ban/>Добавить блокировку</button></div><ToastSignal kind="error" message={error}/><section className="admin-card admin-registry rules-registry"><div className="admin-data-table rules-table"><div className="admin-data-head"><SortHeader field="target" label="Цель" sort={sort} order={order} onChange={changeSort}/><span>Причина</span><SortHeader field="createdAt" label="Создано" sort={sort} order={order} onChange={changeSort}/><SortHeader field="expiresAt" label="Срок" sort={sort} order={order} onChange={changeSort}/><SortHeader field="status" label="Состояние" sort={sort} order={order} onChange={changeSort}/></div>{data?.items.length===0&&<div className="empty-state"><ShieldCheck/>Блокировок нет.</div>}{data?.items.map(rule=><article key={rule.id}><span><b>{rule.value}</b><small>{rule.kind.toUpperCase()}</small></span><span>{rule.reason}</span><time>{formatDateTime(rule.createdAt)}</time><time>{rule.expiresAt?formatDateTime(rule.expiresAt):'Бессрочно'}</time><Toggle checked={rule.enabled} onChange={()=>void toggle(rule)} label={rule.enabled?'Активно':'Отключено'}/></article>)}</div>{data&&<ProxyPagination page={page} pageSize={pageSize} total={data.total} totalPages={Math.max(1,Math.ceil(data.total/pageSize))} onPageChange={setPage} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}</section>{modal&&<AccessBlockModal onClose={()=>setModal(false)} onCreated={()=>{setModal(false);void load()}}/>}</div>
}

/** Разделяет выдачу, посещения и правила, не загружая скрытые вкладки. */
function AdminAccessPage(){
  const requested=new URLSearchParams(window.location.search).get('tab');const initialTab=requested==='visitors'||requested==='rules'?requested:'traffic';const [tab,setTab]=useState(initialTab)
  const changeTab=(value:string)=>{const next=value==='visitors'||value==='rules'?value:'traffic';setTab(next);const url=new URL(window.location.href);if(next==='traffic')url.searchParams.delete('tab');else url.searchParams.set('tab',next);window.history.replaceState({},'',`${url.pathname}${url.search}${url.hash}`)}
  return <section className="admin-section access-admin-section" aria-labelledby="admin-access-title"><AdminPageHeader id="admin-access-title" title="Доступ и IP"/><AdminTabs value={tab} onChange={changeTab} ariaLabel="Разделы доступа и IP" idPrefix="access" items={[["traffic","Клиенты выдачи"],["visitors","Посетители сайта"],["rules","Блокировки"]]}/>{tab==='traffic'?<AdminAccessTrafficPage/>:tab==='rules'?<AdminAccessRulesPage/>:<AdminSiteVisitorsPage/>}</section>
}

/** Посетители и точная история переходов с независимой серверной пагинацией. */
function AdminSiteVisitorsPage(){
  const [view,setView]=useState('visitors');const [data,setData]=useState<SiteVisitorPage|null>(null);const [history,setHistory]=useState<SiteVisitPage|null>(null);const [page,setPage]=useState(1);const [pageSize,setPageSize]=useState(10);const [sort,setSort]=useState('lastSeen');const [order,setOrder]=useState('desc');const [blocking,setBlocking]=useState('');const [error,setError]=useState('');const [loading,setLoading]=useState(false)
  const load=useCallback(async()=>{setLoading(true);const endpoint=view==='history'?'visitors/history':'visitors';const response=await fetch(`${API}/api/v1/admin/access/${endpoint}?page=${page}&pageSize=${pageSize}&sort=${sort}&order=${order}`,{credentials:'include'});if(!response.ok)setError(await responseMessage(response,'Статистика посещений недоступна'));else{if(view==='history')setHistory(await response.json() as SiteVisitPage);else setData(await response.json() as SiteVisitorPage);setError('')}setLoading(false)},[view,page,pageSize,sort,order]);useEffect(()=>{const timer=window.setTimeout(()=>void load(),0);return()=>window.clearTimeout(timer)},[load])
  const changeView=(value:string)=>{setView(value);setPage(1);setSort(value==='history'?'visitedAt':'lastSeen');setOrder('desc')};const changeSort=(field:string,nextOrder:string)=>{setSort(field);setOrder(nextOrder);setPage(1)};const summary=data?.summary
  return <div id="access-panel-visitors" className="access-visitors-section" role="tabpanel" aria-labelledby="access-tab-visitors"><div className="admin-subsection-heading"><div><span className="kicker">SITE ANALYTICS</span><h2>Посетители сайта</h2><p>First-party статистика без рекламных cookies и query-параметров.</p></div><button className="icon-button" aria-label="Обновить статистику посещений" disabled={loading} onClick={()=>void load()}><RefreshCw className={loading?'spin':''}/></button></div><ToastSignal kind="error" message={error}/>{view==='visitors'&&<div className="admin-summary-grid compact-summary visitor-summary"><article><span className="summary-icon"><MousePointerClick/></span><div><small>Просмотры</small><strong>{formatNumber(summary?.pageViews)}</strong><p>загрузок страниц</p></div></article><article><span className="summary-icon"><Globe2/></span><div><small>Посетители</small><strong>{formatNumber(summary?.uniqueVisitors)}</strong><p>уникальных IP</p></div></article><article><span className="summary-icon"><User/></span><div><small>С аккаунтом</small><strong>{formatNumber(summary?.authenticatedVisitors)}</strong><p>авторизованных</p></div></article><article><span className="summary-icon"><Clock3/></span><div><small>За 24 часа</small><strong>{formatNumber(summary?.active24Hours)}</strong><p>активных IP</p></div></article></div>}<AdminTabs value={view} onChange={changeView} ariaLabel="Представление посещений" items={[["visitors","Сводка по IP"],["history","История переходов"]]}/>{view==='visitors'?<section className="admin-card admin-registry site-visitors-card"><div className="admin-data-table visitor-table"><div className="admin-data-head"><SortHeader field="ip" label="IP / аккаунт" sort={sort} order={order} onChange={changeSort}/><SortHeader field="pageViews" label="Просмотры" sort={sort} order={order} onChange={changeSort}/><SortHeader field="pages" label="Страницы" sort={sort} order={order} onChange={changeSort}/><SortHeader field="firstSeen" label="Первый визит" sort={sort} order={order} onChange={changeSort}/><SortHeader field="lastSeen" label="Последний визит" sort={sort} order={order} onChange={changeSort}/><span>Действие</span></div>{data?.items.map(item=><article key={item.ipAddress}><span><b>{item.ipAddress}</b><small>{item.displayName||item.userName||item.email||'Анонимный посетитель'}</small></span><b>{formatNumber(item.pageViews)}</b><span>{formatNumber(item.pages)}</span><time>{formatDateTime(item.firstSeenAt)}</time><time>{timeAgo(item.lastSeenAt)}</time><button className="table-action danger" disabled={item.isBlocked} onClick={()=>setBlocking(item.ipAddress)}><Ban/>{item.isBlocked?'Заблокирован':'Блокировать'}</button></article>)}</div>{data&&<ProxyPagination page={page} pageSize={pageSize} total={data.total} totalPages={Math.max(1,Math.ceil(data.total/pageSize))} onPageChange={setPage} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}<p className="privacy-note"><ShieldCheck/>IP и события удаляются через {data?.retentionDays??90} дней. Global Privacy Control учитывается.</p></section>:<section className="admin-card admin-registry visit-history-card"><div className="admin-data-table visit-history-table"><div className="admin-data-head"><SortHeader field="visitedAt" label="Дата и время" sort={sort} order={order} onChange={changeSort}/><SortHeader field="ip" label="IP / аккаунт" sort={sort} order={order} onChange={changeSort}/><SortHeader field="page" label="Страница" sort={sort} order={order} onChange={changeSort}/></div>{history?.items.map(item=><article key={item.id}><time>{formatDateTime(item.visitedAt)}</time><span><b>{item.ipAddress}</b><small>{item.displayName||item.userName||item.email||'Анонимный посетитель'}</small></span><span>{sitePageLabel(item.page)}</span></article>)}</div>{history&&<ProxyPagination page={page} pageSize={pageSize} total={history.total} totalPages={Math.max(1,Math.ceil(history.total/pageSize))} onPageChange={setPage} onPageSizeChange={size=>{setPageSize(size);setPage(1)}}/>}<p className="privacy-note"><ShieldCheck/>История хранится {history?.retentionDays??90} дней и содержит только безопасные коды страниц.</p></section>}{blocking&&<AccessBlockModal initialValue={blocking} onClose={()=>setBlocking('')} onCreated={()=>{setBlocking('');void load()}}/>}</div>
}

/** Пагинация повторяет серверный UX RMS: размер, страницы, быстрый переход и итог. */
function ProxyPagination({page, pageSize, total, totalPages, onPageChange, onPageSizeChange}: {page: number; pageSize: number; total: number; totalPages: number; onPageChange: (page: number) => void; onPageSizeChange: (size: number) => void}) {
  const { t } = useI18n()
  const [jump, setJump] = useState('')
  const pages = paginationWindow(page, totalPages)
  const go = (next: number) => onPageChange(Math.min(totalPages, Math.max(1, next)))
  const showQuickJump = totalPages > 7
  return <nav className={`pagination${showQuickJump ? '' : ' pagination-compact'}`} aria-label={t('quickJump')}>
    <div className="page-sizes"><span>{t('show')}</span>{[10, 25, 50, 100].map(size => <button key={size} className={pageSize === size ? 'active' : ''} aria-pressed={pageSize === size} onClick={() => onPageSizeChange(size)}>{size}</button>)}</div>
    <div className="page-controls"><button aria-label={t('previousPage')} disabled={page === 1} onClick={() => go(page - 1)}>←</button>{pages.map((item, index) => item === '…' ? <span key={`ellipsis-${index}`}>…</span> : <button key={item} className={item === page ? 'active' : ''} aria-current={item === page ? 'page' : undefined} onClick={() => go(item)}>{item}</button>)}<button aria-label={t('nextPage')} disabled={page === totalPages} onClick={() => go(page + 1)}>→</button></div>
    {showQuickJump && <form className="page-jump" aria-label={t('quickJump')} onSubmit={event => { event.preventDefault(); const value = Number(jump); if (Number.isInteger(value) && value > 0) { go(value); setJump('') } }}><input aria-label={t('pageNumber')} inputMode="numeric" min={1} max={totalPages} type="number" placeholder="Стр." value={jump} onChange={event => setJump(event.target.value)}/><span aria-hidden="true">/ {totalPages}</span><button type="submit" aria-label={t('goToPage')}><ArrowRight size={14}/></button></form>}
    <p>{t('page',{page,pages:totalPages,total:formatNumber(total)})}</p>
  </nav>
}

function paginationWindow(page: number, totalPages: number): (number | '…')[] {
  if (totalPages <= 7) return Array.from({ length: totalPages }, (_, index) => index + 1)
  if (page <= 4) return [1, 2, 3, 4, 5, '…', totalPages]
  if (page >= totalPages - 3) return [1, '…', totalPages - 4, totalPages - 3, totalPages - 2, totalPages - 1, totalPages]
  return [1, '…', page - 1, page, page + 1, '…', totalPages]
}

function Metric({icon, label, value, note}: {icon: React.ReactNode; label: string; value: string; note: string}) { return <article className="metric"><div className="metric-icon">{icon}</div><div><span>{label}</span><strong>{value}</strong><small>{note}</small></div></article> }
function formatNumber(value?: number) { return value === undefined ? '—' : value.toLocaleString(currentLocale()) }
function formatBytes(value?: number) {
  if (value === undefined) return '—'
  if (value < 1024) return `${value} Б`
  if (value < 1024 ** 2) return `${(value / 1024).toLocaleString(currentLocale(), { maximumFractionDigits: 1 })} KB`
  if (value < 1024 ** 3) return `${(value / 1024 ** 2).toLocaleString(currentLocale(), { maximumFractionDigits: 1 })} MB`
  return `${(value / 1024 ** 3).toLocaleString(currentLocale(), { maximumFractionDigits: 1 })} GB`
}
function formatDateTime(value:string){return new Intl.DateTimeFormat(currentLocale(),{dateStyle:'medium',timeStyle:'medium'}).format(new Date(value))}
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
function backupDelivery(run: BackupRun) {const delivered=[];if(run.sentToObjectStorage)delivered.push('S3');if(run.sentToTelegram)delivered.push('Telegram');if(delivered.length)return `доставлен: ${delivered.join(' + ')}`;if(run.objectStorageConfigured||run.telegramConfigured)return 'внешняя доставка не подтверждена';return 'только локально'}
function planLabel(plan?: string) { return plan === 'unlimited' ? 'Unlimited' : plan === 'pro' ? 'Pro' : 'Free' }
function money(minor:number,currency:string){return new Intl.NumberFormat(currentLocale(),{style:'currency',currency}).format(minor/100)}
function calculateSubscriptionPrice(dailyMinor:number,days:number,discount:number){return Math.ceil(dailyMinor*days*(100-discount)/100/100)*100}
function subscriptionPeriodName(days:number){return days===1?'День':days===7?'Неделя':days===30?'Месяц':days===90?'Квартал':days===180?'Полгода':days===365?'Год':`${days} дней`}
function providerLabel(provider:string){return ({yookassa:'ЮKassa',yoomoney:'ЮMoney',cloudpayments:'CloudPayments',robokassa:'Robokassa',tbank:'Т-Банк',stripe:'Stripe',cryptomus:'Cryptomus',nowpayments:'NOWPayments'} as Record<string,string>)[provider]??provider}
function paymentStatusLabel(status:string){return ({pending:'Ожидает оплаты',paid:'Оплачен',failed:'Ошибка',canceled:'Отменён',refunded:'Возвращён'} as Record<string,string>)[status]??status}
function localizedSubscriptionStatusLabel(status:string,language:Language){const labels:Record<Language,Record<string,string>>={ru:{active:'Активна',trialing:'Пробная',suspended:'Приостановлена',expired:'Истекла'},en:{active:'Active',trialing:'Trial',suspended:'Suspended',expired:'Expired'},de:{active:'Aktiv',trialing:'Testphase',suspended:'Pausiert',expired:'Abgelaufen'},fr:{active:'Actif',trialing:'Essai',suspended:'Suspendu',expired:'Expiré'},zh:{active:'有效',trialing:'试用',suspended:'已暂停',expired:'已过期'}};return labels[language][status.toLowerCase()]??status}
function subscriptionStatusLabel(status:string){return ({active:'Активна',trialing:'Пробная',past_due:'Просрочена',canceled:'Отменена',expired:'Истекла',suspended:'Приостановлена'} as Record<string,string>)[status]??status}
function sitePageLabel(page:string){return ({home:'Главная',login:'Вход',register:'Регистрация','forgot-password':'Восстановление пароля','reset-password':'Новый пароль',account:'Личный кабинет','admin-overview':'Админка: обзор','admin-operations':'Админка: операции','admin-sources':'Админка: источники','admin-proxies':'Админка: прокси','admin-backups':'Админка: резервные копии','admin-users':'Админка: пользователи','admin-payments':'Админка: оплата','admin-telegram':'Админка: Telegram','admin-subscriptions':'Админка: подписки','admin-access':'Админка: доступ и IP',other:'Другая страница'} as Record<string,string>)[page]??page}
function adminProxyStatusLabel(status:AdminProxyStatus){return ({Alive:'Рабочий',Pending:'Ожидает',Dead:'Нерабочий'} as Record<AdminProxyStatus,string>)[status]}
function label(protocol: Protocol) { return ({Http: 'HTTP', Https: 'HTTPS', Socks4: 'SOCKS4', Socks5: 'SOCKS5'})[protocol] }
function countryName(code:string){return new Intl.DisplayNames([currentLocale()], { type: 'region' }).of(code.toUpperCase())??code.toUpperCase()}
function CountryFlag({code}:{code:string}){const normalized=/^[a-z]{2}$/i.test(code)?code.toLowerCase():'';return normalized?<img className={`country-flag flag-${normalized}`} src={`/flags/${normalized}.svg`} alt="" loading="lazy" decoding="async"/>:<Globe2 className="country-flag country-flag-unknown" aria-hidden="true"/>}
function timeAgo(value: string) { const sec = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000)); const formatter=new Intl.RelativeTimeFormat(currentLocale(),{numeric:'auto'}); if(sec<60)return formatter.format(-sec,'second');if(sec<3600)return formatter.format(-Math.floor(sec/60),'minute');if(sec<86400)return formatter.format(-Math.floor(sec/3600),'hour');return formatter.format(-Math.floor(sec/86400),'day') }
function timeUntil(value: string) { const sec = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000)); if (sec < 60) return `через ${sec} сек`; if (sec < 3600) return `через ${Math.ceil(sec / 60)} мин`; return `через ${Math.ceil(sec / 3600)} ч` }

async function responseMessage(response: Response, fallback: string) {
  try {
    const problem = await response.json() as { title?: string; detail?: string; message?: string; error?: string }
    const message = problem.detail || problem.message || problem.title
    return [message, problem.error && problem.error !== message ? problem.error : ''].filter(Boolean).join(' · ') || fallback
  } catch { return fallback }
}
