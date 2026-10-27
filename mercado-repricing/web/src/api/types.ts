export interface ColumnMapping {store:string;sku:string;targetPrice:string;site?:string|null;itemId?:string|null;variationId?:string|null;promotionId?:string|null;basePrice?:string|null;allowExit?:string|null;allowBaseChange?:string|null;note?:string|null;action?:string|null}
export interface ImportInfo {importId:string;fileName:string;sheets:{name:string;headers:string[];rowCount:number;suggestedMapping:ColumnMapping}[]}
export interface PriceInput {store:string;site:string;currency:string;sku:string;targetPrice:number;basePrice:number|null;allowExit:boolean;allowBaseChange:boolean;action?:string|null;storeName?:string|null}
export interface ValidatedRow {sourceRow:number;sheet:string;original:Record<string,string>;input:PriceInput|null;errors:string[];mapped?:Record<string,string>|null}
export interface ValidationResult {validationId:string;importId:string;rows:ValidatedRow[];stores?:ZiniaoStore[]|null;warnings?:string[]|null}
export type JobStatus='Ready'|'Running'|'Pausing'|'Paused'|'Completed'|'Interrupted'|'AwaitingVerification'|'Verifying';
export type RowStatus='Invalid'|'Pending'|'Running'|'Simulated'|'Succeeded'|'Skipped'|'Failed'|'NeedsReview'|'Inspected'|'RetryPending'|'AwaitingVerification';
export interface VerificationNotice {automatic?:boolean;requestId:string;jobId:string;rowId:string;storeId:string;storeName:string;sku:string;message:string;requestedAt:string}
export interface JobSummary {storeNames?:string[];hasSourceFile?:boolean;id:string;fileName:string;mode:'Simulation'|'Live'|'ReadOnly'|'PriceCut'|'WeekendEnrollment';weekend?:{start:string;end:string;label:string}|null;status:JobStatus;createdAt:string;total:number;finished:number;round?:number;mixedPrices?:boolean;basePrices?:boolean;verification?:VerificationNotice|null}
export interface JobRow {id:string;source:ValidatedRow;status:RowStatus;route:string;message:string;checkpoint:string;attempts?:number;observation?:PriceObservation|null}
export interface JobDetail {summary:JobSummary;rows:JobRow[]}
export interface JobEvent {id:number;jobId:string;at:string;message:string}
export interface CliDiagnostic {available:boolean;message:string;checkedAt:string}
export interface ZiniaoStore {id:string;name:string;platform:string}
export interface PriceObservation {storeId:string;itemId:string;title:string;basePriceText:string;currentPrice:number|null;difference:number|null;checkedAt:string;identityNote:string;promotions:{id:string;name:string;status:string;priceText:string;active:boolean}[]}
export interface Environment {service:string;liveEnabled:boolean;priceCutEnabled:boolean;readOnlyEnabled:boolean;cli:CliDiagnostic}
export const jobLabels:Record<JobStatus,string>={Ready:'待执行',Running:'执行中',Pausing:'正在暂停',Paused:'已暂停',Completed:'已结束',Interrupted:'中断待核对',Verifying:'等待自动验证',AwaitingVerification:'等待店铺验证'};
export const rowLabels:Record<RowStatus,string>={Invalid:'数据异常',Pending:'待执行',Running:'执行中',Simulated:'模拟完成',Succeeded:'实际成功',Skipped:'已跳过',Failed:'执行失败',NeedsReview:'待核对',Inspected:'指定商品已读取',RetryPending:'待下轮重试',AwaitingVerification:'等待验证'};

export const modeLabels={Simulation:'模拟流程',Live:'已禁用',ReadOnly:'只读核对',PriceCut:'实际调价',WeekendEnrollment:'周末活动报名'};
