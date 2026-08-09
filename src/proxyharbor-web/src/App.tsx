import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Activity, ArrowDownToLine, Check, Clock3, Database, Gauge, KeyRound, Network, Play, RefreshCw, Server, ShieldCheck, Wifi, X } from 'lucide-react'

type Protocol = 'Http' | 'Https' | 'Socks4' | 'Socks5'
type Proxy = { host: string; port: number; protocol: Protocol; url: string; latencyMs: number; successRate: number; exitIp?: string; lastCheckedAt: string }
type Stats = { alive: number; staleAlive: number; pending: number; dead: number; dueForCheck: number; scheduledChecks: number; averageLatencyMs: number | null; sources: number; failingSources: number; repeatedlyFailingSources: number; byProtocol: { protocol: Protocol; count: number }[]; lastRun?: { startedAt: string; candidatesFound: number; newProxies: number; status: string } }
type Source = { id: string; name: string; url: string; defaultProtocol: Protocol; enabled: boolean; priority: number; lastItemCount: number; lastFetchedAt?: string; lastSucceededAt?: string; nextFetchAt?: string; consecutiveFailures: number; lastError?: string; isBuiltIn: boolean; provider?: string; catalogRank?: number }
type CollectionRun = { id: string; startedAt: string; finishedAt?: string; sourcesProcessed: number; sourcesSucceeded: number; sourcesFailed: number; sourcesSkipped: number; candidatesFound: number; newProxies: number; status: string; error?: string }
type BackupRun = { id: string; startedAt: string; finishedAt?: string; status: string; fileName?: string; sizeBytes: number; telegramConfigured: boolean; sentToTelegram: boolean; error?: string }
type SourceCatalogSnapshot = { expectedSources: number; presentSources: number; enabledSources: number; healthySources: number; failingSources: number; neverAuditedSources: number; expectedProviders: number; presentProviders: number; enabledProviders: number; isComplete: boolean; isHealthy: boolean }
type Diagnostics = {
  serverTime: string
  databaseBytes: number
  validationQueue?: { total: number; leased: number; neverChecked: number; due: number; scheduled: number; repeatedlyFailing: number }
  sourceCatalog?: SourceCatalogSnapshot
  recentRuns: CollectionRun[]
  recentBackups: BackupRun[]
}

const API = import.meta.env.VITE_API_URL ?? ''
const protocols: Protocol[] = ['Http', 'Https', 'Socks4', 'Socks5']

