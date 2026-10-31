// One page for every window (SPEC §38): the window's label picks its view. Task windows (R9) add theirs, numbered
// when several run at once ("tidy-2").

export const views = ['search'] as const;
export type View = (typeof views)[number];

/** The view a window's label names; an unknown label shows the Search window. */
export function viewFor(label: string): View {
  const name = label.replace(/-\d+$/, '');
  return (views as readonly string[]).includes(name) ? (name as View) : 'search';
}
