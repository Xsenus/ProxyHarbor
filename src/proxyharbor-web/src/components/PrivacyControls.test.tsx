import { cleanup, fireEvent, render, screen } from '@testing-library/react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PrivacyControls as PrivacyControlsView } from './PrivacyControls'
import { SiteSettingsContext } from '../siteSettingsContext'
import { defaultSiteSettings } from '../siteSettingsModel'
import { analyticsAllowed, writeAnalyticsChoice } from '../privacyPreferences'
import { publicInfoPaths } from '../publicInfoRoutes'

function PrivacyControls() {
  return <SiteSettingsContext.Provider value={{settings:defaultSiteSettings, loading:false, analyticsReady:true, refresh:async () => defaultSiteSettings}}>
    <PrivacyControlsView/>
  </SiteSettingsContext.Provider>
}

describe('PrivacyControls', () => {
  beforeEach(() => {
    localStorage.clear()
    window.history.replaceState({}, '', '/')
  })
  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
    writeAnalyticsChoice('rejected')
    localStorage.clear()
    window.history.replaceState({}, '', '/')
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

  it('closes the initial dialog when another tab records a choice', () => {
    render(<PrivacyControls/>)
    expect(screen.getByRole('dialog')).toBeVisible()
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'rejected')
    fireEvent(window, new StorageEvent('storage', {key:'proxyharbor.analytics-consent.v1', newValue:'rejected'}))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(screen.getByRole('button', {name:'Настройки cookies'})).toBeVisible()
    expect(analyticsAllowed()).toBe(false)
  })

  it('updates an open settings dialog after another tab revokes consent', () => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted')
    render(<PrivacyControls/>)
    fireEvent.click(screen.getByRole('button', {name:'Настройки cookies'}))
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'rejected')
    fireEvent(window, new StorageEvent('storage', {key:'proxyharbor.analytics-consent.v1', newValue:'rejected'}))
    expect(screen.getByRole('dialog')).toBeVisible()
    expect(screen.getByText('Необязательная статистика отключена.')).toBeVisible()
    expect(analyticsAllowed()).toBe(false)
  })

  it.each(['proxyharbor.analytics-consent.v1', null])('requires a new choice when another tab clears consent (%s)', (key) => {
    localStorage.setItem('proxyharbor.analytics-consent.v1', 'accepted')
    render(<PrivacyControls/>)
    localStorage.clear()
    fireEvent(window, new StorageEvent('storage', {key, newValue:null}))
    expect(screen.getByRole('dialog')).toHaveAttribute('aria-modal', 'true')
    expect(screen.queryByRole('button', {name:'Закрыть'})).not.toBeInTheDocument()
    expect(analyticsAllowed()).toBe(false)
  })

  it.each(Object.keys(publicInfoPaths))('allows reading %s without recording consent', (path) => {
    window.history.replaceState({}, '', path)
    render(<PrivacyControls/>)
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(localStorage.getItem('proxyharbor.analytics-consent.v1')).toBeNull()
    expect(analyticsAllowed()).toBe(false)
    fireEvent(window, new StorageEvent('storage', {key:null}))
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })

  it('opens dismissible preferences while reading, without interpreting dismissal as consent', () => {
    window.history.replaceState({}, '', '/cookies/?source=notice')
    render(<PrivacyControls/>)
    const trigger = screen.getByRole('button', {name:'Настройки cookies'})
    trigger.focus()
    fireEvent.click(trigger)
    const dialog = screen.getByRole('dialog')
    expect(dialog).toHaveAttribute('aria-modal', 'false')
    expect(dialog).toHaveFocus()
    fireEvent.keyDown(dialog, {key:'Escape'})
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
    expect(localStorage.getItem('proxyharbor.analytics-consent.v1')).toBeNull()
    expect(analyticsAllowed()).toBe(false)
  })

  it.each(['/account', '/cookies/unrelated', '/privacy-extra'])('still requires a choice on %s', (path) => {
    window.history.replaceState({}, '', path)
    render(<PrivacyControls/>)
    expect(screen.getByRole('dialog')).toHaveAttribute('aria-modal', 'true')
  })

  it('keeps keyboard navigation inside the required dialog and does not dismiss it with Escape', () => {
    render(<PrivacyControls/>)
    const dialog = screen.getByRole('dialog')
    const first = screen.getByRole('link', {name:'Подробнее о cookies'})
    const last = screen.getByRole('button', {name:'Разрешить статистику'})
    expect(dialog).toHaveFocus()
    fireEvent.keyDown(dialog, {key:'Tab', shiftKey:true})
    expect(last).toHaveFocus()
    fireEvent.keyDown(last, {key:'Tab'})
    expect(first).toHaveFocus()
    fireEvent.keyDown(first, {key:'Tab', shiftKey:true})
    expect(last).toHaveFocus()
    fireEvent.keyDown(last, {key:'Escape'})
    expect(dialog).toBeVisible()
    expect(analyticsAllowed()).toBe(false)
  })

  it('does not trap keyboard focus on a disabled analytics choice when DNT is enabled', () => {
    const original = Object.getOwnPropertyDescriptor(navigator, 'doNotTrack')
    Object.defineProperty(navigator, 'doNotTrack', {configurable:true, value:'1'})
    try {
      render(<PrivacyControls/>)
      expect(screen.getByRole('button', {name:'Разрешить статистику'})).toBeDisabled()
      const first = screen.getByRole('link', {name:'Подробнее о cookies'})
      first.focus()
      fireEvent.keyDown(first, {key:'Tab', shiftKey:true})
      expect(screen.getByRole('button', {name:'Только необходимые'})).toHaveFocus()
    } finally {
      if (original) Object.defineProperty(navigator, 'doNotTrack', original)
      else Reflect.deleteProperty(navigator, 'doNotTrack')
    }
  })
})
