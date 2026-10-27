export interface DashboardShop {id:string;name:string;targetCny:number}
export interface BatchMapping {fileName:string;importedAt:string|null;unmatched:number;values:Record<string,string>}
export interface DashboardData {
 store:string;currency:string;retrievedAt:string;reportDate:string;monthThrough:string;rate:number;targetCny:number;targetBrl:number|null;
 yesterdaySales:number;yesterdayOrders:number;yesterdayUnits:number;growth:number|null;monthSales:number;monthOrders:number;completion:number|null;gapBrl:number|null;remainingDays:number;requiredDaily:number|null;
 history:{date:string;sales:number;orders:number}[];categories:{name:string;sales:number;share:number|null}[];categoryTotal:number;
 ranking:{sku:string;name:string;sales:number;orders:number;units:number;margin:number|null;category:string}[];profitBrl:number|null;margin:number|null;profitIssues:string[];
}
const amountFormatter=new Intl.NumberFormat('en-US',{minimumFractionDigits:2,maximumFractionDigits:2});
export const money=(n:number|null|undefined)=>n==null?'—':amountFormatter.format(n);
export const percent=(n:number|null|undefined)=>n==null?'—':`${(n*100).toFixed(2)}%`;
export const categoryColors:Record<string,string>={P:'#48efbc',A:'#b097ff',B:'#ffab8b',KA:'#ffe275','未分类':'#83d6ff'};
