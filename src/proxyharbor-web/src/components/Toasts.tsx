import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { Check, Info, TriangleAlert, X } from 'lucide-react'

type ToastKind = 'error' | 'success' | 'info'
type ToastAction = { label: string; run: () => void }
type ToastItem = { id: number; kind: ToastKind; message: string; action?: ToastAction }

const ToastContext = createContext<((kind: ToastKind, message: string, action?: ToastAction) => void) | null>(null)

/** Единый доступный слой кратких уведомлений для публичной части и кабинета. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<ToastItem[]>([])
  const nextId = useRef(1)
  const timers = useRef(new Map<number, number>())

  const dismiss = useCallback((id: number) => {
    const timer = timers.current.get(id)
    if (timer) window.clearTimeout(timer)
    timers.current.delete(id)
    setItems(current => current.filter(item => item.id !== id))
  }, [])

  const push = useCallback((kind: ToastKind, message: string, action?: ToastAction) => {
    if (!message.trim()) return
    const id = nextId.current++
    setItems(current => [...current.slice(-3), { id, kind, message, action }])
    timers.current.set(id, window.setTimeout(() => dismiss(id), kind === 'error' ? 8000 : 5000))
  }, [dismiss])

  useEffect(() => () => timers.current.forEach(timer => window.clearTimeout(timer)), [])
  const value = useMemo(() => push, [push])

  return <ToastContext.Provider value={value}>
    {children}
    <aside className="toast-viewport" aria-label="Уведомления" aria-live="polite">
      {items.map(item => <article key={item.id} className={`app-toast ${item.kind}`} role={item.kind === 'error' ? 'alert' : 'status'}>
        <span className="toast-icon">{item.kind === 'error' ? <TriangleAlert/> : item.kind === 'success' ? <Check/> : <Info/>}</span>
        <p>{item.message}</p>
        {item.action && <button className="toast-action" type="button" onClick={() => { dismiss(item.id); item.action?.run() }}>{item.action.label}</button>}
        <button className="toast-close" type="button" aria-label="Закрыть уведомление" onClick={() => dismiss(item.id)}><X/></button>
      </article>)}
    </aside>
  </ToastContext.Provider>
}

/** Преобразует существующее состояние формы в toast, не дублируя StrictMode-эффекты. */
export function ToastSignal({ kind, message, action }: { kind: ToastKind; message?: string; action?: ToastAction }) {
  const push = useContext(ToastContext)
  const delivered = useRef('')
  useEffect(() => {
    if (!message) { delivered.current = ''; return }
    const key = `${kind}:${message}`
    if (delivered.current === key) return
    delivered.current = key
    push?.(kind, message, action)
  }, [action, kind, message, push])
  // Component tests and embedded screens can render without the application provider.
  return !push && message ? <span className="sr-only" role={kind === 'error' ? 'alert' : 'status'}>{message}</span> : null
}

type ServerNotification = { id: string; message: string; actionUrl?: string }

/** Доставляет сохранённые сервером уведомления один раз, даже после повторного входа или перезагрузки. */
export function NotificationBridge({ apiBase = '' }: { apiBase?: string }) {
  const push = useContext(ToastContext)
  useEffect(() => {
    if (!push) return
    let stopped = false
    const poll = async () => {
      try {
        const response = await fetch(`${apiBase}/api/v1/account/notifications`, { credentials: 'include' })
        if (!response.ok || stopped) return
        const items = await response.json() as ServerNotification[]
        for (const item of items) {
          if (stopped) return
          push('info', item.message, item.actionUrl ? {
            label: 'Открыть',
            run: () => window.location.assign(item.actionUrl!),
          } : undefined)
          await fetch(`${apiBase}/api/v1/account/notifications/${item.id}/delivered`, {
            method: 'POST', credentials: 'include',
          })
        }
      } catch {
        // Отсутствие сети или гостевая сессия не должны мешать работе публичной страницы.
      }
    }
    void poll()
    const timer = window.setInterval(() => void poll(), 60_000)
    return () => { stopped = true; window.clearInterval(timer) }
  }, [apiBase, push])
  return null
}
