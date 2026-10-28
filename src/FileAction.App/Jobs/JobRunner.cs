using FileAction.App.Actions;
using FileAction.App.Shell;
using FileAction.App.UI;
using FileAction.Core;

namespace FileAction.App.Jobs;

/// <summary>Peel's own job lifecycle shared by drag, search, presets and history.</summary>
internal sealed class JobRunner
{
    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly Dictionary<string,CancellationTokenSource> _active=[];
    public event Action? Changed;
    public IReadOnlyList<JobRecord> Records => _settings.History;
    public int ActiveCount => _active.Count;
    public JobRunner(AppSettings settings,ToolLocator tools)
    {
        _settings=settings;_tools=tools;
        foreach(var job in settings.History.Where(job=>job.State is "Queued" or "Working"))
        { job.State="Interrupted";job.Detail="Peel closed before this job finished. You can retry it."; }
    }
    public void Start(Operation op,IReadOnlyList<FileItem> items)=>Start(op,items,null);
    public async void Start(Operation op,IReadOnlyList<FileItem> items,IReadOnlyDictionary<string,string>? parameters)
    { try { await RunAsync(op,items,parameters); }catch(Exception ex){Notifications.Show("Peel",ex.Message,true);} }
    public void Cancel(string id){if(_active.TryGetValue(id,out var token))token.Cancel();}
    public void ForgetCompleted(){_settings.History.RemoveAll(job=>!_active.ContainsKey(job.Id));_settings.Save();Changed?.Invoke();}
    private async Task RunAsync(Operation op,IReadOnlyList<FileItem> items,IReadOnlyDictionary<string,string>? parameters)
    {
        if(items.Count==0)return;
        if(!op.AppliesTo(items)||!op.IsAvailable(_tools,items))throw new ConversionException("This action is unavailable for these files.");
        if(op is AppAction action){await action.RunAsync(items);return;}
        if(parameters is null&&op.Parameters.Count>0)
        {
            if(ImageToolDialog.Supports(op,items)){var dialog=new ImageToolDialog(op,items);if(dialog.ShowDialog()!=true)return;parameters=dialog.Values;}
            else{var dialog=new ParameterDialog(op,items);if(dialog.ShowDialog()!=true)return;parameters=dialog.Values;}
        }
        parameters??=new Dictionary<string,string>();
        _settings.LastOperationId=op.Id;_settings.LastParameters=new(parameters);
        var record=new JobRecord{OperationId=op.Id,Label=op.Wheel==WheelKind.Convert?"Convert to "+op.Label:op.Label,Sources=items.Select(item=>item.Path).ToArray(),Parameters=new(parameters)};
        using var cancellation=new CancellationTokenSource();_active.Add(record.Id,cancellation);_settings.History.Insert(0,record);
        _settings.History=_settings.History.Where(job=>_active.ContainsKey(job.Id)).Concat(_settings.History.Where(job=>!_active.ContainsKey(job.Id)).Take(30)).ToList();
        _settings.Save();Changed?.Invoke();
        // One card follows the job from progress to its result.
        var toast=Toasts.Job(record.Label,items.Count==1?items[0].Name:Loc.Items(items.Count),items[0].Path,()=>Cancel(record.Id));
        var lastNotification=0L;
        var progress=new Progress<double>(value=>{if(!_active.ContainsKey(record.Id))return;toast.Report(value);record.State="Working";record.Progress=value;if(Environment.TickCount64-lastNotification<200)return;lastNotification=Environment.TickCount64;Changed?.Invoke();});
        var context=new OperationContext{Tools=_tools,Progress=progress,CancellationToken=cancellation.Token,OutputDirectory=string.IsNullOrWhiteSpace(_settings.OutputDirectory)?null:_settings.OutputDirectory,Parameters=parameters,Settings=_settings.Quality};
        try
        {
            record.State="Working";Changed?.Invoke();
            var result=op is Ocr.OcrOperation?await Task.Run(()=>op.ExecuteAsync(items,context),cancellation.Token):await WorkerHost.ExecuteAsync(op,items,context);
            record.Outputs=result.Outputs.ToArray();record.State=result.Message is null?"Done":"Review";record.Detail=result.Message??result.Note;record.Progress=1;
            toast.Complete(record.Outputs,result.Message,result.Note);
            if(_settings.RevealResult&&record.Outputs.Length>0)JobCard.Reveal(record.Outputs[0]);
        }
        catch(OperationCanceledException){record.State="Cancelled";record.Detail="Cancelled. Original files were preserved.";toast.Cancelled();}
        catch(Exception ex){record.State="Failed";record.Detail=ex.Message;toast.Fail(ex.Message);}
        finally{_active.Remove(record.Id);if(!_settings.KeepHistory)_settings.History.Remove(record);_settings.Save();Changed?.Invoke();}
    }
}
