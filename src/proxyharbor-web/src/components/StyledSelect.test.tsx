import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { StyledSelect } from './StyledSelect'

describe('StyledSelect', () => {
  afterEach(cleanup)

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
})
