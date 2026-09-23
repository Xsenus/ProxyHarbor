import { cleanup, render, screen } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { BackupProtectionDetail } from './BackupProtectionDetail'

describe('BackupProtectionDetail', () => {
  afterEach(() => {
    cleanup()
    vi.unstubAllGlobals()
  })

  it('does not present a legacy backup as protected', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({
      assessment: 'legacy_unassessed', state: null, copies: [],
    }), { status: 200 })))

    render(<BackupProtectionDetail backupId="legacy" apiBaseUrl="" />)

    expect(await screen.findByText(/это прежняя запись без снимка политики/)).toBeInTheDocument()
    expect(screen.queryByText('Защищён')).not.toBeInTheDocument()
  })

  it('shows a safe retry message when the request fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 503 })))

    render(<BackupProtectionDetail backupId="failed" apiBaseUrl="" />)

    expect(await screen.findByRole('alert')).toHaveTextContent('Не удалось получить состояние защиты')
    expect(screen.queryByText('Защищён')).not.toBeInTheDocument()
  })
})
