import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import App from './App'

const stats = {
  alive: 0,
  staleAlive: 0,
  pending: 1,
  dead: 0,
  dueForCheck: 1,
  scheduledChecks: 0,
  averageLatencyMs: null,
  sources: 81,
  failingSources: 0,
  repeatedlyFailingSources: 0,
  byProtocol: [],
}

describe('ProxyHarbor UI', () => {
  beforeEach(() => {
    sessionStorage.clear()
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0 })
      if (url.includes('/api/v1/admin/sources')) return jsonResponse({ title: 'Неверный ключ администратора' }, 401)
      return jsonResponse({ title: 'Unexpected request' }, 500)
    }))
  })

  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('keeps public API healthy when admin authentication fails', async () => {
    render(<App />)
    expect(await screen.findByText('система активна')).toBeInTheDocument()

    fireEvent.click(screen.getByRole('button', { name: /^Управление$/ }))
    fireEvent.change(screen.getByLabelText('Ключ администратора'), { target: { value: 'wrong-key' } })
    fireEvent.click(screen.getByRole('button', { name: 'Войти' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Неверный ключ администратора')
    expect(screen.getByText('система активна')).toBeInTheDocument()
    expect(screen.queryByText('API недоступен')).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Запустить сбор' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Проверить пакет' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Создать backup' })).toBeDisabled()
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).includes('/api/v1/admin/sources'))).toHaveLength(1)
  })

  it('closes the admin dialog with Escape and restores focus', async () => {
    render(<App />)
    await screen.findByText('система активна')
    const trigger = screen.getByRole('button', { name: /^Управление$/ })
    fireEvent.click(trigger)

    await waitFor(() => expect(screen.getByLabelText('Ключ администратора')).toHaveFocus())
    fireEvent.keyDown(document, { key: 'Escape' })

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    expect(trigger).toHaveFocus()
  })
})

function jsonResponse(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}
