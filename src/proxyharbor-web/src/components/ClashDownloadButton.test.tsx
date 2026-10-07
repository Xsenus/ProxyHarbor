import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ClashDownloadButton } from './ClashDownloadButton'
import { I18nProvider, type Language } from '../i18n'
import { clashDownloadMessages } from '../clashDownloadMessages'

describe('ClashDownloadButton', () => {
  const onError = vi.fn()
  beforeEach(() => {
    onError.mockReset()
    vi.stubGlobal('fetch', vi.fn())
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: vi.fn(() => 'blob:clash-download') })
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() })
  })
  afterEach(() => { cleanup(); localStorage.clear(); vi.useRealTimers(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

  it.each<Language>(['ru', 'en', 'de', 'fr', 'zh'])('localizes public download actions and unavailable errors in %s', async language => {
    localStorage.setItem('proxyharbor.language', language)
    vi.mocked(fetch).mockResolvedValue(new Response('secret', { status: 404 }))
    render(<I18nProvider><ClashDownloadButton exportQuery="" api="" onError={onError}/></I18nProvider>)
    fireEvent.click(screen.getByRole('button', { name: clashDownloadMessages[language].download }))
    await waitFor(() => expect(onError).toHaveBeenLastCalledWith(clashDownloadMessages[language].unavailable))
  })

  it('uses the administrator session, preserves the downloaded YAML and releases its URL', async () => {
    const blob = new Blob(['proxies: []'], { type: 'application/yaml' })
    vi.mocked(fetch).mockResolvedValue({ ok: true, headers: new Headers({ 'Content-Type': 'application/yaml; charset=utf-8' }), blob: async () => blob } as Response)
    let saved: { href: string; filename: string } | undefined
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { saved = { href: this.href, filename: this.download } })
    vi.useFakeTimers()
    render(<ClashDownloadButton endpointId="endpoint-1" api="" onError={onError}/>)
    const button = screen.getByRole('button', { name: 'Скачать Clash YAML' })
    button.focus()
    fireEvent.click(button)
    await vi.advanceTimersByTimeAsync(0)
    expect(fetch).toHaveBeenCalledExactlyOnceWith('/api/v1/admin/vpn/endpoints/endpoint-1/clash', { credentials: 'include' })
    expect(URL.createObjectURL).toHaveBeenCalledWith(blob)
    expect(saved).toEqual({ href: 'blob:clash-download', filename: 'proxyharbor-endpoint-1.yaml' })
    expect(button).toHaveFocus()
    expect(document.querySelector('a[download]')).toBeNull()
    expect(onError).toHaveBeenCalledExactlyOnceWith('')
    await vi.advanceTimersByTimeAsync(10_000)
    expect(URL.revokeObjectURL).toHaveBeenCalledExactlyOnceWith('blob:clash-download')
  })

  it('prevents a second request while a download is pending and allows a retry after failure', async () => {
    let resolve!: (value: Response) => void
    vi.mocked(fetch).mockReturnValue(new Promise<Response>(done => { resolve = done }))
    render(<ClashDownloadButton endpointId="one" api="" onError={onError}/>)
    fireEvent.click(screen.getByRole('button'))
    const pending = screen.getByRole('button', { name: 'Скачиваем Clash YAML' })
    expect(pending).toBeDisabled()
    expect(pending).toHaveAttribute('aria-busy', 'true')
    fireEvent.click(pending)
    expect(fetch).toHaveBeenCalledOnce()
    resolve(new Response('', { status: 409 }))
    await waitFor(() => expect(screen.getByRole('button', { name: 'Скачать Clash YAML' })).toBeEnabled())
    expect(onError).toHaveBeenLastCalledWith('Сохранённую конфигурацию нельзя безопасно экспортировать.')
    fireEvent.click(screen.getByRole('button'))
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it.each([401, 403, 404, 409, 500])('does not download error responses or expose their secret bodies (%i)', async status => {
    vi.mocked(fetch).mockResolvedValue(new Response('{"detail":"secret-key"}', { status, headers: { 'Content-Type': 'application/json' } }))
    render(<ClashDownloadButton endpointId="one" api="" onError={onError}/>)
    fireEvent.click(screen.getByRole('button'))
    await waitFor(() => expect(onError).toHaveBeenCalledTimes(2))
    expect(onError.mock.lastCall?.[0]).not.toContain('secret-key')
    expect(URL.createObjectURL).not.toHaveBeenCalled()
    expect(screen.getByRole('button')).toBeEnabled()
  })

  it.each(['text/html', 'application/json', 'application/yaml-invalid', ''])('rejects successful responses with an unexpected MIME type (%s)', async contentType => {
    vi.mocked(fetch).mockResolvedValue(new Response('login or problem', { headers: { 'Content-Type': contentType } }))
    render(<ClashDownloadButton endpointId="one" api="" onError={onError}/>)
    fireEvent.click(screen.getByRole('button'))
    await waitFor(() => expect(onError).toHaveBeenLastCalledWith('Сервер вернул неожиданный формат конфигурации.'))
    expect(URL.createObjectURL).not.toHaveBeenCalled()
  })

  it('rejects an empty YAML download', async () => {
    vi.mocked(fetch).mockResolvedValue(new Response('', { headers: { 'Content-Type': 'application/yaml' } }))
    render(<ClashDownloadButton endpointId="one" api="" onError={onError}/>)
    fireEvent.click(screen.getByRole('button'))
    await waitFor(() => expect(onError).toHaveBeenLastCalledWith('Сервер вернул пустую конфигурацию.'))
    expect(URL.createObjectURL).not.toHaveBeenCalled()
  })

  it.each([401, 403, 404, 409, 429, 503])('reports public export failures without leaking response bodies (%i)', async status => {
    vi.mocked(fetch).mockResolvedValue(new Response('private-password', { status }))
    render(<ClashDownloadButton exportQuery="protocol=Vless&country=DE" api="" onError={onError}/>)
    fireEvent.click(screen.getByRole('button'))
    await waitFor(() => expect(onError).toHaveBeenCalledTimes(2))
    expect(onError.mock.lastCall?.[0]).not.toContain('private-password')
    expect(onError.mock.lastCall?.[0]).not.toContain('админку')
    expect(URL.createObjectURL).not.toHaveBeenCalled()
    expect(fetch).toHaveBeenCalledExactlyOnceWith('/api/v1/vpn/export/clash?protocol=Vless&country=DE', { credentials: 'include' })
  })

  it('reports network failures without exposing URLs', async () => {
    vi.mocked(fetch).mockRejectedValue(new TypeError('https://secret.example/?key=secret-key'))
    render(<ClashDownloadButton endpointId="one" api="" onError={onError}/>)
    fireEvent.click(screen.getByRole('button'))
    await waitFor(() => expect(onError).toHaveBeenLastCalledWith('Не удалось скачать конфигурацию Clash YAML. Попробуйте ещё раз.'))
    expect(URL.createObjectURL).not.toHaveBeenCalled()
  })
})
