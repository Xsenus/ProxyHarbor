import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { PrivacyControls } from './PrivacyControls'

describe('PrivacyControls', () => {
  beforeEach(() => localStorage.clear())
  afterEach(cleanup)

  it('keeps optional analytics disabled until a user explicitly accepts it', () => {
    render(<PrivacyControls/>)
    expect(screen.getByRole('dialog',{name:'Настройки конфиденциальности'})).toBeVisible()
    fireEvent.click(screen.getByRole('button',{name:'Только необходимые'}))
    expect(localStorage.getItem('proxyharbor.analytics-consent.v1')).toBe('rejected')
    expect(screen.queryByRole('dialog',{name:'Настройки конфиденциальности'})).not.toBeInTheDocument()
  })

  it('reopens the settings and records explicit analytics consent', () => {
    localStorage.setItem('proxyharbor.analytics-consent.v1','rejected')
    render(<PrivacyControls/>)
    fireEvent.click(screen.getByRole('button',{name:'Настройки cookies'}))
    fireEvent.click(screen.getByRole('button',{name:'Разрешить статистику'}))
    expect(localStorage.getItem('proxyharbor.analytics-consent.v1')).toBe('accepted')
  })
})
