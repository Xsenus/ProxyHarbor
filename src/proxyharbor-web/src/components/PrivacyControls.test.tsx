import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PrivacyControls } from './PrivacyControls'
import { analyticsAllowed, writeAnalyticsChoice } from '../privacyPreferences'

describe('PrivacyControls', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
    writeAnalyticsChoice('rejected')
    localStorage.clear()
  })

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

  it('remains usable when browser storage is unavailable', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('Storage denied') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage denied') })
    render(<PrivacyControls/>)
    fireEvent.click(screen.getByRole('button', {name:'Только необходимые'}))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(analyticsAllowed()).toBe(false)
    fireEvent.click(screen.getByRole('button', {name:'Настройки cookies'}))
    expect(screen.getByRole('dialog')).toBeVisible()
    fireEvent.click(screen.getByRole('button', {name:'Разрешить статистику'}))
    expect(analyticsAllowed()).toBe(true)
  })
})
