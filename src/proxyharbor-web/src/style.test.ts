import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { join } from 'node:path'

// Читаем реальный source-файл: CSS import в jsdom намеренно возвращает пустой
// модуль и не может служить regression-контрактом для production stylesheet.
const stylesheet = readFileSync(join(process.cwd(), 'src', 'style.css'), 'utf8')

describe('responsive accessibility stylesheet', () => {
  it('keeps primary mobile controls and feed links at touch-friendly sizes', () => {
    // Этот контракт дополняет browser QA: случайное удаление responsive-правил
    // должно ломать обычный frontend test gate до публикации образа.
    expect(stylesheet).toContain('header>.brand{min-height:44px}')
    expect(stylesheet).toContain('.provider-feeds summary,.provider-grid a{display:flex;align-items:center;min-height:24px}')
    expect(stylesheet).toContain('.mobile-admin{width:44px;height:44px}')
    expect(stylesheet).toContain('.provider-feeds summary,.provider-grid a{min-height:44px}')
    expect(stylesheet).toContain('.tabs button{min-width:44px;min-height:44px}')
    expect(stylesheet).toContain('.filters input{height:44px}')
    expect(stylesheet).toContain('.close,.diagnostics-heading button{width:44px;height:44px;padding:0;display:grid;place-items:center}')
    expect(stylesheet).toContain('.key-input input,.key-input button,.admin-actions button,.source-form input,.source-form select,.source-form button,.source-controls button{min-height:44px}')
    expect(stylesheet).toContain('.source-controls button{min-width:44px}')
  })

  it('overrides Chromium autofill without hiding saved credentials', () => {
    expect(stylesheet).toContain('.login-field input:-webkit-autofill')
    expect(stylesheet).toContain('-webkit-text-fill-color:#e9f6f2!important')
    expect(stylesheet).toContain('0 0 0 1000px #091410 inset!important')
  })

  it('uses a full-width, full-height admin workspace with bottom-aligned registries', () => {
    expect(stylesheet).toContain('.admin-section{display:flex;flex-direction:column;width:100%;min-height:calc(100vh - 76px)')
    expect(stylesheet).toContain('.admin-page-heading{display:flex;align-items:center;justify-content:space-between;gap:20px;width:100%;height:auto;min-height:44px;max-width:none;padding:0;margin:0 0 16px;border:0}')
    expect(stylesheet).toContain('.admin-breadcrumb h1{overflow:hidden;margin:0')
    expect(stylesheet).toContain('.proxy-inventory-card>.pagination,.users-registry>.pagination')
    expect(stylesheet).toContain('margin-top:auto;margin-bottom:-5px')
  })
})
