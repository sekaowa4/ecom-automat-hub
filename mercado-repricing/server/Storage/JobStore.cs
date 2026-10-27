using System.Text.Json;
using Microsoft.Data.Sqlite;
namespace Repricing.Storage;

public class JobStore
{
    public const int MaxAttempts=3;
    readonly object gate=new(); readonly string connectionString;
    public JobStore(string directory)
    {
        Directory.CreateDirectory(directory);connectionString=new SqliteConnectionStringBuilder{DataSource=Path.Combine(directory,"tasks.db"),Pooling=false}.ToString();
        using var db=Open();Run(db,"PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, validation_id TEXT UNIQUE, status TEXT NOT NULL, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS job_rows(id TEXT PRIMARY KEY, job_id TEXT NOT NULL, ordinal INTEGER NOT NULL, payload TEXT NOT NULL); CREATE TABLE IF NOT EXISTS job_events(id INTEGER PRIMARY KEY AUTOINCREMENT, job_id TEXT NOT NULL, at TEXT NOT NULL, message TEXT NOT NULL); CREATE TABLE IF NOT EXISTS job_sources(job_id TEXT PRIMARY KEY, content BLOB NOT NULL); CREATE INDEX IF NOT EXISTS event_job ON job_events(job_id,id);");
    }
    SqliteConnection Open(){var db=new SqliteConnection(connectionString);db.Open();return db;}
    static SqliteCommand Command(SqliteConnection db,string sql,params object?[] args){var c=db.CreateCommand();c.CommandText=sql;for(int i=0;i<args.Length;i++)c.Parameters.AddWithValue("$"+i,args[i]??DBNull.Value);return c;}
    static void Run(SqliteConnection db,string sql,params object?[] args){using var c=Command(db,sql,args);c.ExecuteNonQuery();}
    static T Read<T>(string s)=>JsonSerializer.Deserialize<T>(s)!;
    static void Event(SqliteConnection db,Guid id,string message)=>Run(db,"INSERT INTO job_events(job_id,at,message) VALUES($0,$1,$2)",id.ToString(),DateTimeOffset.UtcNow.ToString("O"),message);
    static void SaveSummary(SqliteConnection db,JobSummary summary)=>Run(db,"UPDATE jobs SET status=$1,payload=$2 WHERE id=$0",summary.Id.ToString(),summary.Status.ToString(),JsonSerializer.Serialize(summary));
    static void SaveRow(SqliteConnection db,JobRow row)=>Run(db,"UPDATE job_rows SET payload=$1 WHERE id=$0",row.Id.ToString(),JsonSerializer.Serialize(row));
    JobDetail Get(SqliteConnection db,Guid id)
    {
        using var c=Command(db,"SELECT payload FROM jobs WHERE id=$0",id.ToString());var json=c.ExecuteScalar() as string??throw new AppException("任务不存在。",404);var summary=Read<JobSummary>(json);
        using var rc=Command(db,"SELECT payload FROM job_rows WHERE job_id=$0 ORDER BY ordinal",id.ToString());using var reader=rc.ExecuteReader();var rows=new List<JobRow>();while(reader.Read())rows.Add(Read<JobRow>(reader.GetString(0)));
        using var source=Command(db,"SELECT COUNT(*) FROM job_sources WHERE job_id=$0",id.ToString());
        return new(summary with{StoreNames=rows.Where(r=>r.Source.Input!=null).Select(r=>r.Source.Input!.StoreName??r.Source.Input.Store).Distinct().ToArray(),HasSourceFile=Convert.ToInt64(source.ExecuteScalar())>0,Finished=rows.Count(r=>r.Status is not RowStatus.Pending and not RowStatus.Running and not RowStatus.RetryPending and not RowStatus.AwaitingVerification)},rows.ToArray());
    }
    public JobDetail Get(Guid id){lock(gate){using var db=Open();return Get(db,id);}}
    public void SplitPriceCutRow(Guid id,Guid rowId,PriceInput[] inputs)
    {
        lock(gate){using var db=Open();var job=Get(db,id);var row=job.Rows.Single(r=>r.Id==rowId);var source=row.Source.Input;
            var validId=(string? item)=>item!=null&&System.Text.RegularExpressions.Regex.IsMatch(item,@"^\d{1,30}$");
            var commonFieldsMatch=source!=null&&inputs.All(x=>x with{ItemId=source.ItemId,PromotionId=source.PromotionId,AllowExit=source.AllowExit}==source);
            var productSplit=source?.ItemId==null&&inputs.Select(x=>x.ItemId).Distinct().Count()==inputs.Length&&commonFieldsMatch&&inputs.All(x=>x.PromotionId==source!.PromotionId);
            var campaignIds=inputs.Select(x=>x.PromotionId).ToArray();
            var campaignSplit=source!=null&&source.PromotionId==null&&inputs.Select(x=>x.ItemId).Distinct().Count()==1&&(source.ItemId==null||inputs.All(x=>x.ItemId==source.ItemId))&&commonFieldsMatch&&campaignIds.All(p=>p!=null&&System.Text.RegularExpressions.Regex.IsMatch(p,@"^C-[A-Za-z0-9-]{1,80}$"))&&campaignIds.Distinct(StringComparer.Ordinal).Count()==inputs.Length;
            var splitInputsValid=source!=null&&inputs.All(x=>validId(x.ItemId)&&(!x.AllowExit||source.AllowExit||job.Summary.SubsidyExits));
            if(job.Summary.Mode is not ExecutionMode.PriceCut and not ExecutionMode.WeekendEnrollment||row.Status!=RowStatus.Running||row.PriceCut!=null||source==null||inputs.Length is <2 or >50||!splitInputsValid||!productSplit&&!campaignSplit)throw new AppException("拆分商品或活动记录不符合原任务范围，未提交价格。");
            using var tx=db.BeginTransaction();var ordinal=Convert.ToInt32(Command(db,"SELECT MAX(ordinal) FROM job_rows WHERE job_id=$0",id.ToString()).ExecuteScalar())+1;
            for(var i=0;i<inputs.Length;i++){var child=row with{Id=i==0?row.Id:Guid.NewGuid(),Source=row.Source with{Input=inputs[i] with{AllowExit=source.AllowExit}},Status=RowStatus.Pending,Attempts=0,Message=campaignSplit?$"精确商品 {inputs[i].ItemId} 的普通活动 {inputs[i].PromotionId}，独立执行与核验。":$"精确 SKU 对应商品 {inputs[i].ItemId}，独立执行与核验。",Checkpoint=campaignSplit?"cut-split-campaign":"cut-split-item",Observation=null};if(i==0)SaveRow(db,child);else Run(db,"INSERT INTO job_rows VALUES($0,$1,$2,$3)",child.Id.ToString(),id.ToString(),ordinal++,JsonSerializer.Serialize(child));}
            SaveSummary(db,job.Summary with{Total=job.Summary.Total+inputs.Length-1});Event(db,id,campaignSplit?$"{source.Sku} · 商品 {inputs[0].ItemId} 拆分为 {inputs.Length} 个明确普通活动：{string.Join("、",campaignIds)}；各活动独立保存与回读。":$"{source.Sku} · 拆分为 {inputs.Length} 个精确匹配商品：{string.Join("、",inputs.Select(x=>x.ItemId))}；各商品独立保存与核验。");tx.Commit();}
    }
    public JobSummary[] List(){lock(gate){using var db=Open();using var c=Command(db,"SELECT id FROM jobs ORDER BY rowid DESC");using var r=c.ExecuteReader();var ids=new List<Guid>();while(r.Read())ids.Add(Guid.Parse(r.GetString(0)));r.Close();return ids.Select(id=>Get(db,id).Summary).ToArray();}}
    public JobDetail Create(ValidationSnapshot validation,ExecutionMode mode,bool mixedPrices=false,bool basePrices=false,bool subsidyExits=false,bool incomePriority=false)
    {
        if(mode==ExecutionMode.WeekendEnrollment&&(mixedPrices||basePrices||subsidyExits||incomePriority||validation.Rows.Any(r=>r.Input is {} input&&(input.Action!=null||input.AllowExit||input.AllowBaseChange||input.BasePrice!=null))))throw new AppException("周末报名不能带调价动作、改原价或退出权限。",409);
        lock(gate){if(mode is not ExecutionMode.Simulation and not ExecutionMode.ReadOnly and not ExecutionMode.PriceCut and not ExecutionMode.WeekendEnrollment)throw new AppException("仅支持模拟、只读核对和实际调价。",409);if(subsidyExits&&(!mixedPrices||mode!=ExecutionMode.PriceCut))throw new AppException("退出补贴活动必须确认实际混合调价。",409);if(basePrices&&(!mixedPrices||mode!=ExecutionMode.PriceCut))throw new AppException("改原价必须同时确认实际混合调价。",409);if(!validation.Rows.Any(r=>r.Input!=null))throw new AppException("没有有效数据可执行。");using var db=Open();using var tx=db.BeginTransaction();using var existing=Command(db,"SELECT id FROM jobs WHERE validation_id=$0",validation.Id.ToString());if(existing.ExecuteScalar() is string old){var previous=Get(db,Guid.Parse(old));if(previous.Summary.Mode!=mode||previous.Summary.MixedPrices!=(mode==ExecutionMode.PriceCut&&mixedPrices)||previous.Summary.BasePrices!=basePrices||previous.Summary.SubsidyExits!=subsidyExits||previous.Summary.IncomePriority!=incomePriority)throw new AppException("此检查结果已创建其他模式或操作范围的任务，请重新检查数据。",409);return previous;}
            var id=Guid.NewGuid();var summary=new JobSummary(id,validation.FileName,mode,JobStatus.Ready,DateTimeOffset.UtcNow,validation.Rows.Length,0,MixedPrices:mode==ExecutionMode.PriceCut&&mixedPrices,BasePrices:basePrices,SubsidyExits:subsidyExits,IncomePriority:incomePriority,Weekend:mode==ExecutionMode.WeekendEnrollment?Execution.WeekendActivity.Current(DateTimeOffset.UtcNow):null);
            Run(db,"INSERT INTO jobs VALUES($0,$1,$2,$3)",id.ToString(),validation.Id.ToString(),summary.Status.ToString(),JsonSerializer.Serialize(summary));
            if(validation.SourceFile!=null)Run(db,"INSERT INTO job_sources(job_id,content) VALUES($0,$1)",id.ToString(),validation.SourceFile);
            for(int i=0;i<validation.Rows.Length;i++){var source=validation.Rows[i];var row=new JobRow(Guid.NewGuid(),source,source.Input==null?RowStatus.Invalid:RowStatus.Pending,"",string.Join("；",source.Errors),"created");Run(db,"INSERT INTO job_rows VALUES($0,$1,$2,$3)",row.Id.ToString(),id.ToString(),i,JsonSerializer.Serialize(row));}
            Event(db,id,mode switch{ExecutionMode.Simulation=>"已创建模拟任务；不会操作真实店铺。",ExecutionMode.WeekendEnrollment=>$"已创建周末活动报名任务：{summary.Weekend!.Start:yyyy-MM-dd} 至 {summary.Weekend.End:yyyy-MM-dd}，只按指定日期普通活动报名，不改原价或退出其他活动。",ExecutionMode.PriceCut=>basePrices?"已确认店铺和三种调价流程：降价、退出重报提价、退出活动后按表格目标修改原价。":"已创建原范围实际调价任务；未确认的第三种改原价不执行。",_=>"已创建只读核对任务；只读取店铺，不会修改价格或活动。"});tx.Commit();return Get(db,id);
        }
    }
    public bool RetryWeekendReviews(Guid id)
    {
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();var job=Get(db,id);
            if(job.Summary.Mode!=ExecutionMode.WeekendEnrollment||job.Summary.Status is not JobStatus.Completed and not JobStatus.Paused||ActiveId()!=null)return false;
            var rows=job.Rows.Where(r=>r.Status==RowStatus.NeedsReview).ToArray();if(rows.Length==0)return false;
            foreach(var row in rows)SaveRow(db,row with{Status=RowStatus.RetryPending,Message=row.Message+" 已按修复后补报请求重新排队；保留原尝试次数和提交检查点。"});
            SaveSummary(db,job.Summary with{Status=JobStatus.Paused});Event(db,id,$"修复后仅重新核对并补报 {rows.Length} 条待核对；已完成项跳过，提交结果未知先回读，不重复报名。");tx.Commit();return true;
        }
    }
    public bool RetryPriceCutReviews(Guid id)
    {
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();var job=Get(db,id);
            if(job.Summary.Mode!=ExecutionMode.PriceCut||job.Summary.Status is not JobStatus.Completed and not JobStatus.Paused||ActiveId()!=null)return false;
            var events=new List<string>();using(var eventCommand=Command(db,"SELECT message FROM job_events WHERE job_id=$0 ORDER BY id",id.ToString()))using(var reader=eventCommand.ExecuteReader())while(reader.Read())events.Add(reader.GetString(0));
            var rows=job.Rows.Where(r=>r.Status==RowStatus.NeedsReview&&r.Source.Input is {} input&&(
                r.Checkpoint=="guard-review"&&r.PriceCut==null&&r.Attempts<MaxAttempts&&input.PromotionId==null&&input.ItemId!=null&&System.Text.RegularExpressions.Regex.IsMatch(input.ItemId,@"^\d{1,30}$")&&r.Message=="拆分商品或活动记录不符合原任务范围，未提交价格。"||
                job.Summary.MixedPrices&&job.Summary.BasePrices&&(
                r.Checkpoint=="discount-base-review"&&r.Route=="降低原价后设置折扣价"&&r.Message.Contains("平台要求确认未识别的商品或费用变更",StringComparison.Ordinal)&&r.Message.Contains("运费",StringComparison.Ordinal)&&r.PriceCut is {ChangeBase:true,Stage:"discount-base",DiscountBase:decimal adjusted} plan&&plan.DiscountPromotionId?.StartsWith("C-",StringComparison.Ordinal)==true&&plan.Product.Sku==input.Sku&&plan.Input.Sku==input.Sku&&plan.Input.TargetPrice==input.TargetPrice&&adjusted==input.TargetPrice*4m&&System.Text.RegularExpressions.Regex.IsMatch(plan.Product.ItemId,@"^\d{1,30}$")&&(input.ItemId==null||plan.Product.ItemId==input.ItemId)||
                r.Checkpoint=="discount-base-review"&&r.Route=="降低原价后设置折扣价"&&r.Message.Contains("连接或记录失败",StringComparison.Ordinal)&&r.PriceCut is {ChangeBase:true,Stage:"discount-base",DiscountBase:decimal disconnectedBase} disconnected&&disconnected.DiscountPromotionId?.StartsWith("C-",StringComparison.Ordinal)==true&&disconnected.Product.Sku==input.Sku&&disconnected.Input.Sku==input.Sku&&disconnected.Input.TargetPrice==input.TargetPrice&&disconnectedBase==input.TargetPrice*4m&&System.Text.RegularExpressions.Regex.IsMatch(disconnected.Product.ItemId,@"^\d{1,30}$")&&(input.ItemId==null||disconnected.Product.ItemId==input.ItemId)&&HasVerifiedDiscountBaseThenActivityOpenDisconnect(events,r,input,disconnected)||
                r.Checkpoint=="cut-review"&&r.PriceCut==null&&r.Message.Contains("未提交价格",StringComparison.Ordinal)&&r.Message.Contains("等待分组子商品展开",StringComparison.Ordinal)&&r.Source.Input!.ItemId==null)||
                r.Checkpoint=="cut-review"&&r.Route=="自动调价"&&r.PriceCut==null&&r.Attempts<MaxAttempts&&r.Message.StartsWith("普通活动已按表格目标价预约，尚未激活",StringComparison.Ordinal)&&input.PromotionId==null&&input.TargetPrice>0&&(input.Action=="降价"||job.Summary.MixedPrices)&&(input.ItemId==null||System.Text.RegularExpressions.Regex.IsMatch(input.ItemId,@"^\d{1,30}$")))).ToArray();
            if(rows.Length==0)return false;
            foreach(var row in rows)SaveRow(db,row with{Status=RowStatus.RetryPending,Message=row.Message+" 已按原目标价安全重排；先回读商品和活动状态，再从原检查点续跑。"});
            SaveSummary(db,job.Summary with{Status=JobStatus.Paused});Event(db,id,$"上一批调价修复后，仅重新核对 {rows.Length} 条：单笔运费确认项、旧版活动目标冲突（新版本逐个确认活动 ID）及明确未提交的商品列表超时项；完成项、其他待核对项及原目标价均不变。");tx.Commit();return true;
        }
    }
    static bool HasVerifiedDiscountBaseThenActivityOpenDisconnect(List<string> events,JobRow row,PriceInput input,Execution.PriceCutPlan plan)
    {
        var observation=row.Observation;var expected=plan.DiscountBase;
        if(expected==null||observation==null||observation.ItemId!=plan.Product.ItemId||Execution.ZiniaoReadOnlyExecutor.Money(observation.BasePriceText)!=expected||observation.Promotions.Count(p=>p.Id==plan.DiscountPromotionId&&!p.Active&&p.Joinable)!=1)return false;
        var marker=$"· {input.Sku} ·";var attempt=$"开始实际调价，第 {row.Attempts} 次尝试";var starts=events.Select((message,index)=>(message,index)).Where(x=>x.message.Contains(marker,StringComparison.Ordinal)&&x.message.Contains(attempt,StringComparison.Ordinal)).Select(x=>x.index).ToArray();
        if(starts.Length==0)return false;var retryStart=starts[^1];var prior=events.Take(retryStart).Where(message=>message.StartsWith(input.Sku+" · ",StringComparison.Ordinal)).ToArray();
        if(!prior.Any(message=>message.Contains("阶段 discount-base 提交前记录已保存",StringComparison.Ordinal))||!prior.Any(message=>message.Contains("耗时 · 原价保存 ·",StringComparison.Ordinal))||!prior.Any(message=>message.Contains("耗时 · 最终价格核验 ·",StringComparison.Ordinal)))return false;
        var afterRetry=events.Skip(retryStart+1).Where(message=>message.StartsWith(input.Sku+" · ",StringComparison.Ordinal)).ToArray();var disconnect=Array.FindLastIndex(afterRetry,message=>message.Contains("页面连接中断 · 节点 活动 open ·",StringComparison.Ordinal)&&message.Contains("尚未确认出现验证页",StringComparison.Ordinal));
        if(disconnect<0)return false;var terminal=afterRetry.Skip(disconnect+1).ToArray();
        return terminal.Length==1&&terminal[0].Contains("已记录退出、原价或报名阶段",StringComparison.Ordinal)&&terminal[0].Contains("连接或记录失败",StringComparison.Ordinal)&&terminal[0].Contains("已达3次尝试上限",StringComparison.Ordinal);
    }
    public bool TryStart(Guid id)
    {
        lock(gate){using var db=Open();using var tx=db.BeginTransaction();var job=Get(db,id);if(job.Summary.Verification!=null||job.Summary.Status is not JobStatus.Ready and not JobStatus.Paused)return false;
            using var c=Command(db,"SELECT COUNT(*) FROM jobs WHERE status IN ('Running','Pausing','AwaitingVerification','Verifying')");if((long)c.ExecuteScalar()!>0)return false;
            if(job.Summary.Mode==ExecutionMode.PriceCut)
            {
                foreach(var row in job.Rows.Where(r=>r.Status==RowStatus.NeedsReview&&r.Attempts==0&&r.Checkpoint=="cut-review"&&(r.PriceCut!=null||r.Message.Contains("连接、页面或记录写入失败")||r.Message.Contains("超时"))))
                    SaveRow(db,row with{Status=RowStatus.RetryPending,Attempts=Math.Max(1,row.Attempts),Message=row.Message+" 已加入本轮结束后的重试队列。"});
                foreach(var row in job.Rows.Where(r=>r.Status==RowStatus.NeedsReview&&r.Attempts<MaxAttempts&&r.PriceCut is {ChangeBase:true,DiscountBase:null,Stage:"base-save"}&&r.Message.Contains("确认平台商品更新后果后弹框未关闭，保存结果需回读")))
                    SaveRow(db,row with{Status=RowStatus.RetryPending,Message=row.Message+" 已加入回读队列，先核对已保存价格。"});
            }
            if(job.Summary.Mode==ExecutionMode.WeekendEnrollment)
            {
                foreach(var row in job.Rows.Where(r=>r.Status==RowStatus.NeedsReview&&r.Attempts<JobStore.MaxAttempts&&r.PriceCut==null&&r.Checkpoint=="weekend-review"&&(r.Message.Contains("未找到日期为")||r.Message.Contains("未按父链接统一报名"))))
                    SaveRow(db,row with{Status=RowStatus.RetryPending,Message=row.Message+" 已加入本轮结束后的重读重试队列。"});
            }
            SaveSummary(db,job.Summary with{Status=JobStatus.Running});Event(db,id,job.Summary.Mode switch{ExecutionMode.Simulation=>"开始模拟执行。",ExecutionMode.WeekendEnrollment=>$"开始周末活动报名：{job.Summary.Weekend?.Label}。",ExecutionMode.PriceCut=>"开始实际调价；逐店复用一个工作标签。",_=>"开始只读核对；不提交价格。"});tx.Commit();return true;
        }
    }
    public bool RequestPause(Guid id){lock(gate){using var db=Open();var job=Get(db,id);if(job.Summary.Status==JobStatus.AwaitingVerification){SaveSummary(db,job.Summary with{Status=JobStatus.Paused});Event(db,id,"验证任务已暂存，等待人工继续。");return true;}if(job.Summary.Status is not JobStatus.Running and not JobStatus.Verifying)return false;using var tx=db.BeginTransaction();SaveSummary(db,job.Summary with{Status=JobStatus.Pausing});Event(db,id,"已请求暂停，当前行结束后生效。");tx.Commit();return true;}}
    public Guid? ActiveId(){lock(gate){using var db=Open();using var c=Command(db,"SELECT id FROM jobs WHERE status IN ('Running','Pausing','AwaitingVerification','Verifying') LIMIT 1");return c.ExecuteScalar() is string id?Guid.Parse(id):null;}}
    public JobRow? ClaimNext(Guid id)
    {
        lock(gate){using var db=Open();var job=Get(db,id);if(job.Summary.Status is not JobStatus.Running and not JobStatus.Pausing)return null;if(job.Rows.Any(r=>r.Status==RowStatus.Running))return null;
            var next=job.Rows.FirstOrDefault(r=>r.Status==RowStatus.Pending);
            using var tx=db.BeginTransaction();
            var retries=job.Rows.Where(r=>r.Status==RowStatus.RetryPending).ToArray();
            if(job.Summary.Status==JobStatus.Pausing){var status=next==null&&retries.Length==0?JobStatus.Completed:JobStatus.Paused;SaveSummary(db,job.Summary with{Status=status});Event(db,id,status==JobStatus.Completed?"批次已结束，请查看逐行结果。":"任务已暂停。");tx.Commit();return null;}
            if(next==null&&retries.Length>0)
            {
                var round=job.Summary.Round+1;foreach(var row in retries)SaveRow(db,row with{Status=RowStatus.Pending});
                SaveSummary(db,job.Summary with{Round=round});Event(db,id,$"第 {round} 轮开始，仅重试 {retries.Length} 项未成功记录；已完成项不重复执行。");next=retries[0];
            }
            if(next==null){SaveSummary(db,job.Summary with{Status=JobStatus.Completed});Event(db,id,"批次已结束，请查看逐行结果。");tx.Commit();return null;}
            var attempt=next.Checkpoint=="verification-resume"&&next.PriceCutAttempt<next.Attempts?next.Attempts:next.Attempts+1;var label=job.Summary.Mode switch{ExecutionMode.Simulation=>"模拟",ExecutionMode.PriceCut=>"实际调价",ExecutionMode.WeekendEnrollment=>"周末活动报名",_=>"只读核对"};var running=next with{Status=RowStatus.Running,Attempts=attempt,Checkpoint=job.Summary.Mode==ExecutionMode.PriceCut?"cut-started":"read-or-simulation-started",Message=$"正在{label}，第 {attempt} 次尝试"};SaveRow(db,running);Event(db,id,$"第 {next.Source.SourceRow} 行 · {next.Source.Input!.Sku} · 开始{label}，第 {running.Attempts} 次尝试");tx.Commit();return running;
        }
    }
    public void UpdateRow(Guid jobId,Guid rowId,ExecutionResult result)
    {
        lock(gate){using var db=Open();var job=Get(db,jobId);var row=job.Rows.First(r=>r.Id==rowId);var retry=job.Summary.Mode is ExecutionMode.PriceCut or ExecutionMode.WeekendEnrollment&&result.Retryable;var message=result.Message+(retry?(row.Attempts<MaxAttempts?" 本轮暂时跳过，完成本轮后重试。":" 已达3次尝试上限，请核对原因后另行处理。") : "");using var tx=db.BeginTransaction();SaveRow(db,row with{Status=retry&&row.Attempts<MaxAttempts?RowStatus.RetryPending:result.Status,Route=result.Route,Message=message,Checkpoint=result.Checkpoint,Observation=result.Observation});if(result.PauseBatch)SaveSummary(db,job.Summary with{Status=JobStatus.Paused});Event(db,jobId,$"{row.Source.Input?.Sku} · {message}");tx.Commit();}
    }
    public void AddEvent(Guid id,string message){lock(gate){using var db=Open();Event(db,id,message);}}
    public void RecordVerification(Guid id,Guid rowId,VerificationUpdate update)
    {
        lock(gate){using var db=Open();var job=Get(db,id);var row=job.Rows.Single(r=>r.Id==rowId);using var tx=db.BeginTransaction();
            if(update.Phase=="connection-failed"&&job.Summary.Status is JobStatus.Running or JobStatus.Verifying)SaveSummary(db,job.Summary with{Status=JobStatus.Pausing,Verification=null});
            if(update.Phase=="entered"&&job.Summary.Status==JobStatus.Running){var input=row.Source.Input!;SaveSummary(db,job.Summary with{Status=JobStatus.Verifying,Verification=new(Guid.NewGuid(),id,rowId,input.Store,input.StoreName??input.Store,input.Sku,update.Message,DateTimeOffset.UtcNow,Automatic:true)});}
            if(update.Phase=="passed"&&job.Summary.Verification?.Automatic==true)SaveSummary(db,job.Summary with{Status=job.Summary.Status==JobStatus.Verifying?JobStatus.Running:job.Summary.Status,Verification=null});
            var label=update.Phase=="connection-failed"?"页面连接中断":"验证"+(update.Phase switch{"entered"=>"进入","confirmed"=>"自动确认","passed"=>"通过","failed"=>"等待中断",_=>"检查"});
            Event(db,id,$"{row.Source.Input?.Sku} · {label} · 节点 {update.Operation} · {update.Message}");tx.Commit();}
    }
    static void SaveWeekendPlan(SqliteConnection db,JobDetail job,JobRow row,Execution.PriceCutPlan plan)
    {
        var campaign=plan.Product.Promotions.Where(p=>p.Id==plan.PromotionId).ToArray();
        var recovery=row.PriceCut!=null&&row.PriceCutAttempt<row.Attempts&&JsonSerializer.Serialize(row.PriceCut)==JsonSerializer.Serialize(plan);
        if(row.Status!=RowStatus.Running||row.PriceCut!=null&&!recovery||plan.Input!=row.Source.Input||plan.Store.Id!=plan.Input.Store&&plan.Store.Name!=plan.Input.Store||plan.Stage!="weekend-enroll"||!plan.Join||plan.Rejoin||plan.ChangeBase||plan.DiscountBase!=null||plan.IncomeSubsidyId!=null||!SharedWeekendTargetsAreUnified(job,plan.Input,plan.Product)||plan.Product.Sku!=plan.Input.Sku||plan.Input.ItemId!=null&&plan.Product.ItemId!=plan.Input.ItemId||campaign.Length!=1||job.Summary.Weekend==null||!Execution.WeekendActivity.Matches(campaign[0],job.Summary.Weekend)||!campaign[0].Joinable||campaign[0].Active||Execution.ZiniaoReadOnlyExecutor.Money(plan.Product.BasePriceText)!=plan.BasePrice)
            throw new AppException("周末报名检查点超出本批商品、日期或操作范围，禁止提交。",409);
        var sharedNote=plan.Product.SharedSkus is {Length:>1} siblings?$"旧链接 {plan.Product.ItemId} 的 {siblings.Length} 个关联 SKU 目标价一致，按父链接统一报名。":"";
        using var tx=db.BeginTransaction();SaveRow(db,row with{PriceCut=plan,PriceCutAttempt=row.Attempts,Checkpoint="weekend-before-submit",Message="已记录周末活动编号、日期及目标价，准备单次报名。"});Event(db,job.Summary.Id,$"{plan.Input.Sku} · 周末 {job.Summary.Weekend.Label} · 商品 {plan.Product.ItemId} · 活动 {plan.PromotionId} · 目标 {plan.Input.TargetPrice:0.00} · {sharedNote}提交前记录已保存");tx.Commit();
    }
    static bool SharedWeekendTargetsAreUnified(JobDetail job,PriceInput input,Execution.ZiniaoProduct product)
    {
        if(product.SharedSkus is not {Length:>1})return true;
        var siblings=product.SharedSkus.Where(s=>!string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToArray();
        if(siblings.Length<2||!siblings.Contains(input.Sku,StringComparer.Ordinal))return false;
        var rows=job.Rows.Where(r=>r.Source.Input is {} candidate&&candidate.Store==input.Store&&candidate.Site==input.Site&&candidate.Currency==input.Currency&&siblings.Contains(candidate.Sku,StringComparer.Ordinal)&&(candidate.ItemId==null||candidate.ItemId==product.ItemId)).Select(r=>r.Source.Input!).ToArray();
        return siblings.All(s=>rows.Any(r=>r.Sku==s))&&rows.Select(r=>r.TargetPrice).Distinct().Count()==1;
    }
    public VerificationNotice? CurrentVerification(){lock(gate){using var db=Open();using var c=Command(db,"SELECT payload FROM jobs WHERE status IN ('AwaitingVerification','Verifying') LIMIT 1");return c.ExecuteScalar() is string s?Read<JobSummary>(s).Verification:null;}}
    public void WaitForVerification(Guid id,Guid rowId,string message)
    {
        lock(gate){using var db=Open();var job=Get(db,id);var row=job.Rows.Single(r=>r.Id==rowId);
            if(job.Summary.Status is not JobStatus.Running and not JobStatus.Pausing and not JobStatus.Verifying||row.Status!=RowStatus.Running)throw new AppException("任务状态已变化，不能进入验证等待。",409);
            var input=row.Source.Input!;var notice=new VerificationNotice(Guid.NewGuid(),id,rowId,input.Store,input.StoreName??input.Store,input.Sku,message,DateTimeOffset.UtcNow);
            using var tx=db.BeginTransaction();SaveRow(db,row with{Status=RowStatus.AwaitingVerification,Checkpoint="verification-wait",Message=message});SaveSummary(db,job.Summary with{Status=JobStatus.AwaitingVerification,Verification=notice});Event(db,id,$"{input.Sku} · 需要人工验证，已暂停整批，点击继续前不再操作店铺。");tx.Commit();}
    }
    public bool ContinueAfterVerification(Guid id,Guid requestId)
    {
        lock(gate){using var db=Open();var job=Get(db,id);var notice=job.Summary.Verification;
            if(notice==null||notice.Automatic||notice.RequestId!=requestId||job.Summary.Status is not JobStatus.AwaitingVerification and not JobStatus.Paused)return false;
            using var active=Command(db,"SELECT COUNT(*) FROM jobs WHERE status IN ('Running','Pausing','AwaitingVerification','Verifying') AND id<>$0",id.ToString());if((long)active.ExecuteScalar()!>0)return false;
            var row=job.Rows.Single(r=>r.Id==notice.RowId);if(row.Status!=RowStatus.AwaitingVerification)return false;
            using var tx=db.BeginTransaction();SaveRow(db,row with{Status=RowStatus.Pending,Checkpoint="verification-resume",Message="已请求继续，先核验验证状态和原提交结果。"});SaveSummary(db,job.Summary with{Status=JobStatus.Running,Verification=null});Event(db,id,$"{notice.Sku} · 人工点击继续；先重新读取验证状态及商品，不盲目重复提交。");tx.Commit();return true;}
    }
    public void SavePriceCutPlan(Guid id,Guid rowId,Execution.PriceCutPlan plan)
    {
        lock(gate)
        {
            using var db=Open();var job=Get(db,id);var row=job.Rows.Single(r=>r.Id==rowId);var old=row.PriceCut;
            if(job.Summary.Mode==ExecutionMode.WeekendEnrollment){SaveWeekendPlan(db,job,row,plan);return;}
            var transitioning=old!=null&&Execution.ZiniaoIncreaseExecutor.FollowScheduledDiscount(old,plan.Product) is {} successor&&JsonSerializer.Serialize(successor)==JsonSerializer.Serialize(plan);
            var discountAdvancing=old?.DiscountBase!=null&&plan.DiscountBase!=null&&(old.Stage=="discount-exit"&&plan.Stage=="discount-base"||old.Stage=="discount-base"&&plan.Stage=="discount-join");
            var income=Execution.ZiniaoIncomeExecutor.IsIncome(plan);
            var incomeAdvancing=old!=null&&(old.Stage=="income-join"&&plan.Stage=="income-exit"||old.Stage=="income-ordinary-exit"&&plan.Stage=="income-join")&&old.IncomeSubsidyId==plan.IncomeSubsidyId&&old.IncomeOrdinaryId==plan.IncomeOrdinaryId&&old.Store.Id==plan.Store.Id&&old.Product.ItemId==plan.Product.ItemId&&old.BasePrice==plan.BasePrice&&old.Input==plan.Input;
            var advancing=incomeAdvancing||transitioning||discountAdvancing||old?.Rejoin==true&&old.Stage=="exit"&&plan.Rejoin&&plan.Stage=="join"||old?.ChangeBase==true&&old.Stage=="base-exit"&&plan.ChangeBase&&plan.Stage=="base-save";
            var changed=old!=null&&!incomeAdvancing&&!transitioning&&(JsonSerializer.Serialize(old.PreviousExit)!=JsonSerializer.Serialize(plan.PreviousExit)||old.Store.Id!=plan.Store.Id||old.Product.ItemId!=plan.Product.ItemId||old.PromotionId!=plan.PromotionId||old.Input.Sku!=plan.Input.Sku||old.Input.TargetPrice!=plan.Input.TargetPrice||old.Input.AllowExit!=plan.Input.AllowExit||old.Before!=plan.Before||old.BasePrice!=plan.BasePrice||old.Join!=plan.Join||old.Rejoin!=plan.Rejoin||old.ChangeBase!=plan.ChangeBase||old.DirectBaseOnly!=plan.DirectBaseOnly||old.Input.Note!=plan.Input.Note||old.DiscountBase!=plan.DiscountBase||old.RejoinPromotionId!=plan.RejoinPromotionId||old.DiscountPromotionId!=null&&old.DiscountPromotionId!=plan.DiscountPromotionId||old.DiscountBase!=null&&old.Stage=="discount-join"&&old.OfferPrice!=plan.OfferPrice||old.Rejoin&&old.Stage=="join"&&(plan.Stage!="join"||old.OfferPrice!=plan.OfferPrice)||old.ChangeBase&&old.Stage=="base-save"&&plan.Stage!="base-save");
            var outsideRow=row.Source.Input==null||plan.Input.Store!=row.Source.Input.Store||plan.Input.Sku!=row.Source.Input.Sku||plan.Input.TargetPrice!=row.Source.Input.TargetPrice||plan.Input.Site!=row.Source.Input.Site||plan.Input.Currency!=row.Source.Input.Currency||plan.Input.AllowExit&&!job.Summary.SubsidyExits&&row.Source.Input.AllowExit!=true||row.Source.Input.ItemId!=null&&plan.Product.ItemId.Replace("MLB","",StringComparison.OrdinalIgnoreCase).TrimStart('#','-')!=row.Source.Input.ItemId.Replace("MLB","",StringComparison.OrdinalIgnoreCase).TrimStart('#','-');
            var invalidIncome=income&&(!job.Summary.IncomePriority||!job.Summary.MixedPrices||!job.Summary.SubsidyExits||!Execution.ZiniaoIncomeExecutor.Valid(plan)||old!=null&&!incomeAdvancing)||!income&&(plan.Stage.StartsWith("income-",StringComparison.Ordinal)||plan.IncomeOrdinaryId!=null||plan.IncomeOrdinaryPrice!=null||plan.IncomeSubsidyNet!=null||plan.IncomeOrdinaryNet!=null);
            var invalidIncrease=plan.Rejoin&&((plan.RejoinPromotionId!=null&&(!plan.PromotionId.StartsWith("P-",StringComparison.Ordinal)&&plan.PromotionId!="D-PRICE-DISCOUNT"||!plan.RejoinPromotionId.StartsWith("C-",StringComparison.Ordinal)||!plan.Product.Promotions.Any(p=>p.Id==plan.RejoinPromotionId&&Execution.ZiniaoReadOnlyExecutor.OrdinaryReplacement(p,plan.Input.TargetPrice,plan.Input.AllowExit&&plan.PromotionId.StartsWith("P-",StringComparison.Ordinal)))))||!job.Summary.MixedPrices||plan.Join||plan.Stage is not "exit" and not "join"||old==null&&plan.Stage!="exit"||old?.Rejoin==true&&plan.Stage=="exit"&&!transitioning||plan.Stage=="join"&&plan.OfferPrice==null);
            var invalidDiscount=plan.DiscountBase!=null&&(!job.Summary.BasePrices||!job.Summary.MixedPrices||!Execution.ZiniaoDiscountBaseExecutor.Valid(plan)||plan.Product.Sku!=plan.Input.Sku||Execution.ZiniaoReadOnlyExecutor.Money(plan.Product.BasePriceText)!=plan.BasePrice||old==null&&plan.Stage=="discount-join"||old==null&&plan.Stage=="discount-base"&&plan.Product.Promotions.Any(p=>p.Active)||old!=null&&old.Stage!=plan.Stage&&!discountAdvancing||plan.Stage!="discount-exit"&&plan.DiscountPromotionId?.StartsWith("C-",StringComparison.Ordinal)!=true||plan.Stage=="discount-exit"&&plan.DiscountPromotionId!=null);
            var directBaseAuthorized=plan.DirectBaseOnly&&plan.Input.Note==row.Source.Input?.Note&&row.Source.Input?.Note?.Trim().StartsWith("仅改原价",StringComparison.Ordinal)==true;
            var invalidBase=plan.ChangeBase&&plan.DiscountBase==null&&(!job.Summary.BasePrices||!job.Summary.MixedPrices||plan.Join||plan.Rejoin||plan.Stage is not "base-exit" and not "base-save"||plan.DirectBaseOnly&&!directBaseAuthorized||!directBaseAuthorized&&plan.Input.TargetPrice>=plan.BasePrice*.2m&&plan.Input.TargetPrice<=plan.BasePrice*.95m||plan.Product.Sku!=plan.Input.Sku||Execution.ZiniaoReadOnlyExecutor.Money(plan.Product.BasePriceText)!=plan.BasePrice||plan.Input.TargetPrice<=0||decimal.Round(plan.Input.TargetPrice,2)!=plan.Input.TargetPrice||old==null&&plan.Stage=="base-save"&&plan.Product.Promotions.Any(p=>p.Active)||old?.ChangeBase==true&&plan.Stage=="base-exit"||plan.Stage=="base-exit"&&string.IsNullOrWhiteSpace(plan.PromotionId));
            if(job.Summary.Mode!=ExecutionMode.PriceCut||row.Status!=RowStatus.Running||row.PriceCutAttempt==row.Attempts&&!advancing||changed||invalidIncrease||invalidBase||invalidDiscount||invalidIncome||outsideRow)throw new AppException("调价提交记录状态或原商品价格不符，禁止重复保存。",409);
            using var tx=db.BeginTransaction();SaveRow(db,row with{PriceCut=plan,PriceCutAttempt=row.Attempts,Checkpoint="cut-before-submit",Message=plan.Rejoin||plan.ChangeBase?$"已记录调价阶段 {plan.Stage}，准备单次提交。":"已记录商品、活动、原价与改前价格，准备单次提交。"});
            Event(db,id,$"{plan.Input.Sku} · 第 {row.Attempts} 次 · 商品 {plan.Product.ItemId} · 活动 {plan.PromotionId} · {plan.Before:0.00} → {plan.Input.TargetPrice:0.00} · 阶段 {plan.Stage} 提交前记录已保存");tx.Commit();
        }
    }
    public JobEvent[] Events(Guid id,long after){lock(gate){using var db=Open();using var c=Command(db,"SELECT id,at,message FROM job_events WHERE job_id=$0 AND id>$1 ORDER BY id LIMIT 500",id.ToString(),after);using var r=c.ExecuteReader();var events=new List<JobEvent>();while(r.Read())events.Add(new(r.GetInt64(0),id,DateTimeOffset.Parse(r.GetString(1)),r.GetString(2)));return events.ToArray();}}
    public byte[] SourceFile(Guid id){lock(gate){using var db=Open();Get(db,id);using var c=Command(db,"SELECT content FROM job_sources WHERE job_id=$0",id.ToString());return c.ExecuteScalar() as byte[]??throw new AppException("此历史任务未保存上传原表。",404);}}
    public byte[] ExportLog(Guid id){lock(gate){using var db=Open();var job=Get(db,id);using var c=Command(db,"SELECT at,message FROM job_events WHERE job_id=$0 ORDER BY id",id.ToString());using var r=c.ExecuteReader();var log=new System.Text.StringBuilder();log.AppendLine($"任务：{id}\n文件：{job.Summary.FileName}\n店铺：{string.Join("、",job.Summary.StoreNames??[])}\n日志时间：UTC（ISO 8601）");while(r.Read())log.AppendLine($"{r.GetString(0)}\t{r.GetString(1)}");return new System.Text.UTF8Encoding(true).GetPreamble().Concat(System.Text.Encoding.UTF8.GetBytes(log.ToString())).ToArray();}}
    public void RecoverInterrupted()
    {
        lock(gate){using var db=Open();foreach(var summary in List().Where(j=>j.Status is JobStatus.Running or JobStatus.Pausing or JobStatus.Verifying)){var job=Get(db,summary.Id);using var tx=db.BeginTransaction();foreach(var row in job.Rows.Where(r=>r.Status==RowStatus.Running))SaveRow(db,row with{Status=RowStatus.NeedsReview,Message="服务中断，结果未知；请重新核对，不会自动重试。",Checkpoint="interrupted"});SaveSummary(db,job.Summary with{Status=JobStatus.Interrupted});Event(db,summary.Id,"服务已重启，原任务标记为中断待核对；重新上传可创建新批次。");tx.Commit();}}
    }
}
