import { cleanup, render, screen } from '@testing-library/react'
import axe, { type Result } from 'axe-core'
import { afterEach, beforeEach, describe, it, vi } from 'vitest'
import App from './App'

const stats = {
  alive: 0, staleAlive: 0, pending: 1, dead: 0, dueForCheck: 1,
  checksInProgress: 0, scheduledChecks: 0, averageLatencyMs: null,
  sources: 81, failingSources: 0, repeatedlyFailingSources: 0, byProtocol: [],
}

const axeOptions = {
  // jsdom не вычисляет итоговые CSS-цвета. Contrast остаётся задачей browser-аудита;
  // DOM/ARIA/landmark/name/keyboard-related правила выполняются полностью.
  rules: { 'color-contrast': { enabled: false } },
}

describe('ProxyHarbor accessibility', () => {
  beforeEach(() => {
    sessionStorage.clear()
    window.history.replaceState({}, '', '/')
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse(stats)
      if (url.includes('/api/v1/proxies')) {
        return jsonResponse({ items: [], page: 1, pageSize: 25, total: 0 })
      }
      return jsonResponse({ title: 'Unexpected request' }, 500)
    }))
  })

  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
  })

  it('has no automated WCAG violations in the loaded public dashboard', async () => {
    render(<App />)
    await screen.findByText('система активна')

    assertNoViolations(await axe.run(document.body, axeOptions))
  })

  it('has no automated WCAG violations in a populated proxy table', async () => {
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/stats')) return jsonResponse({ ...stats, alive: 1 })
      if (url.includes('/api/v1/proxies')) {
        return jsonResponse({
          items: [{
            host: '1.1.1.1', port: 8080, protocol: 'Http', url: 'http://1.1.1.1:8080',
            latencyMs: 120, successRate: 100, exitIp: '1.1.1.1', lastCheckedAt: new Date().toISOString(),
          }],
          page: 1, pageSize: 25, total: 1,
        })
      }
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })

    render(<App />)
    await screen.findByRole('cell', { name: /1\.1\.1\.1/ })

    assertNoViolations(await axe.run(document.body, axeOptions))
  })

  it('has no automated WCAG violations on the dedicated login page', async () => {
    window.history.replaceState({}, '', '/admin/login')
    render(<App />)
    await screen.findByRole('heading', { name: 'Вход в управление' })

    assertNoViolations(await axe.run(document.body, axeOptions))
  })

  it('has no automated WCAG violations in the sectioned admin workspace', async () => {
    window.history.replaceState({}, '', '/admin')
    vi.mocked(fetch).mockImplementation(async input => {
      const url = String(input)
      if (url.includes('/api/v1/admin/sources')) return jsonResponse([])
      if (url.includes('/api/v1/admin/diagnostics')) return jsonResponse({
        serverTime: new Date().toISOString(), databaseBytes: 0,
        validationQueue: { total: 0, due: 0 }, sourceCatalog: undefined,
        recentRuns: [], recentValidationRuns: [], recentBackups: [],
      })
      return jsonResponse({ title: 'Unexpected request' }, 500)
    })
    render(<App />)
    await screen.findByRole('heading', { name: 'Обзор', level: 1 })

    assertNoViolations(await axe.run(document.body, axeOptions))
  })
})

function assertNoViolations(results: { violations: Result[] }) {
  if (results.violations.length === 0) return
  const summary = results.violations.map(violation => ({
    id: violation.id,
    impact: violation.impact,
    targets: violation.nodes.map(node => node.target),
  }))
  throw new Error(`axe обнаружил нарушения: ${JSON.stringify(summary)}`)
}

function jsonResponse(body: unknown, status = 200) {
  return Promise.resolve(new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  }))
}
