import { useCallback, useEffect, useMemo, useState } from 'react'
import { Activity, ArrowDownToLine, Check, Clock3, Database, Gauge, KeyRound, Network, Play, RefreshCw, Server, ShieldCheck, Wifi, X } from 'lucide-react'

type Protocol = 'Http' | 'Https' | 'Socks4' | 'Socks5'
type Proxy = { host: string; port: number; protocol: Protocol; url: string; latencyMs: number; successRate: number; exitIp?: string; lastCheckedAt: string }
type Stats = { alive: number; staleAlive: number; pending: number; dead: number; dueForCheck: number; scheduledChecks: number; averageLatencyMs: number | null; sources: number; failingSources: number; repeatedlyFailingSources: number; byProtocol: { protocol: Protocol; count: number }[]; lastRun?: { startedAt: string; candidatesFound: number; newProxies: number; status: string } }
type Source = { id: string; name: string; url: string; defaultProtocol: Protocol; enabled: boolean; priority: number; lastItemCount: number; lastFetchedAt?: string; lastSucceededAt?: string; consecutiveFailures: number; lastError?: string }

const API = import.meta.env.VITE_API_URL ?? ''
const protocols: Protocol[] = ['Http', 'Https', 'Socks4', 'Socks5']

/** Основная панель: публичный каталог и компактное администрирование в одном интерфейсе. */
export default function App() {
  const [stats, setStats] = useState<Stats | null>(null)
  const [proxies, setProxies] = useState<Proxy[]>([])
  const [protocol, setProtocol] = useState<Protocol | 'All'>('All')
  const [maxLatency, setMaxLatency] = useState(2000)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')
  const [adminOpen, setAdminOpen] = useState(false)
  const [adminKey, setAdminKey] = useState(() => sessionStorage.getItem('proxyharbor-admin-key') ?? '')
  const [sources, setSources] = useState<Source[]>([])
  const [action, setAction] = useState('')
  const [sourceBusy, setSourceBusy] = useState('')
  const [sourceDraft, setSourceDraft] = useState<{name: string; url: string; protocol: Protocol; priority: number}>({ name: '', url: '', protocol: 'Http', priority: 100 })

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
      setError('')
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Ошибка загрузки') }
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

  const loadSources = useCallback(async () => {
    if (!adminKey) return
    sessionStorage.setItem('proxyharbor-admin-key', adminKey)
    const response = await fetch(`${API}/api/v1/admin/sources`, { headers: { 'X-Admin-Key': adminKey } })
    if (response.ok) setSources(await response.json()); else setError('Неверный ключ администратора')
  }, [adminKey])

  useEffect(() => {
    if (!adminOpen) return
    const initialLoad = window.setTimeout(() => void loadSources(), 0)
    return () => window.clearTimeout(initialLoad)
  }, [adminOpen, loadSources])

  const runAdminAction = async (name: 'collect' | 'validate' | 'backup') => {
    setAction(name)
    try {
      const response = await fetch(`${API}/api/v1/admin/${name}`, { method: 'POST', headers: { 'X-Admin-Key': adminKey } })
      if (!response.ok) throw new Error(await response.text())
      await Promise.all([load(), loadSources()])
    } catch { setError('Административная операция не выполнена') }
    finally { setAction('') }
  }

  const saveSource = async (event: React.FormEvent) => {
    event.preventDefault()
    setSourceBusy('new')
    try {
      const response = await fetch(`${API}/api/v1/admin/sources`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
        body: JSON.stringify({ ...sourceDraft, enabled: true }),
      })
      if (!response.ok) throw new Error(await response.text())
      setSourceDraft({ name: '', url: '', protocol: 'Http', priority: 100 })
      await loadSources()
    } catch { setError('Не удалось добавить источник: проверьте HTTPS URL и уникальность') }
    finally { setSourceBusy('') }
  }

  const toggleSource = async (source: Source) => {
    setSourceBusy(source.id)
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-Admin-Key': adminKey },
        body: JSON.stringify({ name: source.name, url: source.url, protocol: source.defaultProtocol, priority: source.priority, enabled: !source.enabled }),
      })
      if (!response.ok) throw new Error(await response.text())
      await loadSources()
    } catch { setError('Не удалось изменить состояние источника') }
    finally { setSourceBusy('') }
  }

  const removeSource = async (source: Source) => {
    if (!window.confirm(`Удалить или отключить источник «${source.name}»?`)) return
    setSourceBusy(source.id)
    try {
      const response = await fetch(`${API}/api/v1/admin/sources/${source.id}`, { method: 'DELETE', headers: { 'X-Admin-Key': adminKey } })
      if (!response.ok) throw new Error(await response.text())
      await loadSources()
    } catch { setError('Не удалось удалить источник') }
    finally { setSourceBusy('') }
  }

  const protocolCounts = useMemo(() => Object.fromEntries(stats?.byProtocol.map(x => [x.protocol, x.count]) ?? []), [stats])
  const freshness = stats?.lastRun?.startedAt ? timeAgo(stats.lastRun.startedAt) : 'ожидается'

  return <div className="app-shell">
    <header>
      <a className="brand" href="#top" aria-label="ProxyHarbor — наверх"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a>
      <nav><a href="#catalog">Каталог</a><a href="#api">API</a><button className="admin-link" onClick={() => setAdminOpen(true)}><KeyRound size={15}/> Управление</button></nav>
      <div className={`live-pill ${error ? 'offline' : ''}`} aria-live="polite"><span/> {error ? 'API недоступен' : 'система активна'}</div>
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
        <Metric icon={<Database/>} label="Источников" value={formatNumber(stats?.sources)} note={stats?.failingSources ? `${stats.failingSources} требуют внимания` : 'все источники стабильны'}/>
        <Metric icon={<Clock3/>} label="Готовы к проверке" value={formatNumber(stats?.dueForCheck)} note={`${formatNumber(stats?.scheduledChecks)} запланировано позже`}/>
      </section>

      <section id="catalog" className="catalog">
        <div className="section-heading"><div><span className="kicker">LIVE CATALOG</span><h2>Лучшие прямо сейчас</h2></div><p>Автообновление каждые 15 секунд</p></div>
        <div className="filters"><div className="tabs"><button className={protocol === 'All' ? 'active' : ''} onClick={() => setProtocol('All')}>Все</button>{protocols.map(x => <button key={x} className={protocol === x ? 'active' : ''} onClick={() => setProtocol(x)}>{label(x)}</button>)}</div><label>до <b>{maxLatency} мс</b><input type="range" min="200" max="5000" step="100" value={maxLatency} onChange={e => setMaxLatency(Number(e.target.value))}/></label></div>
        {error && <div className="error-banner"><X size={17}/>{error}<button onClick={() => { setError(''); void load() }}>повторить</button></div>}
        <div className="proxy-table" aria-busy={loading}>
          <div className="table-row table-head"><span>Адрес</span><span>Протокол</span><span>Задержка</span><span>Надёжность</span><span>Проверен</span></div>
          {loading ? <div className="empty"><RefreshCw className="spin"/> Загружаем свежий каталог…</div> : proxies.length === 0 ? <div className="empty"><Server/> Живые прокси появятся после первого цикла проверки.</div> : proxies.map(proxy => <div className="table-row" key={proxy.url}><code>{proxy.host}<i>:</i>{proxy.port}</code><span className={`badge ${proxy.protocol.toLowerCase()}`}>{label(proxy.protocol)}</span><span className="latency"><i className={proxy.latencyMs < 800 ? 'fast' : proxy.latencyMs < 1800 ? 'medium' : 'slow'}/>{proxy.latencyMs} мс</span><span>{proxy.successRate}%</span><span>{timeAgo(proxy.lastCheckedAt)}</span></div>)}
        </div>
      </section>

      <section id="api" className="api-panel"><div><span className="kicker">ONE-CLICK EXPORT</span><h2>Забирайте как удобно</h2><p>Фильтруйте через API или скачивайте готовый список. Выдача содержит только прокси со статусом Alive.</p></div><div className="export-grid">{['json','xml','txt','csv'].map(format => <a key={format} href={`${API}/api/v1/export/${format}`}><span>.{format}</span><ArrowDownToLine size={18}/></a>)}</div><div className="endpoint"><span>GET</span><code>/api/v1/proxies?protocol=Socks5&amp;maxLatencyMs=1000</code></div></section>
    </main>

    <footer><div className="brand"><span className="brand-mark"><Network size={18}/></span><span>Proxy<span>Harbor</span></span></div><p>Используйте публичные прокси ответственно и в рамках закона.</p><span>© {new Date().getFullYear()}</span></footer>

    {adminOpen && <div className="modal-backdrop" onMouseDown={e => e.target === e.currentTarget && setAdminOpen(false)}>
      <section className="admin-modal" role="dialog" aria-modal="true" aria-label="Управление ProxyHarbor">
        <button className="close" aria-label="Закрыть" onClick={() => setAdminOpen(false)}><X/></button>
        <span className="kicker">ADMIN CONSOLE</span><h2>Управление сбором</h2><p>Ключ хранится только до закрытия вкладки.</p>
        <div className="key-input"><KeyRound size={18}/><input type="password" aria-label="Ключ администратора" placeholder="X-Admin-Key" value={adminKey} onChange={e => setAdminKey(e.target.value)}/><button onClick={loadSources}>Войти</button></div>
        <div className="admin-actions">
          <button onClick={() => runAdminAction('collect')} disabled={!adminKey || !!action}><Play/> {action === 'collect' ? 'Собираем…' : 'Запустить сбор'}</button>
          <button onClick={() => runAdminAction('validate')} disabled={!adminKey || !!action}><Check/> {action === 'validate' ? 'Проверяем…' : 'Проверить пакет'}</button>
          <button onClick={() => runAdminAction('backup')} disabled={!adminKey || !!action}><Database/> {action === 'backup' ? 'Копируем…' : 'Создать backup'}</button>
        </div>
        <h3>Добавить источник</h3>
        <form className="source-form" onSubmit={saveSource}>
          <input required minLength={2} maxLength={120} aria-label="Название источника" placeholder="Название" value={sourceDraft.name} onChange={e => setSourceDraft({...sourceDraft, name: e.target.value})}/>
          <input required type="url" maxLength={2048} pattern="https://.*" aria-label="HTTPS URL источника" placeholder="https://example.org/proxies.txt" value={sourceDraft.url} onChange={e => setSourceDraft({...sourceDraft, url: e.target.value})}/>
          <select aria-label="Протокол источника" value={sourceDraft.protocol} onChange={e => setSourceDraft({...sourceDraft, protocol: e.target.value as Protocol})}>{protocols.map(item => <option key={item} value={item}>{label(item)}</option>)}</select>
          <input type="number" min={-10000} max={10000} aria-label="Приоритет источника" value={sourceDraft.priority} onChange={e => setSourceDraft({...sourceDraft, priority: Number(e.target.value)})}/>
          <button type="submit" disabled={!adminKey || !!sourceBusy}>{sourceBusy === 'new' ? 'Добавляем…' : 'Добавить'}</button>
        </form>
        <h3>Источники <span>{sources.length}</span></h3>
        <div className="source-list">{sources.map(source => <article key={source.id}>
          <div><b>{source.name}</b><small>{source.defaultProtocol} · {source.lastItemCount.toLocaleString('ru-RU')} адресов{source.consecutiveFailures > 0 ? ` · сбоев подряд: ${source.consecutiveFailures}` : ''}</small></div>
          <div className="source-controls"><span title={source.lastError} className={source.lastError ? 'source-error' : 'source-ok'}>{source.lastError ? 'ошибка' : source.enabled ? 'активен' : 'пауза'}</span><button disabled={sourceBusy === source.id} onClick={() => toggleSource(source)}>{source.enabled ? 'Пауза' : 'Включить'}</button><button className="danger" disabled={sourceBusy === source.id} onClick={() => removeSource(source)}>Удалить</button></div>
        </article>)}</div>
      </section>
    </div>}
  </div>
}

function Metric({icon, label, value, note}: {icon: React.ReactNode; label: string; value: string; note: string}) { return <article className="metric"><div className="metric-icon">{icon}</div><div><span>{label}</span><strong>{value}</strong><small>{note}</small></div></article> }
function formatNumber(value?: number) { return value === undefined ? '—' : value.toLocaleString('ru-RU') }
function label(protocol: Protocol) { return ({Http: 'HTTP', Https: 'HTTPS', Socks4: 'SOCKS4', Socks5: 'SOCKS5'})[protocol] }
function timeAgo(value: string) { const sec = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000)); if (sec < 10) return 'только что'; if (sec < 60) return `${sec} сек назад`; if (sec < 3600) return `${Math.floor(sec / 60)} мин назад`; return `${Math.floor(sec / 3600)} ч назад` }
