import { describe, expect, it, vi } from 'vitest';
import { renderToStaticMarkup } from 'react-dom/server';
import { App } from './main';

describe('dashboard UI', () => {
  it('renders the real workspace creation form', () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: true, json: async () => [] })));
    const html = renderToStaticMarkup(<App />);
    expect(html).toContain('Operação do pipeline');
    expect(html).toContain('Novo workspace');
    expect(html).toContain('Nome');
    expect(html).toContain('Criar workspace');
  });
});
