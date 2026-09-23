import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import { afterEach, expect, it, vi } from 'vitest'
import { AdminBackupProvisioning } from './AdminBackupProvisioning'

const json = (body: unknown, status = 200) => new Response(JSON.stringify(body),
  {status, headers: {'Content-Type': 'application/json'}})

afterEach(() => { cleanup(); vi.unstubAllGlobals() })

it('submits S3 credentials only to the write-only endpoint and reports a disabled destination', async () => {
  const saved = vi.fn()
  const secret = 'test-secret-key-for-ui'
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, options?: RequestInit) => {
    expect(String(input)).toBe('/api/v1/admin/backups/destinations/s3')
    expect(options?.method).toBe('POST')
    return json({id:'destination-1',name:'S3 NL',kind:'s3',enabled:false})
  }))
  render(<AdminBackupProvisioning mode="destination" apiBase="" onClose={vi.fn()} onSaved={saved}/>)
  const dialog = screen.getByRole('dialog',{name:'Добавить S3-назначение'})
  expect(within(dialog).getByLabelText('Название')).toHaveFocus()
  fireEvent.change(within(dialog).getByLabelText('Название'),{target:{value:'S3 NL'}})
  fireEvent.change(within(dialog).getByLabelText('HTTPS endpoint'),{target:{value:'https://s3.example.test'}})
  fireEvent.change(within(dialog).getByLabelText('Регион'),{target:{value:'eu-west-1'}})
  fireEvent.change(within(dialog).getByLabelText('Bucket'),{target:{value:'private-backups'}})
  fireEvent.change(within(dialog).getByLabelText('Access key'),{target:{value:'test-access-key'}})
  fireEvent.change(within(dialog).getByLabelText('Secret key'),{target:{value:secret}})
  expect(within(dialog).getByLabelText('Access key')).toHaveAttribute('type','password')
  expect(within(dialog).getByLabelText('Secret key')).toHaveAttribute('type','password')
  fireEvent.click(within(dialog).getByRole('button',{name:'Добавить выключенное S3'}))
  await waitFor(() => expect(saved).toHaveBeenCalledWith({kind:'destination',name:'S3 NL'}))
  const call = vi.mocked(fetch).mock.calls[0]
  expect(JSON.parse(String(call[1]?.body))).toMatchObject({
    name:'S3 NL',endpoint:'https://s3.example.test',bucket:'private-backups',secretKey:secret
  })
  expect(JSON.stringify(saved.mock.calls)).not.toContain(secret)
})

it('rejects a non-HTTPS S3 endpoint before sending credentials', async () => {
  const send = vi.fn()
  vi.stubGlobal('fetch', send)
  render(<AdminBackupProvisioning mode="destination" apiBase="" onClose={vi.fn()} onSaved={vi.fn()}/>)
  fireEvent.change(screen.getByLabelText('Название'),{target:{value:'S3 test'}})
  fireEvent.change(screen.getByLabelText('HTTPS endpoint'),{target:{value:'http://s3.example.test'}})
  fireEvent.change(screen.getByLabelText('Регион'),{target:{value:'eu-west-1'}})
  fireEvent.change(screen.getByLabelText('Bucket'),{target:{value:'private-backups'}})
  fireEvent.change(screen.getByLabelText('Access key'),{target:{value:'test-access-key'}})
  fireEvent.change(screen.getByLabelText('Secret key'),{target:{value:'test-secret-key'}})
  fireEvent.click(screen.getByRole('button',{name:'Добавить выключенное S3'}))
  expect(await screen.findByRole('alert')).toHaveTextContent('HTTPS')
  expect(send).not.toHaveBeenCalled()
})

it('creates a pool from S3 destinations on different pages and keeps dropdown Escape inside the dialog', async () => {
  const saved = vi.fn()
  const first = {id:'s3-first',name:'S3 NL',kind:'s3',enabled:false,credentialsConfigured:true,failureDomainConfigured:true}
  const second = {id:'s3-second',name:'S3 DE',kind:'s3',enabled:true,credentialsConfigured:true,failureDomainConfigured:true}
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, options?: RequestInit) => {
    const url = String(input)
    if (url.includes('page=1')) return json({items:[first],total:101})
    if (url.includes('page=2')) return json({items:[second],total:101})
    if (url.endsWith('/backups/pools') && options?.method === 'POST')
      return json({id:'pool-1',name:'durable',policyVersion:1},201)
    return json({title:'Unexpected request'},500)
  }))
  render(<AdminBackupProvisioning mode="pool" apiBase="" onClose={vi.fn()} onSaved={saved}/>)
  const dialog = screen.getByRole('dialog',{name:'Создать protection pool'})
  await screen.findByRole('button',{name:'S3 назначение маршрута 1'})
  fireEvent.change(within(dialog).getByLabelText('Имя pool'),{target:{value:'durable'}})
  fireEvent.change(within(dialog).getByLabelText('Желаемых проверенных копий'),{target:{value:'2'}})
  fireEvent.click(within(dialog).getByRole('button',{name:'S3 назначение маршрута 1'}))
  const firstOption = within(dialog).getByRole('option',{name:'S3 NL · выключено'})
  fireEvent.keyDown(firstOption,{key:'Escape'})
  expect(screen.getByRole('dialog',{name:'Создать protection pool'})).toBeInTheDocument()
  fireEvent.click(within(dialog).getByRole('button',{name:'S3 назначение маршрута 1'}))
  fireEvent.click(within(dialog).getByRole('option',{name:'S3 NL · выключено'}))
  fireEvent.click(within(dialog).getByRole('switch',{name:'Включить S3 NL при создании pool'}))
  fireEvent.click(within(dialog).getByRole('button',{name:'Добавить маршрут'}))
  fireEvent.click(within(dialog).getByRole('button',{name:'S3 назначение маршрута 2'}))
  fireEvent.click(within(dialog).getByRole('option',{name:'S3 DE'}))
  fireEvent.click(within(dialog).getByRole('button',{name:'Создать pool'}))
  await waitFor(() => expect(saved).toHaveBeenCalledWith({kind:'pool',name:'durable',id:'pool-1'}))
  const call = vi.mocked(fetch).mock.calls.find(([input,options]) =>
    String(input).endsWith('/backups/pools') && options?.method === 'POST')
  expect(JSON.parse(String(call?.[1]?.body))).toMatchObject({
    name:'durable',desiredVerifiedCopies:2,routes:[
      {destinationId:'s3-first',role:'primary',activateDestination:true},
      {destinationId:'s3-second',role:'fallback',activateDestination:false}
    ]
  })
})
