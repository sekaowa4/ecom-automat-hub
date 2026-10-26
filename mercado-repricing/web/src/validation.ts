export interface ImportSelection { importId:string; sheet:string; mapping:Record<string,string|null|undefined>; defaultSite:string; selectedStoreId?:string }
export function invalidateValidationOnChange(previous:ImportSelection,next:ImportSelection):boolean {
  return previous.importId!==next.importId || previous.sheet!==next.sheet || previous.defaultSite!==next.defaultSite || previous.selectedStoreId!==next.selectedStoreId || [...new Set([...Object.keys(previous.mapping),...Object.keys(next.mapping)])].some(key=>previous.mapping[key]!==next.mapping[key]);
}
