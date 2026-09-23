import { useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Check, ChevronDown } from 'lucide-react'

export type StyledSelectOption = readonly [value: string, label: string, icon?: ReactNode]

type StyledSelectProps = {
  value: string
  onChange: (value: string) => void
  options: readonly StyledSelectOption[]
  ariaLabel?: string
  disabled?: boolean
  leadingIcon?: ReactNode
}

/**
 * Единый выпадающий список ProxyHarbor. Компонент не использует системное меню
 * браузера, поэтому одинаково выглядит на Windows, Linux и мобильных устройствах.
 */
export function StyledSelect({value,onChange,options,ariaLabel,disabled=false,leadingIcon}:StyledSelectProps){
  const [open,setOpen]=useState(false)
  const [openUp,setOpenUp]=useState(false)
  const root=useRef<HTMLDivElement>(null)
  const trigger=useRef<HTMLButtonElement>(null)
  const optionRefs=useRef<Array<HTMLButtonElement|null>>([])
  const listboxId=useId()
  const selectedIndex=Math.max(0,options.findIndex(([key])=>key===value))
  const selected=options[selectedIndex]
  const resolvedAriaLabel=ariaLabel ?? (options.some(([key])=>key==='auto') ? 'Политика подключения Telegram' : 'Выбор значения')

  useEffect(()=>{
    if(!open)return
    const close=(event:PointerEvent)=>{if(!root.current?.contains(event.target as Node))setOpen(false)}
    document.addEventListener('pointerdown',close)
    return()=>document.removeEventListener('pointerdown',close)
  },[open])

  useLayoutEffect(()=>{
    if(!open||!root.current)return
    const bounds=root.current.getBoundingClientRect()
    const menu=root.current.querySelector<HTMLElement>('.styled-select-menu')
    if(!menu)return
    let top=0
    let bottom=window.innerHeight
    for(let ancestor=root.current.parentElement;ancestor;ancestor=ancestor.parentElement){
      if(!/(auto|scroll|hidden|clip)/.test(getComputedStyle(ancestor).overflowY))continue
      const rect=ancestor.getBoundingClientRect()
      top=Math.max(top,rect.top)
      bottom=Math.min(bottom,rect.bottom)
    }
    const required=menu.getBoundingClientRect().height+7
    setOpenUp(bottom-bounds.bottom<required&&bounds.top-top>bottom-bounds.bottom)
  },[open,options.length])

  const focusOption=(index:number)=>window.setTimeout(()=>optionRefs.current[index]?.focus(),0)
  const closeAndFocus=()=>{setOpen(false);window.setTimeout(()=>trigger.current?.focus(),0)}
  const choose=(next:string)=>{onChange(next);closeAndFocus()}
  const onTriggerKeyDown=(event:KeyboardEvent<HTMLButtonElement>)=>{
    if(event.key==='ArrowDown'||event.key==='ArrowUp'){
      event.preventDefault()
      if(disabled)return
      setOpen(true)
      focusOption(event.key==='ArrowDown'?selectedIndex:Math.max(0,options.length-1))
    }else if(event.key==='Escape'&&open){event.preventDefault();closeAndFocus()}
  }
  const onOptionKeyDown=(event:KeyboardEvent<HTMLButtonElement>,index:number)=>{
    if(event.key==='ArrowDown'){event.preventDefault();focusOption((index+1)%options.length)}
    else if(event.key==='ArrowUp'){event.preventDefault();focusOption((index-1+options.length)%options.length)}
    else if(event.key==='Home'){event.preventDefault();focusOption(0)}
    else if(event.key==='End'){event.preventDefault();focusOption(options.length-1)}
    else if(event.key==='Escape'){event.preventDefault();closeAndFocus()}
    else if(event.key==='Tab')setOpen(false)
  }

  return <div className={`styled-select${open?' open':''}${openUp?' open-up':''}${disabled?' disabled':''}`} ref={root}>
    <button ref={trigger} type="button" className="styled-select-trigger" aria-label={resolvedAriaLabel} aria-haspopup="listbox" aria-controls={listboxId} aria-expanded={open} disabled={disabled} onKeyDown={onTriggerKeyDown} onClick={()=>setOpen(current=>!current)}>{leadingIcon&&<span className="styled-select-leading" aria-hidden="true">{leadingIcon}</span>}<span className="styled-select-value">{selected?.[2]}{selected?.[1]??value}</span><ChevronDown className="styled-select-chevron"/></button>
    {open&&<div id={listboxId} className="styled-select-menu" role="listbox" aria-label={resolvedAriaLabel}>{options.map(([key,label,icon],index)=><button ref={element=>{optionRefs.current[index]=element}} key={key} type="button" role="option" aria-selected={key===value} onKeyDown={event=>onOptionKeyDown(event,index)} onClick={()=>choose(key)}><span>{icon}{label}</span>{key===value&&<Check/>}</button>)}</div>}
  </div>
}
