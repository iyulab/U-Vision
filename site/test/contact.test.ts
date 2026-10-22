import { describe, it, expect } from 'vitest';
import { COPY, CONTACT_URL, contactUrl } from '../src/copy';

describe('contact links', () => {
  it('name the product, the request type and the topic the visitor asked from', () => {
    const url = new URL(contactUrl('scenario-counting'));
    expect(`${url.origin}${url.pathname}`).toBe(CONTACT_URL);
    expect(Object.fromEntries(url.searchParams)).toEqual({
      product: 'u-vision',
      intent: 'general',
      topic: 'scenario-counting',
    });
    expect(new URL(contactUrl('hero', 'demo')).searchParams.get('intent')).toBe('demo');
  });

  it('every roadmap scenario has a claim to ask by', () => {
    for (const copy of Object.values(COPY))
      for (const s of copy.scenarios.items.filter(s => s.roadmap)) expect(s.claim).toMatch(/^[\w-]+$/);
  });
});
