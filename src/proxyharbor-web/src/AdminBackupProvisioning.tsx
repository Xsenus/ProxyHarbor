import { useEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { Plus, ShieldCheck, X } from 'lucide-react'
import { StyledSelect } from './components/StyledSelect'
import { Toggle } from './components/Toggle'

export type BackupDestinationChoice = {
  id: string
  name: string
  kind: string
  enabled: boolean
  credentialsConfigured: boolean
  failureDomainConfigured: boolean
}

type DestinationDraft = {
  name: string
  endpoint: string
  region: string
  bucket: string
  prefix: string
  usePathStyle: boolean
  accessKey: string
  secretKey: string
  priority: number
}

type PoolRouteDraft = {
  destinationId: string
  priority: number
  role: string
  activateDestination: boolean
}

type PoolDraft = {
  name: string
  requiredVerifiedCopies: number
  desiredVerifiedCopies: number
  maxAttemptsPerCycle: number
  overallDeadlineSeconds: number
  failbackHealthyForSeconds: number
  routes: PoolRouteDraft[]
}

type PagedDestinations = { items: BackupDestinationChoice[]; total: number }

export type BackupProvisioningResult =
  | { kind: 'destination'; name: string }
  | { kind: 'pool'; name: string; id: string }

const destinationInitial: DestinationDraft = {
  name: '', endpoint: '', region: '', bucket: '', prefix: 'proxyharbor/backups',
  usePathStyle: true, accessKey: '', secretKey: '', priority: 10
}
const emptyRoute = (priority: number): PoolRouteDraft =>
  ({ destinationId: '', priority, role: priority === 10 ? 'primary' : 'fallback', activateDestination: false })
const poolInitial: PoolDraft = {
  name: '', requiredVerifiedCopies: 1, desiredVerifiedCopies: 1,
  maxAttemptsPerCycle: 3, overallDeadlineSeconds: 600, failbackHealthyForSeconds: 900,
  routes: [emptyRoute(10)]
}

async function safeError(response: Response, fallback: string) {
  try {
    const problem = await response.json() as { detail?: string; title?: string }
    return problem.detail || problem.title || fallback
  } catch { return fallback }
}

/** Write-only S3 destination and explicit pool provisioning; neither switches production routing. */
export function AdminBackupProvisioning({mode, apiBase, onClose, onSaved}: {
  mode: 'destination' | 'pool'
  apiBase: string
  onClose: () => void
  onSaved: (result: BackupProvisioningResult) => void
}) {
  const [destination, setDestination] = useState<DestinationDraft>(destinationInitial)
  const [pool, setPool] = useState<PoolDraft>(poolInitial)
  const [choices, setChoices] = useState<BackupDestinationChoice[]>([])
  const [loading, setLoading] = useState(mode === 'pool')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const dialog = useRef<HTMLElement>(null)

  useEffect(() => {
    if (mode !== 'pool') return
    const controller = new AbortController()
    const load = async () => {
      try {
        const getPage = async (page: number) => {
          const response = await fetch(`${apiBase}/api/v1/admin/backups/destinations?page=${page}&pageSize=100`,
            { credentials: 'include', cache: 'no-store', signal: controller.signal })
          if (!response.ok) throw new Error('Не удалось загрузить назначения.')
          return response.json() as Promise<PagedDestinations>
        }
        const first = await getPage(1)
        if (first.total > 1000) throw new Error('Назначений слишком много для этой формы; настройте pool через API.')
        const pageCount = Math.ceil(first.total / 100)
        const rest = await Promise.all(Array.from({length: Math.max(0, pageCount - 1)}, (_, index) => getPage(index + 2)))
        if (!controller.signal.aborted) setChoices([first, ...rest].flatMap(page => page.items)
          .filter(item => item.kind === 's3' && item.credentialsConfigured && item.failureDomainConfigured))
      } catch (reason) {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason.message : 'Не удалось загрузить назначения.')
      } finally { if (!controller.signal.aborted) setLoading(false) }
    }
    void load()
    return () => controller.abort()
  }, [apiBase, mode])

  const close = () => { if (!busy) onClose() }
  const onDialogKeyDown = (event: KeyboardEvent<HTMLElement>) => {
    if (event.defaultPrevented) return
    if (event.key === 'Escape') { event.preventDefault(); close(); return }
    if (event.key !== 'Tab') return
    const focusables = Array.from(dialog.current?.querySelectorAll<HTMLElement>(
      'button:not(:disabled),input:not(:disabled)') ?? []).filter(element => element.getClientRects().length > 0)
    if (focusables.length === 0) return
    const first = focusables[0]
    const last = focusables[focusables.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
  }
  const updateRoute = (index: number, next: PoolRouteDraft) =>
    setPool(current => ({...current, routes: current.routes.map((route, at) => at === index ? next : route)}))

  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (busy) return
    if (mode === 'destination' && !destination.endpoint.trim().startsWith('https://')) {
      setError('S3 endpoint должен использовать HTTPS.')
      return
    }
    if (mode === 'pool') {
      const ids = pool.routes.map(route => route.destinationId)
      if (ids.some(id => !id) || new Set(ids).size !== ids.length ||
          pool.desiredVerifiedCopies > ids.length || pool.desiredVerifiedCopies < pool.requiredVerifiedCopies) {
        setError('Выберите разные S3-назначения для каждого маршрута и проверьте числа обязательных и желаемых копий.')
        return
      }
    }
    setBusy(true)
    setError('')
    try {
      const endpoint = mode === 'destination' ? 'backups/destinations/s3' : 'backups/pools'
      const body = mode === 'destination' ? destination : pool
      const response = await fetch(`${apiBase}/api/v1/admin/${endpoint}`, {
        method: 'POST', credentials: 'include', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)
      })
      if (!response.ok) throw new Error(await safeError(response, 'Не удалось сохранить конфигурацию backup.'))
      const created = await response.json() as { id: string; name: string }
      onSaved(mode === 'destination'
        ? {kind: 'destination', name: created.name}
        : {kind: 'pool', name: created.name, id: created.id})
    } catch (reason) { setError(reason instanceof Error ? reason.message : 'Не удалось сохранить конфигурацию backup.') }
    finally { setBusy(false) }
  }

  return <div className="source-editor-backdrop" role="presentation" onMouseDown={event => {if (event.target === event.currentTarget) close()}}>
    <section ref={dialog} className="source-editor-modal backup-provision-modal" role="dialog" aria-modal="true"
      aria-labelledby="backup-provision-title" onKeyDown={onDialogKeyDown}>
      <div className="source-editor-heading"><div><span className="kicker">BACKUP DESTINATIONS</span>
        <h2 id="backup-provision-title">{mode === 'destination' ? 'Добавить S3-назначение' : 'Создать protection pool'}</h2>
        <p>{mode === 'destination'
          ? 'Ключи сохраняются только в зашифрованном виде. Назначение создаётся выключенным и без маршрута.'
          : 'Pool и маршруты создаются вместе. Это не переключает production routing и не проверяет S3-доступность.'}</p>
      </div><button type="button" className="icon-button" aria-label="Закрыть окно" disabled={busy} onClick={close}><X width={18} height={18}/></button></div>
      <form className="source-editor-form" autoComplete="off" onSubmit={event => void submit(event)}>
        {mode === 'destination' ? <>
          <div className="source-editor-grid">
            <label>Название<input autoFocus required maxLength={120} value={destination.name} onChange={event => setDestination({...destination, name: event.target.value})}/></label>
            <label>Приоритет<input type="number" required min={0} value={destination.priority} onChange={event => setDestination({...destination, priority: Number(event.target.value)})}/></label>
            <label>HTTPS endpoint<input type="url" required placeholder="https://s3.example.com" value={destination.endpoint} onChange={event => setDestination({...destination, endpoint: event.target.value})}/></label>
            <label>Регион<input required maxLength={100} placeholder="eu-west-1" value={destination.region} onChange={event => setDestination({...destination, region: event.target.value})}/></label>
            <label>Bucket<input required maxLength={63} value={destination.bucket} onChange={event => setDestination({...destination, bucket: event.target.value})}/></label>
            <label>Префикс<input maxLength={512} value={destination.prefix} onChange={event => setDestination({...destination, prefix: event.target.value})}/></label>
            <label>Access key<input type="password" required minLength={3} maxLength={256} autoComplete="new-password" data-1p-ignore="true" value={destination.accessKey} onChange={event => setDestination({...destination, accessKey: event.target.value})}/></label>
            <label>Secret key<input type="password" required minLength={8} maxLength={1024} autoComplete="new-password" data-1p-ignore="true" value={destination.secretKey} onChange={event => setDestination({...destination, secretKey: event.target.value})}/></label>
          </div>
          <Toggle checked={destination.usePathStyle} onChange={usePathStyle => setDestination({...destination, usePathStyle})} label="Path-style адресация" disabled={busy}/>
        </> : <>
          <div className="source-editor-grid">
            <label>Имя pool<input autoFocus required maxLength={120} value={pool.name} onChange={event => setPool({...pool, name: event.target.value})}/></label>
            <label>Обязательных проверенных копий<input type="number" min={1} max={16} required value={pool.requiredVerifiedCopies} onChange={event => setPool({...pool, requiredVerifiedCopies: Number(event.target.value)})}/></label>
            <label>Желаемых проверенных копий<input type="number" min={1} max={16} required value={pool.desiredVerifiedCopies} onChange={event => setPool({...pool, desiredVerifiedCopies: Number(event.target.value)})}/></label>
            <label>Попыток за цикл<input type="number" min={1} max={20} required value={pool.maxAttemptsPerCycle} onChange={event => setPool({...pool, maxAttemptsPerCycle: Number(event.target.value)})}/></label>
            <label>Общий deadline, секунд<input type="number" min={30} max={86400} required value={pool.overallDeadlineSeconds} onChange={event => setPool({...pool, overallDeadlineSeconds: Number(event.target.value)})}/></label>
            <label>Окно до failback, секунд<input type="number" min={0} max={604800} required value={pool.failbackHealthyForSeconds} onChange={event => setPool({...pool, failbackHealthyForSeconds: Number(event.target.value)})}/></label>
          </div>
          {loading ? <p className="backup-provision-note">Загружаем S3-назначения…</p> : choices.length === 0
            ? <p className="backup-provision-note">Подходящих S3-назначений нет. Сначала создайте destination.</p>
            : <div className="backup-pool-routes"><b>Маршруты PUT / VERIFY / READ</b>{pool.routes.map((route, index) => {
              const selected = choices.find(choice => choice.id === route.destinationId)
              return <div className="backup-pool-route" key={index}>
                <label>Назначение<StyledSelect ariaLabel={`S3 назначение маршрута ${index + 1}`} value={route.destinationId}
                  onChange={destinationId => updateRoute(index, {...route, destinationId, activateDestination: false})}
                  options={[["", "Выберите S3"], ...choices.map(choice => [choice.id, `${choice.name}${choice.enabled ? '' : ' · выключено'}`] as const)]}/></label>
                <label>Роль<StyledSelect ariaLabel={`Роль маршрута ${index + 1}`} value={route.role}
                  onChange={role => updateRoute(index, {...route, role})}
                  options={[["primary", "Основной"], ["fallback", "Резервный"], ["secondary", "Дополнительный"]]}/></label>
                <label>Приоритет<input type="number" min={0} required value={route.priority}
                  onChange={event => updateRoute(index, {...route, priority: Number(event.target.value)})}/></label>
                {selected && !selected.enabled && <Toggle checked={route.activateDestination}
                  onChange={activateDestination => updateRoute(index, {...route, activateDestination})}
                  label={`Включить ${selected.name} при создании pool`} disabled={busy}/>}
                {pool.routes.length > 1 && <button type="button" className="icon-button danger" aria-label={`Убрать маршрут ${index + 1}`}
                  onClick={() => setPool(current => ({...current, routes: current.routes.filter((_, at) => at !== index)}))}><X width={16} height={16}/></button>}
              </div>
            })}<button type="button" className="secondary-admin-button" disabled={pool.routes.length >= 16 || busy}
              onClick={() => setPool(current => ({...current, routes: [...current.routes, emptyRoute((current.routes.length + 1) * 10)]}))}><Plus width={15} height={15}/>Добавить маршрут</button></div>}
          <p className="backup-provision-note">Разные endpoint сами по себе не доказывают независимость аккаунтов. Проверьте provider canary и восстановление до переключения production pool.</p>
        </>}
        {error && <p className="backup-destination-error" role="alert">{error}</p>}
        <div className="source-editor-actions"><span/><button type="button" className="secondary-admin-button" disabled={busy} onClick={close}>Отмена</button>
          <button type="submit" className="primary-admin-button" disabled={busy || loading || (mode === 'pool' && choices.length === 0)}>
            <ShieldCheck width={16} height={16}/>{busy ? 'Сохраняем…' : mode === 'destination' ? 'Добавить выключенное S3' : 'Создать pool'}</button></div>
      </form>
    </section>
  </div>
}
