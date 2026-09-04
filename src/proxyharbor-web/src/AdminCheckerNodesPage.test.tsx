import { act, cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import AdminCheckerNodesPage from './AdminCheckerNodesPage'

const node={id:'node-1',name:'VPS 1',host:'203.0.113.20',sshPort:22,sshUsername:'root',enabled:true,concurrency:100,batchSize:200,createdAt:'2026-09-01T00:00:00Z',updatedAt:'2026-09-01T00:00:00Z',deploymentStatus:'online',completedChecks:0,aliveChecks:0,online:false,busy:false}
const snapshot=(patch:Partial<typeof node>={})=>({image:'checker:test',nativeAssetBaseUrl:'https://example.test',items:[{...node,...patch}]})
const json=(value:unknown,status=200)=>new Response(JSON.stringify(value),{status,headers:{'Content-Type':'application/json'}})
function deferred(){let resolve!:(value:Response)=>void;const promise=new Promise<Response>(done=>{resolve=done});return {promise,resolve}}
async function start(){render(<AdminCheckerNodesPage/>);await act(async()=>{await vi.advanceTimersByTimeAsync(0)})}
async function clickSave(){await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Сохранить'}))})}

describe('checker node drafts and refresh ordering',()=>{
  beforeEach(()=>{vi.useFakeTimers();vi.spyOn(globalThis,'fetch').mockImplementation(async()=>json(snapshot()))})
  afterEach(()=>{cleanup();vi.useRealTimers();vi.restoreAllMocks()})

  it('preserves edited fields during automatic and manual refresh while updating untouched fields and health',async()=>{
    await start()
    fireEvent.change(screen.getByLabelText('Параллельно'),{target:{value:'250'}})
    vi.mocked(fetch).mockImplementation(async()=>json(snapshot({concurrency:120,batchSize:400,online:true})))
    await act(async()=>{await vi.advanceTimersByTimeAsync(30000)})
    expect(screen.getByLabelText('Параллельно')).toHaveValue(250)
    expect(screen.getByLabelText('Партия')).toHaveValue(400)
    expect(screen.getByText('На связи',{selector:'.state-pill'})).toBeInTheDocument()
    vi.mocked(fetch).mockImplementation(async()=>json(snapshot({batchSize:500})))
    await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Обновить узлы'}))})
    expect(screen.getByLabelText('Параллельно')).toHaveValue(250)
    expect(screen.getByLabelText('Партия')).toHaveValue(500)
    await clickSave()
    const put=vi.mocked(fetch).mock.calls.find(([,options])=>options?.method==='PUT')
    expect(JSON.parse(String(put?.[1]?.body))).toEqual({enabled:true,concurrency:250,batchSize:500})
  })

  it('preserves edits made during save, skips polling during mutation and clears only the later saved draft',async()=>{
    const pending=deferred()
    await start()
    fireEvent.change(screen.getByLabelText('Параллельно'),{target:{value:'250'}})
    vi.mocked(fetch).mockImplementation(async(_input,options)=>options?.method==='PUT'?pending.promise:json(snapshot({concurrency:250})))
    await clickSave()
    expect(screen.getByRole('switch')).toBeDisabled()
    expect(screen.getByRole('button',{name:'Сохранить'})).toBeDisabled()
    fireEvent.change(screen.getByLabelText('Параллельно'),{target:{value:'300'}})
    const readsBefore=vi.mocked(fetch).mock.calls.filter(([,options])=>options?.method!=='PUT').length
    await act(async()=>{await vi.advanceTimersByTimeAsync(30000)})
    expect(vi.mocked(fetch).mock.calls.filter(([,options])=>options?.method!=='PUT')).toHaveLength(readsBefore)
    await act(async()=>{pending.resolve(json({}));await pending.promise})
    expect(screen.getByLabelText('Параллельно')).toHaveValue(300)
    vi.mocked(fetch).mockImplementation(async(_input,options)=>json(options?.method==='PUT'?{}:snapshot({concurrency:300})))
    await clickSave()
    vi.mocked(fetch).mockImplementation(async()=>json(snapshot({concurrency:350})))
    await act(async()=>{await vi.advanceTimersByTimeAsync(30000)})
    expect(screen.getByLabelText('Параллельно')).toHaveValue(350)
  })

  it('retains unsaved values after a failed save and the next refresh',async()=>{
    await start()
    fireEvent.change(screen.getByLabelText('Партия'),{target:{value:'777'}})
    vi.mocked(fetch).mockResolvedValueOnce(json({message:'Не сохранено'},500))
    await clickSave()
    vi.mocked(fetch).mockImplementation(async()=>json(snapshot()))
    await act(async()=>{await vi.advanceTimersByTimeAsync(30000)})
    expect(screen.getByLabelText('Партия')).toHaveValue(777)
  })

  it('ignores an older read completing after a newer read',async()=>{
    await start()
    const stale=deferred()
    vi.mocked(fetch).mockReturnValueOnce(stale.promise)
    fireEvent.click(screen.getByRole('button',{name:'Обновить узлы'}))
    const signal=vi.mocked(fetch).mock.calls.at(-1)?.[1]?.signal
    vi.mocked(fetch).mockResolvedValueOnce(json(snapshot({batchSize:900})))
    await act(async()=>{fireEvent.click(screen.getByRole('button',{name:'Обновить узлы'}))})
    expect(signal?.aborted).toBe(true)
    await act(async()=>{stale.resolve(json(snapshot({batchSize:10})));await stale.promise})
    expect(screen.getByLabelText('Партия')).toHaveValue(900)
  })

  it('aborts a pre-save read so it cannot roll back a successful mutation',async()=>{
    await start()
    const stale=deferred()
    vi.mocked(fetch).mockReturnValueOnce(stale.promise)
    fireEvent.click(screen.getByRole('button',{name:'Обновить узлы'}))
    const signal=vi.mocked(fetch).mock.calls.at(-1)?.[1]?.signal
    fireEvent.change(screen.getByLabelText('Параллельно'),{target:{value:'250'}})
    vi.mocked(fetch).mockImplementation(async(_input,options)=>json(options?.method==='PUT'?{}:snapshot({concurrency:250})))
    await clickSave()
    expect(signal?.aborted).toBe(true)
    await act(async()=>{stale.resolve(json(snapshot()));await stale.promise})
    expect(screen.getByLabelText('Параллельно')).toHaveValue(250)
  })

  it('aborts reads and stops polling after unmount',async()=>{
    const pending=deferred()
    vi.mocked(fetch).mockReturnValue(pending.promise)
    const view=render(<AdminCheckerNodesPage/>)
    await act(async()=>{await vi.advanceTimersByTimeAsync(0)})
    const signal=vi.mocked(fetch).mock.calls.at(-1)?.[1]?.signal
    view.unmount()
    expect(signal?.aborted).toBe(true)
    await act(async()=>{pending.resolve(json(snapshot()));await pending.promise;await vi.advanceTimersByTimeAsync(60000)})
    expect(fetch).toHaveBeenCalledTimes(1)
  })
})