/** Основная панель: публичный каталог и компактное администрирование в одном интерфейсе. */
export default function App() {
  const [stats, setStats] = useState<Stats | null>(null)
  const [proxies, setProxies] = useState<Proxy[]>([])
  const [protocol, setProtocol] = useState<Protocol | 'All'>('All')
  const [maxLatency, setMaxLatency] = useState(2000)
  const [loading, setLoading] = useState(true)
  const [apiError, setApiError] = useState('')
  const [adminOpen, setAdminOpen] = useState(false)
  const [adminKey, setAdminKey] = useState(() => sessionStorage.getItem('proxyharbor-admin-key') ?? '')
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
  const autoLoginAttemptedRef = useRef(false)

  const load = useCallback(async () => {
    try {
      const query = new URLSearchParams({ pageSize: '100', maxLatencyMs: String(maxLatency) })
      if (protocol !== 'All') query.set('protocol', protocol)
      const [statsResponse, proxyResponse] = await Promise.all([
        fetch(`${API}/api/v1/stats`), fetch(`${API}/api/v1/proxies?${query}`),
      ])
      if (!statsResponse.ok || !proxyResponse.ok) throw new Error('API пока недоступен')
      setStats(await statsResponse.json())
      setProxies((await proxyResponse.json()).items)
      setApiError('')
    } catch (reason) { setApiError(reason instanceof Error ? reason.message : 'Ошибка загрузки') }
    finally { setLoading(false) }
  }, [protocol, maxLatency])

  useEffect(() => {
    const initialLoad = window.setTimeout(() => void load(), 0)
    const refreshTimer = window.setInterval(load, 15_000)
    return () => {
      window.clearTimeout(initialLoad)
      window.clearInterval(refreshTimer)
    }
  }, [load])

  const loadAdminData = useCallback(async () => {
    if (!adminKey) {
      setAdminAuthenticated(false)
      return
    }
    setAdminLoading(true)
    try {
      const requestOptions = { headers: { 'X-Admin-Key': adminKey } }
      const [sourcesResponse, diagnosticsResponse] = await Promise.all([
        fetch(`${API}/api/v1/admin/sources`, requestOptions),
        fetch(`${API}/api/v1/admin/diagnostics`, requestOptions),
      ])
      const unauthorizedResponse = [sourcesResponse, diagnosticsResponse].find(response => response.status === 401)
      if (unauthorizedResponse) {
        sessionStorage.removeItem('proxyharbor-admin-key')
        setAdminAuthenticated(false)
        setSources([])
        setDiagnostics(null)
        throw new Error(await responseMessage(unauthorizedResponse, 'Неверный ключ администратора'))
      }
      if (!sourcesResponse.ok) throw new Error(await responseMessage(sourcesResponse, 'Неверный ключ администратора'))
      if (!diagnosticsResponse.ok) throw new Error(await responseMessage(diagnosticsResponse, 'Диагностика недоступна'))
      sessionStorage.setItem('proxyharbor-admin-key', adminKey)
      const [sourceRows, diagnosticSnapshot] = await Promise.all([sourcesResponse.json(), diagnosticsResponse.json()])
      setSources(sourceRows)
      setDiagnostics(diagnosticSnapshot)
      setAdminAuthenticated(true)
      setAdminError('')
    } catch (reason) {
      setAdminError(reason instanceof Error ? reason.message : 'Не удалось открыть административную консоль')
    } finally { setAdminLoading(false) }
  }, [adminKey])

  useEffect(() => {
    if (!adminOpen) {
      autoLoginAttemptedRef.current = false
      return
    }
    if (autoLoginAttemptedRef.current) return
    autoLoginAttemptedRef.current = true
    if (!adminKey) return
    const initialLoad = window.setTimeout(() => void loadAdminData(), 0)
    return () => window.clearTimeout(initialLoad)
  }, [adminOpen, adminKey, loadAdminData])

  useEffect(() => {
    if (!adminOpen || !adminAuthenticated) return
    const refreshTimer = window.setInterval(() => void loadAdminData(), 15_000)
    return () => window.clearInterval(refreshTimer)
  }, [adminOpen, adminAuthenticated, loadAdminData])

  const openAdmin = (event: React.MouseEvent<HTMLButtonElement>) => {
    lastAdminTriggerRef.current = event.currentTarget
    setAdminOpen(true)
  }
  const closeAdmin = useCallback(() => setAdminOpen(false), [])

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
      if (event.shiftKey && document.activeElement === first) {
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
    if (!adminAuthenticated) return
    setAction(name)
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/${name}`, { method: 'POST', headers: { 'X-Admin-Key': adminKey } })
      if (!response.ok) {
        if (response.status === 401) { sessionStorage.removeItem('proxyharbor-admin-key'); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Административная операция не выполнена'))
      }
      await Promise.all([load(), loadAdminData()])
    } catch (reason) { setAdminError(reason instanceof Error ? reason.message : 'Административная операция не выполнена') }
    finally { setAction('') }
  }

  const saveSource = async (event: React.FormEvent) => {
    event.preventDefault()
    if (!adminAuthenticated) return
    setSourceBusy('new')
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/sources`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
        body: JSON.stringify({ ...sourceDraft, enabled: true }),
      })
      if (!response.ok) {
        if (response.status === 401) { sessionStorage.removeItem('proxyharbor-admin-key'); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Не удалось добавить источник'))
      }
      setSourceDraft({ name: '', url: '', protocol: 'Http', priority: 100 })
      await loadAdminData()
    } catch (reason) { setAdminError(reason instanceof Error ? reason.message : 'Не удалось добавить источник') }
    finally { setSourceBusy('') }
  }

  const toggleSource = async (source: Source) => {
    if (!adminAuthenticated) return
    setSourceBusy(source.id)
    setAdminError('')
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
        body: JSON.stringify({ name: source.name, url: source.url, protocol: source.defaultProtocol, priority: source.priority, enabled: !source.enabled }),
      })
      if (!response.ok) {
        if (response.status === 401) { sessionStorage.removeItem('proxyharbor-admin-key'); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Не удалось изменить состояние источника'))
      }
      await loadAdminData()
    } catch (reason) { setAdminError(reason instanceof Error ? reason.message : 'Не удалось изменить состояние источника') }
    finally { setSourceBusy('') }
  }

  const removeSource = async (source: Source) => {
    if (!adminAuthenticated) return
    if (!window.confirm(`Удалить или отключить источник «${source.name}»?`)) return
    setSourceBusy(source.id)
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, { method: 'DELETE', headers: { 'X-Admin-Key': adminKey } })
      if (!response.ok) {
        if (response.status === 401) { sessionStorage.removeItem('proxyharbor-admin-key'); setAdminAuthenticated(false) }
        throw new Error(await responseMessage(response, 'Не удалось удалить источник'))
      }
      await loadAdminData()
    } catch (reason) { setAdminError(reason instanceof Error ? reason.message : 'Не удалось удалить источник') }
    finally { setSourceBusy('') }
  }

  const protocolCounts = useMemo(() => Object.fromEntries(stats?.byProtocol.map(x => [x.protocol, x.count]) ?? []), [stats])
  const freshness = stats?.lastRun?.startedAt ? timeAgo(stats.lastRun.startedAt) : 'ожидается'
  const latestCollection = diagnostics?.recentRuns[0]
  const latestBackup = diagnostics?.recentBackups[0]

  return <div className="app-shell">
    <header aria-hidden={adminOpen || undefined}>
      <a className="brand" href="#top" aria-label="ProxyHarbor — наверх"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
      <nav><a href="#catalog">Каталог</a><a href="#api">API</a><button className="admin-link" onClick={openAdmin}><KeyRound size={15}/> Управление</button></nav>
      <button className="mobile-admin" aria-label="Открыть управление" onClick={openAdmin}><KeyRound size={17}/></button>
      <div className={`live-pill ${apiError ? 'offline' : ''}`} aria-live="polite"><span/> {loading ? 'проверка…' : apiError ? 'API недоступен' : 'система активна'}</div>
    </header>

    <main id="top" aria-hidden={adminOpen || undefined}>
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
        <Metric icon={<Database/>} label="Источников" value={formatNumber(stats?.sources)} note={stats?.failingSources ? `${stats.failingSources} требуют внимания` : 'все источники стабильны'}/>
        <Metric icon={<Clock3/>} label="Готовы к проверке" value={formatNumber(stats?.dueForCheck)} note={`${formatNumber(stats?.scheduledChecks)} запланировано позже`}/>
      </section>

      <section id="catalog" className="catalog">
        <div className="section-heading"><div><span className="kicker">LIVE CATALOG</span><h2>Лучшие прямо сейчас</h2></div><p>Автообновление каждые 15 секунд</p></div>
        <div className="filters"><div className="tabs"><button className={protocol === 'All' ? 'active' : ''} onClick={() => setProtocol('All')}>Все</button>{protocols.map(x => <button key={x} className={protocol === x ? 'active' : ''} onClick={() => setProtocol(x)}>{label(x)}</button>)}</div><label>до <b>{maxLatency} мс</b><input type="range" min="200" max="5000" step="100" value={maxLatency} onChange={e => setMaxLatency(Number(e.target.value))}/></label></div>
        {apiError && <div className="error-banner" role="alert"><X size={17}/>{apiError}<button onClick={() => { setApiError(''); setLoading(true); void load() }}>повторить</button></div>}
        <div className="proxy-table" aria-busy={loading}>
          <div className="table-row table-head"><span>Адрес</span><span>Протокол</span><span>Задержка</span><span>Надёжность</span><span>Проверен</span></div>
          {loading ? <div className="empty"><RefreshCw className="spin"/> Загружаем свежий каталог…</div> : proxies.length === 0 ? <div className="empty"><Server/> Живые прокси появятся после первого цикла проверки.</div> : proxies.map(proxy => <div className="table-row" key={proxy.url}><code>{proxy.host}<i>:</i>{proxy.port}</code><span className={`badge ${proxy.protocol.toLowerCase()}`}>{label(proxy.protocol)}</span><span className="latency"><i className={proxy.latencyMs < 800 ? 'fast' : proxy.latencyMs < 1800 ? 'medium' : 'slow'}/>{proxy.latencyMs} мс</span><span>{proxy.successRate}%</span><span>{timeAgo(proxy.lastCheckedAt)}</span></div>)}
        </div>
      </section>

      <section id="api" className="api-panel"><div><span className="kicker">ONE-CLICK EXPORT</span><h2>Забирайте как удобно</h2><p>Фильтруйте через API или скачивайте готовый список. Выдача содержит только прокси со статусом Alive.</p></div><div className="export-grid">{['json','xml','txt','csv'].map(format => <a key={format} href={`${API}/api/v1/export/${format}`}><span>.{format}</span><ArrowDownToLine size={18}/></a>)}</div><div className="endpoint"><span>GET</span><code>/api/v1/proxies?protocol=Socks5&amp;maxLatencyMs=1000</code></div></section>
    </main>

    <footer aria-hidden={adminOpen || undefined}><div className="brand"><span className="brand-mark"><Network size={18}/></span><span>Proxy<span>Harbor</span></span></div><p>Используйте публичные прокси ответственно и в рамках закона.</p><span>© {new Date().getFullYear()}</span></footer>

    {adminOpen && <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && closeAdmin()}>
      <section ref={adminDialogRef} className="admin-modal" role="dialog" aria-modal="true" aria-labelledby="admin-title">
        <button className="close" aria-label="Закрыть" onClick={closeAdmin}><X/></button>
        <span className="kicker">ADMIN CONSOLE</span><h2 id="admin-title">Управление сбором</h2><p>Ключ хранится только до закрытия вкладки.</p>
        <div className="key-input"><KeyRound size={18}/><input ref={adminKeyRef} type="password" aria-label="Ключ администратора" placeholder="X-Admin-Key" value={adminKey} onChange={e => { setAdminKey(e.target.value); setAdminAuthenticated(false); setAdminError(''); setSources([]); setDiagnostics(null) }}/><button onClick={loadAdminData} disabled={adminLoading}>{adminLoading ? 'Проверяем…' : 'Войти'}</button></div>
        {adminError && <div className="admin-notice" role="alert"><X size={16}/>{adminError}</div>}
        <div className="admin-actions">
          <button onClick={() => runAdminAction('collect')} disabled={!adminAuthenticated || !!action}><Play/> {action === 'collect' ? 'Собираем…' : 'Запустить сбор'}</button>
          <button onClick={() => runAdminAction('validate')} disabled={!adminAuthenticated || !!action}><Check/> {action === 'validate' ? 'Проверяем…' : 'Проверить пакет'}</button>
          <button onClick={() => runAdminAction('backup')} disabled={!adminAuthenticated || !!action}><Database/> {action === 'backup' ? 'Копируем…' : 'Создать backup'}</button>
        </div>
        {adminAuthenticated && <section className="admin-diagnostics" aria-label="Диагностика сервиса">
          <div className="diagnostics-heading"><h3>Диагностика</h3><button aria-label="Обновить диагностику" onClick={loadAdminData} disabled={adminLoading}><RefreshCw className={adminLoading ? 'spin' : ''}/></button></div>
          <div className="diagnostic-grid">
            <article><span>Очередь проверки</span><strong>{formatNumber(diagnostics?.validationQueue?.due)}</strong><small>{formatNumber(diagnostics?.validationQueue?.neverChecked)} ещё не проверено · {formatNumber(diagnostics?.validationQueue?.leased)} арендовано</small></article>
            <article><span>Последний сбор</span><strong className={statusClass(latestCollection?.status)}>{statusLabel(latestCollection?.status)}</strong><small>{latestCollection ? `${formatNumber(latestCollection.candidatesFound)} кандидатов · ${timeAgo(latestCollection.startedAt)}` : 'Циклов пока нет'}</small></article>
            <article><span>Последний backup</span><strong className={statusClass(latestBackup?.status)}>{statusLabel(latestBackup?.status)}</strong><small>{latestBackup ? `${formatBytes(latestBackup.sizeBytes)} · ${backupDelivery(latestBackup)}` : 'Backup ещё не создавался'}</small></article>
            <article><span>Размер PostgreSQL</span><strong>{formatBytes(diagnostics?.databaseBytes)}</strong><small>{formatNumber(diagnostics?.validationQueue?.total)} известных прокси</small></article>
            <article aria-label="Состояние встроенного каталога"><span>Встроенный каталог</span><strong className={catalogStatusClass(diagnostics?.sourceCatalog)}>{diagnostics?.sourceCatalog ? `${diagnostics.sourceCatalog.enabledSources}/${diagnostics.sourceCatalog.expectedSources}` : '—'}</strong><small>{diagnostics?.sourceCatalog ? `${diagnostics.sourceCatalog.enabledProviders}/${diagnostics.sourceCatalog.expectedProviders} провайдеров · ${diagnostics.sourceCatalog.healthySources} прошли аудит` : 'Снимок недоступен'}</small></article>
          </div>
          <div className="diagnostic-history">
            <div><h4>Последние сборы</h4>{diagnostics?.recentRuns.slice(0, 4).map(run => <article key={run.id} title={run.error}><span><i className={statusClass(run.status)}/>{timeAgo(run.startedAt)}</span><small>{formatNumber(run.sourcesSucceeded)}/{formatNumber(run.sourcesProcessed)} источников · +{formatNumber(run.newProxies)}</small></article>)}{diagnostics?.recentRuns.length === 0 && <p>Истории пока нет.</p>}</div>
            <div><h4>История backup</h4>{diagnostics?.recentBackups.slice(0, 4).map(run => <article key={run.id} title={run.error}><span><i className={statusClass(run.status)}/>{run.fileName ?? timeAgo(run.startedAt)}</span><small>{formatBytes(run.sizeBytes)} · {backupDelivery(run)}</small></article>)}{diagnostics?.recentBackups.length === 0 && <p>Истории пока нет.</p>}</div>
          </div>
        </section>}
        <h3>Добавить источник</h3>
        <form className="source-form" onSubmit={saveSource}>
          <input required minLength={2} maxLength={120} aria-label="Название источника" placeholder="Название" value={sourceDraft.name} onChange={e => setSourceDraft({...sourceDraft, name: e.target.value})}/>
          <input required type="url" maxLength={2048} pattern="https://.*" aria-label="HTTPS URL источника" placeholder="https://example.org/proxies.txt" value={sourceDraft.url} onChange={e => setSourceDraft({...sourceDraft, url: e.target.value})}/>
          <select aria-label="Протокол источника" value={sourceDraft.protocol} onChange={e => setSourceDraft({...sourceDraft, protocol: e.target.value as Protocol})}>{protocols.map(item => <option key={item} value={item}>{label(item)}</option>)}</select>
          <input type="number" min={-10000} max={10000} aria-label="Приоритет источника" value={sourceDraft.priority} onChange={e => setSourceDraft({...sourceDraft, priority: Number(e.target.value)})}/>
          <button type="submit" disabled={!adminAuthenticated || !!sourceBusy}>{sourceBusy === 'new' ? 'Добавляем…' : 'Добавить'}</button>
        </form>
        <h3>Источники <span>{sources.length}</span></h3>
        <div className="source-list">{sources.map(source => <article key={source.id}>
          <div><b>{source.name}</b><small>{source.defaultProtocol} · {source.lastItemCount.toLocaleString('ru-RU')} адресов{source.consecutiveFailures > 0 ? ` · сбоев подряд: ${source.consecutiveFailures}` : ''}{source.nextFetchAt ? ` · повтор ${timeUntil(source.nextFetchAt)}` : ''}</small></div>
          <div className="source-controls"><span title={source.isBuiltIn ? `Встроенный источник · ${source.provider} · ранг ${source.catalogRank}` : 'Пользовательский источник'} className="source-kind">{source.isBuiltIn ? source.provider : 'свой'}</span><span title={source.lastError} className={source.lastError ? 'source-error' : 'source-ok'}>{source.lastError ? 'ошибка' : source.enabled ? 'активен' : 'пауза'}</span><button disabled={sourceBusy === source.id} onClick={() => toggleSource(source)}>{source.enabled ? 'Пауза' : 'Включить'}</button><button className="danger" disabled={sourceBusy === source.id} onClick={() => removeSource(source)}>Удалить</button></div>
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
