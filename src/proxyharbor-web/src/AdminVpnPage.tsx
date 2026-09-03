import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { ArrowRight, Check, Copy, Gauge, Globe2, Pencil, Plus, Radio, RefreshCw, Search, ShieldCheck, X } from 'lucide-react'
import { currentLocale, useI18n } from './i18n'
import { StyledSelect } from './components/StyledSelect'
import { ToastSignal } from './components/Toasts'

type ProxyCountry = { code: string; count: number }
type PagedResult<T> = { items: T[]; page: number; pageSize: number; total: number }
type VpnProtocol = 'OpenVpn'|'WireGuard'|'Vless'|'Vmess'|'Trojan'|'Shadowsocks'|'Hysteria2'|'Tuic'
type VpnStatus = 'Pending'|'Reachable'|'Unreachable'|'UnsupportedTransport'
type VpnEndpoint = {id:string;host:string;port:number;countryCode?:string;protocol:VpnProtocol;transport:'tcp'|'udp';status:VpnStatus;latencyMs?:number;firstSeenAt:string;lastSeenAt:string;lastCheckedAt?:string;nextCheckAt?:string;successfulChecks:number;failedChecks:number;successRate:number;knownForSeconds:number;lastError?:string;connectionUri?:string}
type AdminVpnPageData = PagedResult<VpnEndpoint> & {summary:{total:number;reachable:number;pending:number;unreachable:number;unsupportedTransport:number;everReachable:number;averageReachableLatencyMs?:number;countries:number;longestKnownSeconds?:number};countries:ProxyCountry[]}
type VpnSource = {id:string;name:string;provider:string;url:string;defaultProtocol:VpnProtocol;enabled:boolean;priority:number;license:string;lastFetchedAt?:string;lastSucceededAt?:string;lastItemCount:number;consecutiveFailures:number;lastError?:string;isBuiltIn:boolean}

const API = import.meta.env.VITE_API_URL ?? ''

export default function AdminVpnPage() {
  const protocols: VpnProtocol[] = ['OpenVpn','WireGuard','Vless','Vmess','Trojan','Shadowsocks','Hysteria2','Tuic']
  const [tab,setTab] = useState<'endpoints'|'sources'>(() => new URLSearchParams(window.location.search).get('tab') === 'sources' ? 'sources' : 'endpoints')
  const [endpointData,setEndpointData] = useState<AdminVpnPageData|null>(null)
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
      const data = await response.json() as AdminVpnPageData|PagedResult<VpnSource>
      if (tab === 'endpoints') { setEndpointData(data as AdminVpnPageData); setSources([]) }
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
  const save = async (event:FormEvent) => {
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

function CheckboxMark(){return <span className="ui-checkbox-mark" aria-hidden="true"><Check/></span>}

function AdminPageHeader({id,title,children}:{id:string;title:string;children?:ReactNode}){
  return <header className="admin-page-heading">
    <nav className="admin-breadcrumb" aria-label="Положение в панели управления">
      <a href="/admin">Панель управления</a><ArrowRight aria-hidden="true"/><h1 id={id}>{title}</h1>
    </nav>
    {children&&<div className="admin-heading-actions">{children}</div>}
  </header>
}

function SortHeader({field,label,sort,order,onChange}:{field:string;label:string;sort:string;order:string;onChange:(field:string,order:string)=>void}){
  const active=sort===field
  return <button className={`sort-header${active?' active':''}`} aria-label={`${label}: сортировка ${active&&order==='asc'?'по возрастанию':'по убыванию'}`} onClick={()=>onChange(field,active&&order==='desc'?'asc':'desc')}>{label}<span aria-hidden="true">{active?(order==='asc'?'↑':'↓'):'↕'}</span></button>
}

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

function formatNumber(value?: number) { return value === undefined ? '—' : value.toLocaleString(currentLocale()) }
function formatDateTime(value:string){return new Intl.DateTimeFormat(currentLocale(),{dateStyle:'medium',timeStyle:'medium'}).format(new Date(value))}
function formatActiveDuration(value?: number) {
  if (value === undefined) return '—'
  if (value < 60) return '< 1 мин'
  const minutes = Math.floor(value / 60)
  if (minutes < 60) return `${minutes} мин`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours} ч ${minutes % 60} мин`
  return `${Math.floor(hours / 24)} д ${hours % 24} ч`
}
function countryName(code:string){return new Intl.DisplayNames([currentLocale()], { type: 'region' }).of(code.toUpperCase())??code.toUpperCase()}
function CountryFlag({code}:{code:string}){const normalized=/^[a-z]{2}$/i.test(code)?code.toLowerCase():'';return normalized?<img className={`country-flag flag-${normalized}`} src={`/flags/${normalized}.svg`} alt="" loading="lazy" decoding="async"/>:<Globe2 className="country-flag country-flag-unknown" aria-hidden="true"/>}
function timeAgo(value: string) { const sec = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000)); const formatter=new Intl.RelativeTimeFormat(currentLocale(),{numeric:'auto'}); if(sec<60)return formatter.format(-sec,'second');if(sec<3600)return formatter.format(-Math.floor(sec/60),'minute');if(sec<86400)return formatter.format(-Math.floor(sec/3600),'hour');return formatter.format(-Math.floor(sec/86400),'day') }
function timeUntil(value: string) { const sec = Math.max(0, Math.ceil((new Date(value).getTime() - Date.now()) / 1000)); if (sec < 60) return `через ${sec} сек`; if (sec < 3600) return `через ${Math.ceil(sec / 60)} мин`; return `через ${Math.ceil(sec / 3600)} ч` }

async function copyText(value: string) {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(value)
      return
    } catch {
      // Continue with the compatible fallback below.
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

async function responseMessage(response: Response, fallback: string) {
  try {
    const problem = await response.json() as { title?: string; detail?: string; message?: string; error?: string }
    const message = problem.detail || problem.message || problem.title
    return [message, problem.error && problem.error !== message ? problem.error : ''].filter(Boolean).join(' · ') || fallback
  } catch { return fallback }
}

