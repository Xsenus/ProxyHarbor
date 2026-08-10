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
})
