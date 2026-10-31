import { describe, expect, it } from 'vitest';
import { viewFor } from './view';

describe('viewFor', () => {
  it('names the view of a window label, numbered or not', () => {
    expect(viewFor('search')).toBe('search');
    expect(viewFor('search-2')).toBe('search');
  });

  it('shows the Search window for a label it does not know', () => {
    expect(viewFor('unknown')).toBe('search');
    expect(viewFor('')).toBe('search');
  });
});
