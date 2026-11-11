import { expect, test } from '@playwright/test';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// The service worker's push handlers (#100), run against a fake worker scope: no browser needed.
const source = readFileSync(join(dirname(fileURLToPath(import.meta.url)), '../../src/Dialed.Web/wwwroot/push-handlers.js'), 'utf8');

type Page = { url: string; visibilityState: string; focused: boolean; messages: unknown[]; focusCalls: number; postMessage(m: unknown): void; focus(): Promise<void> };

function worker(pages: Partial<Page>[] = []) {
  const listeners: Record<string, (event: unknown) => void> = {};
  const shown: { title: string; options: any }[] = [];
  const opened: string[] = [];
  const clients = pages.map(p => ({ url: 'https://dialed.test/', visibilityState: 'hidden', focused: false, messages: [] as unknown[], focusCalls: 0, ...p } as Page))
    .map(p => Object.assign(p, { postMessage: (m: unknown) => p.messages.push(m), focus: async () => { p.focusCalls++; } }));
  const scope = {
    addEventListener: (type: string, fn: (event: unknown) => void) => { listeners[type] = fn; },
    registration: { scope: 'https://dialed.test/', showNotification: async (title: string, options: any) => { shown.push({ title, options }); } },
    clients: { matchAll: async () => clients, openWindow: async (url: string) => { opened.push(url); } },
  };
  vm.runInNewContext(source, { self: scope, URL });
  const run = async (type: string, event: any) => {
    let pending: Promise<unknown> = Promise.resolve();
    listeners[type]({ ...event, waitUntil: (p: Promise<unknown>) => { pending = p; } });
    await pending;
  };
  const push = (data: unknown) => run('push', { data: { json: () => { if (data === 'bad') throw new Error('not json'); return data; } } });
  const click = (url: string) => run('notificationclick', { notification: { data: { url }, close: () => { } } });
  return { shown, opened, clients, push, click };
}

test('a push shows a notification with the title, line and link', async () => {
  const w = worker();
  await w.push({ title: 'New follow request', body: 'Ada wants to follow you.', url: 'people', tag: 'follow-request' });
  expect(w.shown).toHaveLength(1);
  expect(w.shown[0].title).toBe('New follow request');
  expect(w.shown[0].options.body).toBe('Ada wants to follow you.');
  expect(w.shown[0].options.tag).toBe('follow-request');
  expect(w.shown[0].options.data.url).toBe('people');
});

test('a push the open window already shows is passed to it and not shown as a banner', async () => {
  const w = worker([{ visibilityState: 'visible', focused: true }]);
  await w.push({ title: 'New household member', body: 'Joe joined your household.', url: 'settings#settings-household', tag: 'household-joined' });
  expect(w.shown).toHaveLength(0);
  expect(w.clients[0].messages).toEqual([{ type: 'dialed-push', url: 'settings#settings-household', tag: 'household-joined' }]);
});

test('a hidden window does not stop the banner', async () => {
  const w = worker([{ visibilityState: 'hidden', focused: false }]);
  await w.push({ title: 'T', body: 'B', url: 'people', tag: 'x' });
  expect(w.shown).toHaveLength(1);
});

test('a push that is not valid is ignored', async () => {
  const w = worker();
  await w.push('bad');
  await w.push(null);
  await w.push({ body: 'no title' });
  expect(w.shown).toHaveLength(0);
});

test('a link that leaves the app is dropped', async () => {
  const w = worker();
  await w.push({ title: 'T', body: 'B', url: 'https://evil.example/phish', tag: 'x' });
  await w.push({ title: 'T', body: 'B', url: '//evil.example', tag: 'y' });
  await w.push({ title: 'T', body: 'B', url: 'javascript:alert(1)', tag: 'z' });
  expect(w.shown.map(s => s.options.data.url)).toEqual(['', '', '']);
});

test('tapping a notification focuses the open app and asks it to open the screen', async () => {
  const w = worker([{}]);
  await w.click('people');
  expect(w.clients[0].focusCalls).toBe(1);
  expect(w.clients[0].messages).toEqual([{ type: 'dialed-navigate', url: 'people' }]);
  expect(w.opened).toEqual([]);
});

test('tapping a notification with the app closed opens it at the link', async () => {
  const w = worker();
  await w.click('settings#settings-household');
  expect(w.opened).toEqual(['https://dialed.test/settings#settings-household']);
});

test('tapping a notification with a bad link opens the app start page', async () => {
  const w = worker();
  await w.click('https://evil.example/');
  expect(w.opened).toEqual(['https://dialed.test/']);
});
