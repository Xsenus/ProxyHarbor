import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { ArrowRight, Bot, Clock3, Copy, CreditCard, Globe2, LayoutDashboard, LockKeyhole, LogOut, Network, Plus, RefreshCw, ShieldCheck, ShieldOff, Trash2, User, Users, Workflow } from 'lucide-react'
import { currentLocale, LanguageSwitcher, type Language, useI18n } from './i18n'
import { ToastSignal } from './components/Toasts'

type PagedResult<T> = { items: T[]; page: number; pageSize: number; total: number }
type AccountApiToken = { id:string;name:string;displaySuffix:string;scopes:string[];createdAt:string;lastUsedAt?:string;revokedAt?:string;active:boolean }
type ApiTokenRequest = {id:number;token:{userApiTokenId:string;name:string;displaySuffix:string;revokedAt?:string};ipAddress:string;method:string;path:string;query?:string;statusCode:number;itemCount?:number;durationMs:number;requestedAt:string}
type ApiTokenHistoryPage = PagedResult<ApiTokenRequest>
type ReferralReward = { id:string;kind:'signup'|'purchase';daysGranted:number;createdAt:string;productCode?:string;durationDays?:number }
type ReferralItem = { id:string;createdAt:string;user:{userName:string;email:string;displayName?:string};rewards:ReferralReward[] }
type ReferralPage = PagedResult<ReferralItem>
type ReferralSummary = { code:string;link:string;telegramLink?:string;invited:number;remaining:number;maximum:number;rewardDays:number }
type AccountProfile = { id: string; userName: string; email: string; displayName?: string; preferredLanguage: Language; createdAt: string; lastLoginAt?: string; referralCode:string; referral:ReferralSummary; roles: string[]; subscription?: { plan: string; status: string; startedAt: string; expiresAt?: string }; entitlements:{unlimitedProxyAccess:boolean;apiTokens:boolean};apiTokens:AccountApiToken[] }
type PaymentCatalog = { enabled: boolean; products: PaymentProduct[]; providers: PaymentProvider[] }
type PaymentProduct = { code: string; name: string; plan: string; durationDays: number; amountMinor: number; discountPercent:number; fullDailyPriceMinor:number;savingsMinor:number;currency:string;description:string }
type PaymentProvider = { code: string; name: string; available: boolean }
type PaymentOrder = { id: string; productCode: string; plan: string; provider: string; paymentMethod:string; paymentInstrument?:string; amountMinor: number; currency: string; status: string; createdAt: string; paidAt?: string }

const API = import.meta.env.VITE_API_URL ?? ''

export default function AccountPage() {
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
  const saveProfile = async (event: FormEvent) => { event.preventDefault(); setError(''); const response = await fetch(`${API}/api/v1/account/profile`, {method:'PUT', credentials:'include', headers:{'Content-Type':'application/json'}, body:JSON.stringify({displayName,preferredLanguage:language})}); if (!response.ok) {setError(await responseMessage(response,'Не удалось сохранить профиль'));return} setNotice(t('profileSaved')); await loadProfile() }
  const changePassword = async (event: FormEvent) => { event.preventDefault(); setError(''); const response = await fetch(`${API}/api/v1/account/change-password`, {method:'POST',credentials:'include',headers:{'Content-Type':'application/json'},body:JSON.stringify(passwords)}); if(!response.ok){setError(await responseMessage(response,'Не удалось изменить пароль'));return} setPasswords({currentPassword:'',newPassword:''});setNotice('Пароль изменён. Другие сессии будут отозваны.') }
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
function timeAgo(value: string) { const sec = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000)); const formatter=new Intl.RelativeTimeFormat(currentLocale(),{numeric:'auto'}); if(sec<60)return formatter.format(-sec,'second');if(sec<3600)return formatter.format(-Math.floor(sec/60),'minute');if(sec<86400)return formatter.format(-Math.floor(sec/3600),'hour');return formatter.format(-Math.floor(sec/86400),'day') }
function planLabel(plan?: string) { return plan === 'unlimited' ? 'Unlimited' : plan === 'pro' ? 'Pro' : 'Free' }
function money(minor:number,currency:string){return new Intl.NumberFormat(currentLocale(),{style:'currency',currency}).format(minor/100)}
function providerLabel(provider:string){return ({yookassa:'ЮKassa',yoomoney:'ЮMoney',cloudpayments:'CloudPayments',robokassa:'Robokassa',tbank:'Т-Банк',stripe:'Stripe',cryptomus:'Cryptomus',nowpayments:'NOWPayments'} as Record<string,string>)[provider]??provider}
function paymentStatusLabel(status:string){return ({pending:'Ожидает оплаты',paid:'Оплачен',failed:'Ошибка',canceled:'Отменён',refunded:'Возвращён'} as Record<string,string>)[status]??status}
function localizedSubscriptionStatusLabel(status:string,language:Language){const labels:Record<Language,Record<string,string>>={ru:{active:'Активна',trialing:'Пробная',suspended:'Приостановлена',expired:'Истекла'},en:{active:'Active',trialing:'Trial',suspended:'Suspended',expired:'Expired'},de:{active:'Aktiv',trialing:'Testphase',suspended:'Pausiert',expired:'Abgelaufen'},fr:{active:'Actif',trialing:'Essai',suspended:'Suspendu',expired:'Expiré'},zh:{active:'有效',trialing:'试用',suspended:'已暂停',expired:'已过期'}};return labels[language][status.toLowerCase()]??status}

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

