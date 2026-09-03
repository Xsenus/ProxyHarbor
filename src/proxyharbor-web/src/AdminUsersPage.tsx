import { useCallback, useEffect, useState, type ReactNode } from 'react'
import { ArrowRight, Pencil, RefreshCw, Search, ShieldCheck, User, Users, Workflow, X } from 'lucide-react'
import { currentLocale, useI18n } from './i18n'
import { StyledSelect } from './components/StyledSelect'
import { Toggle } from './components/Toggle'
import { ToastSignal } from './components/Toasts'

type PagedResult<T> = { items:T[];page:number;pageSize:number;total:number }
type AdminUser = { id:string;userName:string;email:string;displayName?:string;createdAt:string;lastLoginAt?:string;roles:string[];isActive:boolean;subscription?:{plan:string;status:string;startedAt:string;expiresAt?:string} }
type UserAccessDraft = { isActive:boolean;administrator:boolean;subscriber:boolean;plan:string;status:string;expiresAt:string }
type ReferralReward = { id:string;kind:'signup'|'purchase';daysGranted:number;createdAt:string;productCode?:string;durationDays?:number }
type AdminReferralItem = { id:string;createdAt:string;referrer:{referrerUserId:string;userName:string;email:string;displayName?:string};referred:{referredUserId:string;userName:string;email:string;displayName?:string};rewardDays:number;rewards:ReferralReward[] }
type AdminReferralPage = PagedResult<AdminReferralItem> & {summary:{referrals:number;rewardDays:number;purchaseRewards:number}}

const API = import.meta.env.VITE_API_URL ?? ''

/** Серверный реестр остаётся быстрым при сотнях тысяч аккаунтов. */
export default function AdminUsersPage() {
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

function AdminPageHeader({id,title,children}:{id:string;title:string;children?:ReactNode}){
  return <header className="admin-page-heading">
    <nav className="admin-breadcrumb" aria-label="Положение в панели управления">
      <a href="/admin">Панель управления</a><ArrowRight aria-hidden="true"/><h1 id={id}>{title}</h1>
    </nav>
    {children&&<div className="admin-heading-actions">{children}</div>}
  </header>
}

function ProxyPagination({page,pageSize,total,totalPages,onPageChange,onPageSizeChange}:{page:number;pageSize:number;total:number;totalPages:number;onPageChange:(page:number)=>void;onPageSizeChange:(size:number)=>void}){
  const {t}=useI18n()
  const [jump,setJump]=useState('')
  const pages=paginationWindow(page,totalPages)
  const go=(next:number)=>onPageChange(Math.min(totalPages,Math.max(1,next)))
  const showQuickJump=totalPages>7
  return <nav className={`pagination${showQuickJump?'':' pagination-compact'}`} aria-label={t('quickJump')}>
    <div className="page-sizes"><span>{t('show')}</span>{[10,25,50,100].map(size=><button key={size} className={pageSize===size?'active':''} aria-pressed={pageSize===size} onClick={()=>onPageSizeChange(size)}>{size}</button>)}</div>
    <div className="page-controls"><button aria-label={t('previousPage')} disabled={page===1} onClick={()=>go(page-1)}>←</button>{pages.map((item,index)=>item==='…'?<span key={`ellipsis-${index}`}>…</span>:<button key={item} className={item===page?'active':''} aria-current={item===page?'page':undefined} onClick={()=>go(item)}>{item}</button>)}<button aria-label={t('nextPage')} disabled={page===totalPages} onClick={()=>go(page+1)}>→</button></div>
    {showQuickJump&&<form className="page-jump" aria-label={t('quickJump')} onSubmit={event=>{event.preventDefault();const value=Number(jump);if(Number.isInteger(value)&&value>0){go(value);setJump('')}}}><input aria-label={t('pageNumber')} inputMode="numeric" min={1} max={totalPages} type="number" placeholder="Стр." value={jump} onChange={event=>setJump(event.target.value)}/><span aria-hidden="true">/ {totalPages}</span><button type="submit" aria-label={t('goToPage')}><ArrowRight size={14}/></button></form>}
    <p>{t('page',{page,pages:totalPages,total:total.toLocaleString(currentLocale())})}</p>
  </nav>
}

function paginationWindow(page:number,totalPages:number):(number|'…')[]{
  if(totalPages<=7)return Array.from({length:totalPages},(_,index)=>index+1)
  if(page<=4)return [1,2,3,4,5,'…',totalPages]
  if(page>=totalPages-3)return [1,'…',totalPages-4,totalPages-3,totalPages-2,totalPages-1,totalPages]
  return [1,'…',page-1,page,page+1,'…',totalPages]
}

function planLabel(plan?:string){return plan==='unlimited'?'Unlimited':plan==='pro'?'Pro':'Free'}
function subscriptionStatusLabel(status:string){return ({active:'Активна',trialing:'Пробная',past_due:'Просрочена',canceled:'Отменена',expired:'Истекла',suspended:'Приостановлена'} as Record<string,string>)[status]??status}
function formatDateTime(value:string){return new Intl.DateTimeFormat(currentLocale(),{dateStyle:'medium',timeStyle:'medium'}).format(new Date(value))}
function timeAgo(value:string){const sec=Math.max(0,Math.floor((Date.now()-new Date(value).getTime())/1000));const formatter=new Intl.RelativeTimeFormat(currentLocale(),{numeric:'auto'});if(sec<60)return formatter.format(-sec,'second');if(sec<3600)return formatter.format(-Math.floor(sec/60),'minute');if(sec<86400)return formatter.format(-Math.floor(sec/3600),'hour');return formatter.format(-Math.floor(sec/86400),'day')}
function isAbortError(reason:unknown){return reason instanceof Error&&reason.name==='AbortError'}
async function responseMessage(response:Response,fallback:string){try{const body=await response.json() as {message?:string;title?:string};return body.message||body.title||fallback}catch{return fallback}}
