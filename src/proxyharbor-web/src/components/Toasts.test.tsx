import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ToastProvider, ToastSignal } from './Toasts'

describe('ToastProvider', () => {
  afterEach(cleanup)

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
})
