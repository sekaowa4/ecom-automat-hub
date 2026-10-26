let tokenPromise:Promise<string>|null=null;
async function token(){return tokenPromise??=fetch('/api/session').then(async r=>{if(!r.ok)throw Error('无法连接本地服务，请重新启动程序。');return (await r.json()).token as string;}).catch(e=>{tokenPromise=null;throw e;});}
async function request(path:string,options:RequestInit={}):Promise<Response>{
  const headers=new Headers(options.headers);
  if(options.method && !['GET','HEAD'].includes(options.method)){headers.set('X-Local-Token',await token());if(!(options.body instanceof FormData))headers.set('Content-Type','application/json');}
  let response:Response;
  try{response=await fetch('/api'+path,{...options,headers});}catch(e){if(e instanceof DOMException&&e.name==='AbortError')throw e;throw Error('本地服务连接中断，请检查程序是否仍在运行。');}
  if(!response.ok){if(response.status===403){tokenPromise=null;throw Error('本地会话已失效，请刷新页面后重试。');}const problem=await response.json().catch(()=>null);throw Error(problem?.title||`请求失败（${response.status}）`);}
  return response;
}
export async function api<T>(path:string,options:RequestInit={}):Promise<T>{const response=await request(path,options);return response.headers.get('content-type')?.includes('json')?response.json():undefined as T;}
export async function cropLabels(file:File,signal:AbortSignal,kind:'product'|'carton'='product'){
  const form=new FormData();form.append('file',file);
  const response=await request(`/labels/${kind}/crop`,{method:'POST',body:form,signal});
  const labels=Number(response.headers.get('X-Label-Count'));
  if(!response.headers.get('content-type')?.includes('application/pdf')||!Number.isInteger(labels)||labels<1)throw Error('剪裁结果不完整，请重新处理。');
  return {blob:await response.blob(),labels};
}
export function post<T>(path:string,body:unknown={}){return api<T>(path,{method:'POST',body:JSON.stringify(body)});}
export function message(error:unknown){return error instanceof Error?error.message:'操作失败，请重试。';}
