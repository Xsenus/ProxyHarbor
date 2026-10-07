import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import AdminVpnPage from './AdminVpnPage'
import { ToastProvider } from './components/Toasts'

const endpoint = { id: 'one', host: 'vpn.example.net', port: 443, protocol: 'AnyTls', transport: 'tcp', status: 'Pending', firstSeenAt: new Date().toISOString(), lastSeenAt: new Date().toISOString(), successfulChecks: 0, failedChecks: 0, successRate: 0, knownForSeconds: 0 }
const page = (items: unknown[]) => ({ items, page: 1, pageSize: 10, total: items.length, countries: [], summary: {} })
const renderPage = () => render(<ToastProvider><AdminVpnPage/></ToastProvider>)

describe('Admin VPN configuration controls', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/admin/vpn')
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(page([{ ...endpoint, hasClashConfiguration: true }, { ...endpoint, id: 'two', host: 'uri.example.net', connectionUri: 'vless://ready' }])))))
  })
  afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals() })

  it('offers YAML only for saved configurations and reports a download failure in the shared toast', async () => {
    vi.mocked(fetch).mockImplementation(async input => String(input).endsWith('/one/clash')
      ? new Response('', { status: 409 })
      : new Response(JSON.stringify(page([{ ...endpoint, hasClashConfiguration: true }, { ...endpoint, id: 'two', connectionUri: 'vless://ready' }]))))
    renderPage()
    const download = await screen.findByRole('button', { name: 'Скачать Clash YAML' })
    expect(screen.getAllByRole('button', { name: 'Скачать Clash YAML' })).toHaveLength(1)
    expect(screen.getByRole('button', { name: 'Копировать конфигурацию VPN' })).toBeInTheDocument()
    fireEvent.click(download)
    expect(await screen.findByRole('alert')).toHaveTextContent('Сохранённую конфигурацию нельзя безопасно экспортировать.')
    expect(fetch).toHaveBeenCalledWith('/api/v1/admin/vpn/endpoints/one/clash', { credentials: 'include' })
  })

  it.each(['AnyTls', 'Hysteria', 'ShadowsocksR', 'HttpProxy', 'Socks4Proxy', 'Socks5Proxy'])('filters the new %s protocol through StyledSelect', async protocol => {
    const { container } = renderPage()
    await screen.findByText('vpn.example.net:443')
    const trigger = screen.getByRole('button', { name: 'VPN протокол' })
    fireEvent.keyDown(trigger, { key: 'ArrowDown' })
    await waitFor(() => expect(screen.getByRole('option', { name: 'Все протоколы' })).toHaveFocus())
    const options = screen.getAllByRole('option')
    const targetIndex = options.findIndex(option => option.textContent === protocol)
    for (let index = 0; index < targetIndex; index++) {
      fireEvent.keyDown(options[index], { key: 'ArrowDown' })
      await waitFor(() => expect(options[index + 1]).toHaveFocus())
    }
    fireEvent.click(options[targetIndex])
    await waitFor(() => expect(fetch).toHaveBeenCalledWith(expect.stringContaining(`protocol=${protocol}`), { credentials: 'include' }))
    await waitFor(() => expect(trigger).toHaveFocus())
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(container.querySelector('select')).toBeNull()
  })

  it('saves an authenticated source using a newly supported protocol', async () => {
    window.history.replaceState({}, '', '/admin/vpn?tab=sources')
    vi.mocked(fetch).mockResolvedValue(new Response(JSON.stringify(page([]))))
    renderPage()
    await screen.findByText('По вашему запросу VPN-источники не найдены.')
    fireEvent.click(screen.getByRole('button', { name: 'Добавить feed' }))
    fireEvent.change(screen.getByLabelText('Название'), { target: { value: 'New YAML feed' } })
    fireEvent.change(screen.getByLabelText('Провайдер'), { target: { value: 'Publisher' } })
    fireEvent.change(screen.getByLabelText('HTTPS URL'), { target: { value: 'https://example.net/clash.yaml' } })
    fireEvent.keyDown(screen.getByRole('button', { name: 'Протокол VPN feed' }), { key: 'ArrowDown' })
    fireEvent.click(screen.getByRole('option', { name: 'AnyTls' }))
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }))
    await waitFor(() => expect(fetch).toHaveBeenCalledWith('/api/v1/admin/vpn/sources', expect.objectContaining({ method: 'POST', credentials: 'include', body: expect.stringContaining('"protocol":"AnyTls"') })))
  })
})
