import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { NotificationBridge, ToastProvider, ToastSignal } from './Toasts'

describe('ToastProvider', () => {
  afterEach(() => {
    cleanup()
    window.history.replaceState({}, '', '/')
    vi.restoreAllMocks()
  })

  it('shows errors in an accessible toast and lets the user dismiss it', async () => {
    render(<ToastProvider><ToastSignal kind="error" message="Сервис временно недоступен"/></ToastProvider>)

    expect(await screen.findByRole('alert')).toHaveTextContent('Сервис временно недоступен')
    fireEvent.click(screen.getByRole('button', { name: 'Закрыть уведомление' }))
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('offers an optional retry action once', async () => {
    const retry = vi.fn()
    render(<ToastProvider><ToastSignal kind="error" message="Ошибка сети" action={{ label: 'Повторить', run: retry }}/></ToastProvider>)

    fireEvent.click(await screen.findByRole('button', { name: 'Повторить' }))
    expect(retry).toHaveBeenCalledOnce()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('does not poll protected notifications on public or authentication pages', async () => {
    const request = vi.spyOn(globalThis, 'fetch')
    window.history.replaceState({}, '', '/login')

    render(<ToastProvider><NotificationBridge/></ToastProvider>)
    await Promise.resolve()

    expect(request).not.toHaveBeenCalled()
  })

  it('polls notifications inside the authenticated account', async () => {
    const request = vi.spyOn(globalThis, 'fetch').mockResolvedValue(new Response('[]', {
      status: 200,
      headers: { 'content-type': 'application/json' },
    }))
    window.history.replaceState({}, '', '/account')

    render(<ToastProvider><NotificationBridge/></ToastProvider>)

    await waitFor(() => expect(request).toHaveBeenCalledWith('/api/v1/account/notifications', { credentials: 'include' }))
  })
})
