/**
 * Script-blind person-name comparison for the live "is this a third party?" hint in the admin
 * forms. The backend (PersonName.cs) is the authority; this only mirrors it so the operator sees the
 * warning before submitting.
 */
// Mirrors backend PersonName.LooksLikeSamePerson for the live hint in the add-account form.
// Georgian/Cyrillic → lowercase Latin, words compared as sets (order/middle name irrelevant).
const GEO: Record<string, string> = {
  'ა':'a','ბ':'b','გ':'g','დ':'d','ე':'e','ვ':'v','ზ':'z','თ':'t','ი':'i','კ':'k','ლ':'l','მ':'m','ნ':'n','ო':'o',
  'პ':'p','ჟ':'zh','რ':'r','ს':'s','ტ':'t','უ':'u','ფ':'p','ქ':'k','ღ':'gh','ყ':'q','შ':'sh','ჩ':'ch','ც':'ts',
  'ძ':'dz','წ':'ts','ჭ':'ch','ხ':'kh','ჯ':'j','ჰ':'h',
};
const CYR: Record<string, string> = {
  'а':'a','б':'b','в':'v','г':'g','д':'d','е':'e','ё':'e','ж':'zh','з':'z','и':'i','й':'i','к':'k','л':'l','м':'m',
  'н':'n','о':'o','п':'p','р':'r','с':'s','т':'t','у':'u','ф':'f','х':'kh','ц':'ts','ч':'ch','ш':'sh','щ':'sh',
  'ъ':'','ы':'y','ь':'','э':'e','ю':'yu','я':'ya',
};
const FOLDS: Array<[string, string]> = [
  ['ph', 'p'], ['th', 't'], ['gh', 'g'], ['kh', 'h'], ['zh', 'j'], ['dz', 'z'],
  ['ts', 'c'], ['tz', 'c'], ['ch', 'c'], ['sh', 's'],
  ['q', 'k'], ['y', 'i'], ['w', 'v'], ['f', 'p'], ['x', 'ks'],
];
function translit(word: string): string {
  let out = '';
  for (const ch of word.normalize('NFD')) {
    if (GEO[ch] !== undefined) { out += GEO[ch]; continue; }
    const lo = ch.toLowerCase();
    if (CYR[lo] !== undefined) { out += CYR[lo]; continue; }
    if (lo >= 'a' && lo <= 'z') out += lo;
  }
  // Same folding as backend PersonName.Fold: common Latin spelling variants → one letter, no doubles.
  for (const [from, to] of FOLDS) out = out.split(from).join(to);
  return out.replace(/(.)\1+/g, '$1');
}
function nameWords(name: string): Set<string> {
  return new Set(name.split(/[\s\-,.'’]+/).map(translit).filter(w => w.length >= 2));
}
export function namesLookAlike(a: string, b: string): boolean {
  const wa = nameWords(a), wb = nameWords(b);
  if (wa.size === 0 || wb.size === 0) return false;
  const [shorter, longer] = wa.size <= wb.size ? [wa, wb] : [wb, wa];
  for (const w of shorter) if (!longer.has(w)) return false;
  return true;
}
