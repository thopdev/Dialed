import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// The loading screen words follow the browser language, like the app does (js/loading.js). Run against a fake document: no browser needed.
const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../src/Dialed.Web/wwwroot/js/loading.js'), 'utf8');

function load(languages: string[] | undefined, language: string) {
  const pour = { textContent: 'Pour', attrs: {} as Record<string, string>, setAttribute(n: string, v: string) { this.attrs[n] = v; } };
  const screen = { setAttribute() { }, querySelector: (s: string) => (s === '.dialed-loading-pour-word' ? pour : null) };
  const root = { style: { getPropertyValue: () => '' } };
  const document = { readyState: 'complete', documentElement: root, querySelector: (s: string) => (s === '.dialed-loading' ? screen : null) };
  class MutationObserver { observe() { } disconnect() { } }
  vm.runInNewContext(source, { document, navigator: { languages, language }, MutationObserver, parseFloat, String, isNaN });
  return pour;
}

test('Dutch browsers see "Gieten" with lang nl', () => {
  const pour = load(['nl-NL', 'en'], 'nl-NL');
  expect(pour.textContent).toBe('Gieten');
  expect(pour.attrs.lang).toBe('nl');
});

test('English and other languages keep "Pour"', () => {
  expect(load(['en-US'], 'en-US').textContent).toBe('Pour');
  expect(load(['de-DE', 'nl'], 'de-DE').textContent).toBe('Pour');
});

test('falls back to navigator.language, and to English when nothing is known', () => {
  expect(load(undefined, 'nl').textContent).toBe('Gieten');
  expect(load(undefined, '').textContent).toBe('Pour');
});
