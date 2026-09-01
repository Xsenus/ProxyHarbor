import { useEffect, useMemo, useState } from 'react'
import type * as React from 'react'
import { Check, Eye, EyeOff, LockKeyhole, Mail, Network, ShieldCheck, User, Workflow } from 'lucide-react'
import { LanguageSwitcher, useI18n } from './i18n'
import { ToastSignal } from './components/Toasts'

const API = import.meta.env.VITE_API_URL ?? ''

export type AuthRouteKind = 'login' | 'register' | 'forgot-password' | 'reset-password'

function AccountLoginPage() {
  const { t } = useI18n()
  const [mode,setMode] = useState<'password'|'token'>('password')
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [apiToken,setApiToken] = useState('')
  const [showPassword, setShowPassword] = useState(false)
  const [rememberMe, setRememberMe] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')

  useEffect(() => {
    const controller = new AbortController()
    void fetch(`${API}/api/v1/auth/session`, { credentials: 'include', signal: controller.signal })
      .then(async response => {
        if (!response.ok) return
        const session = await response.json() as { roles?: string[] }
        window.location.replace(session.roles?.includes('Administrator') ? '/admin' : '/account')
      })
      .catch(reason => { if (reason instanceof DOMException && reason.name === 'AbortError') return })
    return () => controller.abort()
  }, [])

  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (busy || mode==='password' && (!username || !password) || mode==='token' && !apiToken) return
    setBusy(true)
    setError('')
    try {
      const response = await fetch(`${API}/api/v1/auth/${mode==='token'?'token-login':'login'}`, {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(mode==='token'?{token:apiToken.trim(),rememberMe}:{username,password,rememberMe}),
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
    <a className="brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a><LanguageSwitcher compact/>
    <section className="login-card" aria-labelledby="login-title">
      <span className="kicker">ACCOUNT ACCESS</span>
      <h1 id="login-title">{t('authLoginTitle')}</h1>
      <p>{t('authLoginText')}</p>
      <div className="auth-mode-tabs" role="tablist" aria-label={t('authLoginTitle')}><button type="button" role="tab" aria-selected={mode==='password'} className={mode==='password'?'active':''} onClick={()=>{setMode('password');setError('')}}>{t('passwordAccess')}</button><button type="button" role="tab" aria-selected={mode==='token'} className={mode==='token'?'active':''} onClick={()=>{setMode('token');setError('')}}>{t('tokenAccess')}</button></div>
      <form className="login-form" onSubmit={submit}>
        {mode==='password'?<><label htmlFor="account-identifier">{t('loginOrEmail')}</label>
        <div className="login-field"><User size={18}/><input id="account-identifier" autoFocus required type="text" placeholder="login или name@example.com" autoComplete="username" autoCapitalize="none" spellCheck={false} minLength={3} maxLength={254} value={username} onChange={event => setUsername(event.target.value)}/></div>
        <label htmlFor="admin-password">{t('password')}</label>
        <div className="login-field"><LockKeyhole size={18}/><input id="admin-password" required type={showPassword ? 'text' : 'password'} placeholder={t('password')} autoComplete="current-password" maxLength={256} value={password} onChange={event => setPassword(event.target.value)}/><button className="password-toggle" type="button" aria-label={showPassword ? 'Скрыть пароль' : 'Показать пароль'} onClick={() => setShowPassword(value => !value)}>{showPassword ? <EyeOff/> : <Eye/>}</button></div>
        <a className="forgot-link" href="/forgot-password">{t('forgotPassword')}</a></>:<><label htmlFor="account-token">{t('apiToken')}</label><div className="login-field token-login-field"><ShieldCheck size={18}/><input id="account-token" autoFocus required type="password" autoComplete="off" autoCapitalize="none" spellCheck={false} minLength={80} maxLength={160} value={apiToken} onChange={event=>setApiToken(event.target.value)} placeholder="ph_live_…"/></div><small className="token-login-hint">{t('apiTokenLoginHint')}</small></>}
        <label className="remember-session"><input className="ui-checkbox-input" type="checkbox" checked={rememberMe} onChange={event=>setRememberMe(event.target.checked)}/><span className="ui-checkbox-mark" aria-hidden="true"><Check/></span><span><b>{t('rememberMe')}</b><small>{t('rememberMeHint')}</small></span></label>
        <button className="login-submit" type="submit" disabled={busy || mode==='password'&&(!username||!password) || mode==='token'&&!apiToken}>{busy ? t('signingIn') : t('signIn')}</button>
      </form>
      <ToastSignal kind="error" message={error}/>
      <div className="account-auth-footer"><span>{t('noAccount')}</span><a href="/register">{t('register')}</a></div>
      <a className="back-link" href="/">← {t('home')}</a>
    </section>
  </main>
}

/** Самостоятельная регистрация создаёт только безопасную базовую роль User и free-подписку. */
function RegisterPage() {
  const { language, t } = useI18n()
  const referralCode = useMemo(() => new URLSearchParams(window.location.search).get('ref')?.trim().toLowerCase() ?? '', [])
  const [form, setForm] = useState({ username: '', email: '', displayName: '', password: '', confirm: '' })
  const [visiblePasswords, setVisiblePasswords] = useState({ password: false, confirm: false })
  const [acceptedOffer,setAcceptedOffer]=useState(false)
  const [acceptedPersonalData,setAcceptedPersonalData]=useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const submit = async (event: React.FormEvent) => {
    event.preventDefault()
    if (form.password !== form.confirm) { setError(t('passwordsMismatch')); return }
    setBusy(true); setError('')
    try {
      const response = await fetch(`${API}/api/v1/auth/register`, { method: 'POST', credentials: 'include', headers: {'Content-Type':'application/json'}, body: JSON.stringify({...form, acceptedOffer, acceptedPersonalData, preferredLanguage:language, referralCode:referralCode||null}) })
      if (!response.ok) throw new Error(await responseMessage(response, t('accountCreateFailed')))
      window.location.assign('/account')
    } catch (reason) { setError(reason instanceof Error ? reason.message : t('accountCreateFailed')); setBusy(false) }
  }
  return <AuthLayout title={t('registerTitle')} kicker="FREE ACCOUNT" description={t('registerText')} variant="registration">
    {referralCode && <div className="referral-applied"><Workflow/><span><b>Приглашение применено</b><small>После регистрации пригласившему будет начислен 1 день подписки.</small></span></div>}
    <form className="login-form registration-form" aria-label={t('registerTitle')} onSubmit={submit}>
      <div className="registration-control registration-control-wide">
        <label htmlFor="register-name">{t('yourName')}</label>
        <div className="login-field"><User/><input id="register-name" maxLength={120} autoComplete="name" placeholder={t('optionalName')} value={form.displayName} onChange={event => setForm({...form, displayName: event.target.value})}/></div>
      </div>
      <div className="registration-control">
        <label htmlFor="register-username">{t('username')}</label>
        <div className="login-field"><User/><input id="register-username" required minLength={3} maxLength={64} pattern="[A-Za-z0-9._-]+" autoComplete="username" autoCapitalize="none" spellCheck={false} placeholder="proxy.user" value={form.username} onChange={event => setForm({...form, username: event.target.value})}/></div>
      </div>
      <div className="registration-control">
        <label htmlFor="register-email">Email</label>
        <div className="login-field"><Mail/><input id="register-email" required type="email" maxLength={254} autoComplete="email" autoCapitalize="none" spellCheck={false} placeholder="name@example.com" value={form.email} onChange={event => setForm({...form, email: event.target.value})}/></div>
      </div>
      <div className="registration-control">
        <label htmlFor="register-password">{t('password')}</label>
        <div className="login-field"><LockKeyhole/><input id="register-password" required minLength={12} maxLength={256} type={visiblePasswords.password ? 'text' : 'password'} autoComplete="new-password" placeholder={t('passwordHint')} value={form.password} onChange={event => setForm({...form, password: event.target.value})}/><button className="password-toggle" type="button" aria-label={visiblePasswords.password ? t('hidePassword') : t('showPassword')} aria-pressed={visiblePasswords.password} onClick={() => setVisiblePasswords(value => ({...value, password: !value.password}))}>{visiblePasswords.password ? <EyeOff/> : <Eye/>}</button></div>
      </div>
      <div className="registration-control">
        <label htmlFor="register-confirm">{t('repeatPassword')}</label>
        <div className="login-field"><ShieldCheck/><input id="register-confirm" required type={visiblePasswords.confirm ? 'text' : 'password'} autoComplete="new-password" placeholder={t('repeatPassword')} value={form.confirm} onChange={event => setForm({...form, confirm: event.target.value})}/><button className="password-toggle" type="button" aria-label={visiblePasswords.confirm ? t('hidePassword') : t('showPassword')} aria-pressed={visiblePasswords.confirm} onClick={() => setVisiblePasswords(value => ({...value, confirm: !value.confirm}))}>{visiblePasswords.confirm ? <EyeOff/> : <Eye/>}</button></div>
      </div>
      <label className="registration-consent"><input className="ui-checkbox-input" required type="checkbox" checked={acceptedOffer} onChange={event=>setAcceptedOffer(event.target.checked)}/><span className="ui-checkbox-mark" aria-hidden="true"><Check/></span><span>Я принимаю <a href="/offer" target="_blank">публичную оферту</a> (редакция 2.0).</span></label>
      <label className="registration-consent"><input className="ui-checkbox-input" required type="checkbox" checked={acceptedPersonalData} onChange={event=>setAcceptedPersonalData(event.target.checked)}/><span className="ui-checkbox-mark" aria-hidden="true"><Check/></span><span>Я отдельно даю <a href="/personal-data-consent" target="_blank">согласие на обработку персональных данных</a> и ознакомлен с <a href="/privacy" target="_blank">политикой обработки данных</a>.</span></label>
      <button className="login-submit" disabled={busy||!acceptedOffer||!acceptedPersonalData}>{busy ? t('creating') : t('createAccount')}</button>
    </form><ToastSignal kind="error" message={error}/><div className="account-auth-footer registration-auth-footer"><span>{t('alreadyAccount')}</span><a href="/login">{t('signIn')}</a></div><a className="back-link" href="/">← {t('home')}</a>
  </AuthLayout>
}

/** Запрос восстановления всегда показывает нейтральный результат без раскрытия аккаунта. */
function ForgotPasswordPage() {
  const { t } = useI18n()
  const [email, setEmail] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const submit = async (event: React.FormEvent) => {
    event.preventDefault(); setError(''); setMessage('')
    const response = await fetch(`${API}/api/v1/auth/forgot-password`, { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({email}) })
    if (!response.ok) { setError(await responseMessage(response, t('emailSendFailed'))); return }
    setMessage(t('recoverySent'))
  }
  return <AuthLayout title={t('recoveryTitle')} kicker="ACCOUNT RECOVERY" description={t('recoveryText')}>
    <form className="login-form" onSubmit={submit}><label htmlFor="recovery-email">Email</label><div className="login-field"><Mail/><input id="recovery-email" required type="email" autoComplete="email" placeholder="name@example.com" value={email} onChange={event => setEmail(event.target.value)}/></div><button className="login-submit">{t('sendLink')}</button></form>
    <ToastSignal kind="success" message={message}/><ToastSignal kind="error" message={error}/><a className="back-link" href="/login">← {t('backToLogin')}</a>
  </AuthLayout>
}

/** Применяет email и token только из ссылки, а новый пароль вводится дважды. */
function ResetPasswordPage() {
  const { t } = useI18n()
  const query = new URLSearchParams(window.location.search)
  const email = query.get('email') ?? ''
  const token = query.get('token') ?? ''
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const submit = async (event: React.FormEvent) => {
    event.preventDefault(); setError('')
    if (!email || !token) { setError(t('linkIncomplete')); return }
    if (password !== confirm) { setError(t('passwordsMismatch')); return }
    const response = await fetch(`${API}/api/v1/auth/reset-password`, { method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify({email, token, newPassword: password}) })
    if (!response.ok) { setError(await responseMessage(response, t('linkInvalid'))); return }
    setMessage(t('passwordChanged'))
  }
  return <AuthLayout title={t('newPassword')} kicker="SECURE RESET" description={email ? t('recoveryFor', {email}) : t('incompleteRecoveryLink')}>
    {!message && <form className="login-form" onSubmit={submit}><label htmlFor="reset-password">{t('newPassword')}</label><div className="login-field"><LockKeyhole/><input id="reset-password" required minLength={12} type="password" autoComplete="new-password" value={password} onChange={event => setPassword(event.target.value)}/></div><label htmlFor="reset-confirm">{t('repeatPassword')}</label><div className="login-field"><ShieldCheck/><input id="reset-confirm" required type="password" autoComplete="new-password" value={confirm} onChange={event => setConfirm(event.target.value)}/></div><button className="login-submit">{t('changePassword')}</button></form>}
    <ToastSignal kind="success" message={message}/><ToastSignal kind="error" message={error}/><a className="back-link" href="/login">← {t('goToLogin')}</a>
  </AuthLayout>
}

/** Единая оболочка auth-экранов сохраняет визуальный ритм и семантику заголовков. */
function AuthLayout({title, kicker, description, children, variant}: {title: string; kicker: string; description: string; children: React.ReactNode; variant?: 'registration'}) {
  return <main className={`login-page${variant ? ` ${variant}-auth-page` : ''}`}><a className="brand" href="/"><span className="brand-mark"><Network size={20}/></span><span>Proxy<span>Harbor</span></span></a><LanguageSwitcher compact/><section className={`login-card account-auth-card${variant ? ` ${variant}-auth-card` : ''}`} aria-labelledby="auth-title"><span className="kicker">{kicker}</span><h1 id="auth-title">{title}</h1><p>{description}</p>{children}</section></main>
}

export default function AuthRoutePage({ kind }: { kind: AuthRouteKind }) {
  if (kind === 'register') return <RegisterPage/>
  if (kind === 'forgot-password') return <ForgotPasswordPage/>
  if (kind === 'reset-password') return <ResetPasswordPage/>
  return <AccountLoginPage/>
}

async function responseMessage(response: Response, fallback: string) {
  try {
    const payload = await response.json() as { message?: string; detail?: string; title?: string }
    return payload.message ?? payload.detail ?? payload.title ?? fallback
  } catch {
    return fallback
  }
}
