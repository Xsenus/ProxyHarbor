import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from 'react'
import { Check, ChevronDown } from 'lucide-react'

export type StyledSelectOption = readonly [value: string, label: string]

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
export function StyledSelect({value,onChange,options,ariaLabel='Выбор значения',disabled=false,leadingIcon}:StyledSelectProps){
  const [open,setOpen]=useState(false)
  const root=useRef<HTMLDivElement>(null)
  const trigger=useRef<HTMLButtonElement>(null)
  const optionRefs=useRef<Array<HTMLButtonElement|null>>([])
  const listboxId=useId()
  const selectedIndex=Math.max(0,options.findIndex(([key])=>key===value))
  const selected=options[selectedIndex]

  useEffect(()=>{
    if(!open)return
    const close=(event:PointerEvent)=>{if(!root.current?.contains(event.target as Node))setOpen(false)}
    document.addEventListener('pointerdown',close)
    return()=>document.removeEventListener('pointerdown',close)
  },[open])

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

  return <div className={`styled-select${open?' open':''}${disabled?' disabled':''}`} ref={root}>
    <button ref={trigger} type="button" className="styled-select-trigger" aria-label={ariaLabel} aria-haspopup="listbox" aria-controls={listboxId} aria-expanded={open} disabled={disabled} onKeyDown={onTriggerKeyDown} onClick={()=>setOpen(current=>!current)}>{leadingIcon&&<span className="styled-select-leading" aria-hidden="true">{leadingIcon}</span>}<span className="styled-select-value">{selected?.[1]??value}</span><ChevronDown className="styled-select-chevron"/></button>
    {open&&<div id={listboxId} className="styled-select-menu" role="listbox" aria-label={ariaLabel}>{options.map(([key,label],index)=><button ref={element=>{optionRefs.current[index]=element}} key={key} type="button" role="option" aria-selected={key===value} onKeyDown={event=>onOptionKeyDown(event,index)} onClick={()=>choose(key)}><span>{label}</span>{key===value&&<Check/>}</button>)}</div>}
  </div>
}
