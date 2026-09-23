import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { StyledSelect } from './StyledSelect'

describe('StyledSelect', () => {
  afterEach(() => { cleanup(); vi.restoreAllMocks() })

  it('opens upward when a scroll container clips the space below', () => {
    const original=HTMLElement.prototype.getBoundingClientRect
    const rect=(top:number,bottom:number):DOMRect => ({top,bottom,left:0,right:300,width:300,height:bottom-top,x:0,y:top,toJSON:()=>({})})
    vi.spyOn(HTMLElement.prototype,'getBoundingClientRect').mockImplementation(function(this:HTMLElement){
      if(this.classList.contains('styled-select'))return rect(170,210)
      if(this.classList.contains('styled-select-menu'))return rect(217,357)
      if(this.dataset.scrollContainer)return rect(0,240)
      return original.call(this)
    })
    const {container}=render(<div data-scroll-container="true" style={{overflowY:'auto'}}>
      <StyledSelect ariaLabel="Обрезаемый список" value="one" onChange={()=>undefined}
        options={[["one","Первый"],["two","Второй"],["three","Третий"]]}/>
    </div>)
    fireEvent.click(screen.getByRole('button',{name:'Обрезаемый список'}))
    expect(container.querySelector('.styled-select')).toHaveClass('open-up')
  })

  it('opens and moves focus through options with the keyboard', async () => {
    const onChange=vi.fn()
    render(<StyledSelect ariaLabel="Тестовый список" value="one" onChange={onChange} options={[["one","Первый"],["two","Второй"],["three","Третий"]]}/>)

    const trigger=screen.getByRole('button',{name:'Тестовый список'})
    fireEvent.keyDown(trigger,{key:'ArrowDown'})
    const first=screen.getByRole('option',{name:'Первый'})
    await waitFor(()=>expect(first).toHaveFocus())
    fireEvent.keyDown(first,{key:'ArrowDown'})
    const second=screen.getByRole('option',{name:'Второй'})
    await waitFor(()=>expect(second).toHaveFocus())
    fireEvent.click(second)

    expect(onChange).toHaveBeenCalledWith('two')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    await waitFor(()=>expect(trigger).toHaveFocus())
  })

  it('closes with Escape and preserves the selected value', async () => {
    render(<StyledSelect ariaLabel="Тестовый список" value="two" onChange={()=>undefined} options={[["one","Первый"],["two","Второй"]]}/>)
    const trigger=screen.getByRole('button',{name:'Тестовый список'})
    fireEvent.click(trigger)
    const selected=screen.getByRole('option',{name:'Второй'})
    selected.focus()
    fireEvent.keyDown(selected,{key:'Escape'})
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(trigger).toHaveTextContent('Второй')
    await waitFor(()=>expect(trigger).toHaveFocus())
  })

  it('renders an optional decorative icon in the value and options', () => {
    const { container }=render(<StyledSelect ariaLabel="Страна" value="de" onChange={()=>undefined} options={[["","Все страны"],["de","Германия",<span className="test-flag" aria-hidden="true"/>]]}/>)
    const trigger=screen.getByRole('button',{name:'Страна'})
    expect(trigger.querySelector('.test-flag')).toBeInTheDocument()
    fireEvent.click(trigger)
    expect(container.querySelectorAll('.test-flag')).toHaveLength(2)
    expect(screen.getByRole('option',{name:'Германия'})).toBeInTheDocument()
  })
})
