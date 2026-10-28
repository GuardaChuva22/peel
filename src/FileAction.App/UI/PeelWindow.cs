using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using FileAction.App.Jobs;
using FileAction.App.Shell;
using FileAction.App.Wheel;
using FileAction.Core;
using FileAction.Core.Operations;

namespace FileAction.App.UI;

/// <summary>Peel's searchable native workspace, written for this fork.</summary>
internal sealed class PeelWindow:Window
{
    private static PeelWindow? _open;
    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly JobRunner _jobs;
    private readonly Func<IReadOnlyList<Operation>> _catalog;
    private readonly List<FileItem> _files=[];
    private readonly StackPanel _fileRows=new(),_results=new();
    private readonly TextBox _search;
    private readonly Button _clearSelection;
    private readonly TextBlock _heading=PeelUi.Label("Ready when your files are.",20,weight:FontWeights.SemiBold);
    private readonly TextBlock _summary=PeelUi.Label("Drop a file, choose an action, keep moving.",12,PeelUi.Muted);
    private readonly TextBlock _status=PeelUi.Label("",12,PeelUi.Muted);
    private string _section="Convert";
    private readonly Dictionary<string,Button> _tabs=[];
    public static void ShowSingle(AppSettings settings,ToolLocator tools,JobRunner jobs,Func<IReadOnlyList<Operation>> catalog,Action preferences,IReadOnlyList<string>? paths=null)
    {
        _open??=new PeelWindow(settings,tools,jobs,catalog,preferences);
        if(paths is {Count:>0})_open.SetFiles(paths);
        if(!_open.IsVisible)_open.Show();if(_open.WindowState==WindowState.Minimized)_open.WindowState=WindowState.Normal;
        ForegroundHelper.Force(_open);
    }
    internal PeelWindow(AppSettings settings,ToolLocator tools,JobRunner jobs,Func<IReadOnlyList<Operation>> catalog,Action preferences)
    {
        _settings=settings;_tools=tools;_jobs=jobs;_catalog=catalog;
        PeelUi.Configure(this,"Peel",940,660);MinWidth=820;MinHeight=560;AllowDrop=true;
        var root=new Grid{Background=PeelUi.Card};root.RowDefinitions.Add(new(){Height=GridLength.Auto});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=GridLength.Auto});var top=new Border{Background=PeelUi.Card,BorderBrush=PeelUi.Edge,BorderThickness=new Thickness(0,0,0,1),Child=PeelUi.TitleBar(this,"Peel",preferences)};root.Children.Add(top);
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(216)});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Grid.SetRow(body,1);root.Children.Add(body);
        var rail=new Border{Background=PeelUi.Ink,BorderBrush=PeelUi.Brush("#E9EEEB"),BorderThickness=new Thickness(0,0,1,0),Padding=new Thickness(16,22,16,20)};
        var sidebar=new DockPanel();rail.Child=sidebar;body.Children.Add(rail);
        var guide=new StackPanel{Margin=new Thickness(0,18,0,0)};guide.Children.Add(PeelUi.Label("Drag, then tap",11,PeelUi.Muted,FontWeights.SemiBold));
        foreach(var wheel in Enum.GetValues<WheelKind>()){var row=new DockPanel{Margin=new Thickness(0,9,0,0)};var key=PeelUi.Label(AppSettings.KeyName(settings.KeyFor(wheel)),12,PeelUi.AccentInk,FontWeights.SemiBold);key.Width=44;row.Children.Add(key);row.Children.Add(PeelUi.Label(AppSettings.WheelName(wheel),12,PeelUi.Muted));guide.Children.Add(row);}
        DockPanel.SetDock(guide,Dock.Bottom);sidebar.Children.Add(guide);
        var sideTop=new StackPanel();DockPanel.SetDock(sideTop,Dock.Top);sidebar.Children.Add(sideTop);
        var fileHeader=new DockPanel{Margin=new Thickness(0,0,0,12)};
        _clearSelection=PeelUi.Button("Clear",()=>{_files.Clear();RefreshFiles();},compact:true);_clearSelection.Padding=new Thickness(6,3,6,3);_clearSelection.MinHeight=24;_clearSelection.BorderThickness=new Thickness(0);_clearSelection.Background=Brushes.Transparent;_clearSelection.Foreground=PeelUi.Muted;DockPanel.SetDock(_clearSelection,Dock.Right);fileHeader.Children.Add(_clearSelection);fileHeader.Children.Add(PeelUi.Label("Files",13,weight:FontWeights.SemiBold));sideTop.Children.Add(fileHeader);
        var addRow=new Grid{Margin=new Thickness(0,0,0,18)};addRow.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});addRow.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var browse=PeelUi.Button("+  Add files",Browse,compact:true);browse.Background=PeelUi.Card;browse.Margin=new Thickness(0,0,6,0);addRow.Children.Add(browse);
        var folder=PeelUi.Button("Folder…",()=>{var d=new Microsoft.Win32.OpenFolderDialog();if(d.ShowDialog(this)==true)AddFiles([d.FolderName]);},compact:true);folder.Background=PeelUi.Card;folder.Padding=new Thickness(8,6,8,6);Grid.SetColumn(folder,1);addRow.Children.Add(folder);sideTop.Children.Add(addRow);
        sidebar.Children.Add(new ScrollViewer{Content=_fileRows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});
        var main=new Grid{Margin=new Thickness(28,24,28,16)};for(int i=0;i<3;i++)main.RowDefinitions.Add(new(){Height=GridLength.Auto});main.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Grid.SetColumn(main,1);body.Children.Add(main);
        var intro=new StackPanel();intro.Children.Add(_heading);_summary.Margin=new Thickness(0,6,0,18);intro.Children.Add(_summary);main.Children.Add(intro);
        var navigation=new StackPanel{Orientation=Orientation.Horizontal};var navigationHost=new Border{Background=PeelUi.Brush("#F2F5F3"),CornerRadius=new CornerRadius(10),Padding=new Thickness(3),Margin=new Thickness(0,0,0,18),HorizontalAlignment=HorizontalAlignment.Left,Child=navigation};Grid.SetRow(navigationHost,1);main.Children.Add(navigationHost);
        foreach(var name in new[]{"Convert","Tools","Actions","Presets","Recent"}){var captured=name;var tab=PeelUi.Button(name,()=>{_section=captured;Refresh();},compact:true);tab.Margin=new Thickness(0,0,3,0);tab.Padding=new Thickness(13,6,13,6);_tabs[name]=tab;navigation.Children.Add(tab);}
        _search=PeelUi.Input("Search actions");_search.ToolTip="Search actions · Ctrl + K";_search.Height=40;_search.Padding=new Thickness(38,10,64,10);
        var searchHost=new Grid{Margin=new Thickness(0,0,0,14)};searchHost.Children.Add(_search);
        var searchHint=PeelUi.Label("Search formats, tools, presets…",12,PeelUi.Muted);searchHint.Margin=new Thickness(39,0,64,0);searchHint.VerticalAlignment=VerticalAlignment.Center;searchHint.IsHitTestVisible=false;searchHost.Children.Add(searchHint);
        var searchIcon=PeelUi.Icon("\uE721",15,PeelUi.Muted);searchIcon.Margin=new Thickness(14,0,0,0);searchIcon.HorizontalAlignment=HorizontalAlignment.Left;searchIcon.IsHitTestVisible=false;searchHost.Children.Add(searchIcon);
        var keyHint=new Border{Background=PeelUi.Brush("#F5F7F6"),BorderBrush=PeelUi.Brush("#E9EEEB"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(5),Padding=new Thickness(5,2,5,2),Margin=new Thickness(0,0,12,0),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,Child=PeelUi.Label("Ctrl K",10,PeelUi.Muted),IsHitTestVisible=false};searchHost.Children.Add(keyHint);
        _search.TextChanged+=(_,_)=>{searchHint.Visibility=_search.Text.Length==0?Visibility.Visible:Visibility.Collapsed;RefreshResults();};Grid.SetRow(searchHost,2);main.Children.Add(searchHost);
        var scroll=new ScrollViewer{Content=_results,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetRow(scroll,3);main.Children.Add(scroll);
        var footer=new DockPanel{Margin=new Thickness(20,8,20,10)};footer.Children.Add(_status);var shortcut=PeelUi.Label(AppSettings.HotkeyName(settings.Hotkey)+"  ·  anywhere",11,PeelUi.Muted);shortcut.HorizontalAlignment=HorizontalAlignment.Right;footer.Children.Add(shortcut);Grid.SetRow(footer,2);root.Children.Add(footer);Content=root;
        DragOver+=(_,e)=>{e.Handled=true;e.Effects=e.Data.GetDataPresent(DataFormats.FileDrop)||e.Data.GetDataPresent(DataFormats.Bitmap)?DragDropEffects.Copy:DragDropEffects.None;};
        Drop+=async(_,e)=>{e.Handled=true;try{var content=DropContent.Read(e.Data,true);if(content is null)return;if(content.Prepare is not null)await content.Prepare();AddFiles(content.Items.Select(item=>item.Path));}catch(Exception ex){_summary.Text=ex.Message;}};
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape){Close();e.Handled=true;}
            else if(e.Key==Key.K&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){_search.Focus();_search.SelectAll();e.Handled=true;}
            else if(e.Key==Key.O&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){Browse();e.Handled=true;}
            else if(e.Key==Key.Enter&&Keyboard.FocusedElement==_search&&_section is "Convert" or "Tools" or "Actions"){var first=Available().FirstOrDefault();if(first is not null)Run(first);e.Handled=true;}
        };
        _jobs.Changed+=OnJobsChanged;Closed+=(_,_)=>{_jobs.Changed-=OnJobsChanged;if(ReferenceEquals(_open,this))_open=null;};RefreshFiles();
    }
    private void OnJobsChanged(){if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(OnJobsChanged);return;}Refresh();}
    internal void SetFiles(IEnumerable<string> paths){_files.Clear();AddFiles(paths);}
    private void AddFiles(IEnumerable<string> paths){foreach(var path in paths.Where(p=>File.Exists(p)||Directory.Exists(p)))if(!_files.Any(item=>string.Equals(item.Path,path,StringComparison.OrdinalIgnoreCase)))_files.Add(new(path));RefreshFiles();}
    private void Browse(){var d=new Microsoft.Win32.OpenFileDialog{Multiselect=true,Title="Add files to Peel"};if(d.ShowDialog(this)==true)AddFiles(d.FileNames);}
    private void RefreshFiles()
    {
        _clearSelection.Visibility=_files.Count==0?Visibility.Collapsed:Visibility.Visible;_fileRows.Children.Clear();if(_files.Count==0){var empty=PeelUi.Label("Drop files here\nor choose Add files.",13,PeelUi.Muted);empty.Margin=new Thickness(0,14,0,0);_fileRows.Children.Add(empty);}
        foreach(var item in _files.Take(100))
        {
            var row=new DockPanel{Margin=new Thickness(0,0,0,8)};var remove=PeelUi.Button("×",()=>{_files.Remove(item);RefreshFiles();},compact:true);remove.ToolTip="Remove from selection";remove.Background=Brushes.Transparent;remove.BorderThickness=new Thickness(0);remove.Padding=new Thickness(5,2,5,2);remove.MinHeight=24;remove.Foreground=PeelUi.Muted;DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
            var labels=new StackPanel{Margin=new Thickness(0,3,6,0)};var name=PeelUi.Label(item.Name,12,weight:FontWeights.SemiBold);name.TextWrapping=TextWrapping.NoWrap;name.TextTrimming=TextTrimming.CharacterEllipsis;name.ToolTip=item.Path;labels.Children.Add(name);labels.Children.Add(PeelUi.Label(item.IsDirectory?"Folder":item.Format.ToUpperInvariant(),10,PeelUi.Muted));row.Children.Add(labels);var tile=PeelUi.Panel(row,10);tile.Background=PeelUi.Card;tile.BorderThickness=new Thickness(0);tile.CornerRadius=new CornerRadius(10);tile.Margin=new Thickness(0,0,0,8);_fileRows.Children.Add(tile);
        }
        _heading.Text=_files.Count==0?"Ready when your files are.":_files.Count==1?_files[0].Name:$"{_files.Count} files. One place to work.";
        _summary.Text=_files.Count==0?"Convert, edit and organize your files.":_files.Count==1?(_files[0].IsDirectory?"Folder":_files[0].Format.ToUpperInvariant())+" · Original preserved":$"{_files.Count} selected · Original files preserved";Refresh();
    }
    private void Refresh(){foreach(var(name,tab)in _tabs){tab.Background=name==_section?PeelUi.Card:PeelUi.Brush("#00FFFFFF");tab.BorderBrush=PeelUi.Brush("#00FFFFFF");tab.Foreground=name==_section?PeelUi.Text:PeelUi.Muted;}_status.Text=_jobs.ActiveCount>0?$"●  {_jobs.ActiveCount} jobs in progress · two converting at once":"●  Local processing · no uploads";RefreshResults();}
    internal IReadOnlyList<Operation> SearchActions(string section,string query)
    {
        var wheel=section=="Tools"?WheelKind.Tools:section=="Actions"?WheelKind.Actions:WheelKind.Convert;
        var arranged=WheelLayout.Arrange(Catalog.For(_catalog(),wheel,_files,_tools),_settings.HiddenOperations,_settings.FavoriteOperations);
        return arranged.Where(op=>query.Length==0||(op.Label+" "+op.Subtitle+" "+op.Id).Contains(query,StringComparison.OrdinalIgnoreCase)).ToList();
    }
    private IEnumerable<Operation> Available()=>SearchActions(_section,_search.Text.Trim());
    internal void SetSearchText(string text)=>_search.Text=text;
    internal void SelectSection(string section){_section=section;Refresh();}
    private void RefreshResults()
    {
        _results.Children.Clear();if(_section=="Recent"){RenderHistory();return;}if(_section=="Presets"){RenderRecipes();return;}
        if(_files.Count==0)
        {
            var copy=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(12,8,12,8)};
            var art=new Grid{Width=104,Height=88,Margin=new Thickness(0,0,0,8)};
            var halo=new System.Windows.Shapes.Ellipse{Width=104,Height=88,Fill=new RadialGradientBrush(Color.FromArgb(72,55,240,98),Colors.Transparent)};art.Children.Add(halo);art.Children.Add(PeelUi.Mark(80));copy.Children.Add(art);
            var title=PeelUi.Label("Make files more useful.",22,weight:FontWeights.SemiBold);title.HorizontalAlignment=HorizontalAlignment.Center;copy.Children.Add(title);
            var detail=PeelUi.Label("Drop your files here to convert, crop, compress and more.",13,PeelUi.Muted);detail.TextAlignment=TextAlignment.Center;detail.MaxWidth=330;detail.Margin=new Thickness(0,10,0,22);copy.Children.Add(detail);
            var choose=PeelUi.Button("Choose files",Browse,true);choose.HorizontalAlignment=HorizontalAlignment.Center;copy.Children.Add(choose);
            var formats=PeelUi.Label("Images  ·  Video  ·  Audio  ·  Documents  ·  Archives",11,PeelUi.Muted);formats.TextAlignment=TextAlignment.Center;formats.Margin=new Thickness(0,12,0,0);copy.Children.Add(formats);
            var panel=new Border{Child=copy,Padding=new Thickness(10),Margin=new Thickness(0,6,0,10)};_results.Children.Add(panel);
            var tip=PeelUi.Label("A tap away. Drag a file and tap Shift for the conversion wheel.",12,PeelUi.Muted);tip.TextAlignment=TextAlignment.Center;_results.Children.Add(tip);return;
        }
        var operations=Available().ToList();if(operations.Count==0){_results.Children.Add(PeelUi.Label(_search.Text.Length>0?"No matching actions. Try another search.":"No compatible actions here. Try Tools or Actions, or select compatible files.",13,PeelUi.Muted));return;}
        var listHeading=PeelUi.Label(_section=="Convert"?"Convert to":_section=="Tools"?"Available tools":"File actions",11,PeelUi.Muted,FontWeights.SemiBold);listHeading.Margin=new Thickness(2,5,0,9);_results.Children.Add(listHeading);
        foreach(var op in operations)
        {
            var row=new Grid{Margin=new Thickness(10,10,10,10)};
            row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
            var badge=new Border{Width=34,Height=34,CornerRadius=new CornerRadius(9),Background=PeelUi.Ink,Child=PeelUi.Icon(op.Glyph,17,PeelUi.AccentInk),Margin=new Thickness(0,0,13,0),VerticalAlignment=VerticalAlignment.Center};row.Children.Add(badge);
            var labels=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,12,0)};var title=PeelUi.Label(op.Label,14,weight:FontWeights.SemiBold);labels.Children.Add(title);
            if(op.Subtitle is {Length:>0}){var subtitle=PeelUi.Label(op.Subtitle,11,PeelUi.Muted);subtitle.Margin=new Thickness(0,3,0,0);labels.Children.Add(subtitle);}
            Grid.SetColumn(labels,1);row.Children.Add(labels);
            var star=PeelUi.Button(_settings.FavoriteOperations.Contains(op.Id)?"★":"☆",()=>{if(!_settings.FavoriteOperations.Remove(op.Id))_settings.FavoriteOperations.Insert(0,op.Id);_settings.Save();RefreshResults();},compact:true);
            star.ToolTip="Pin to the top and the drag wheel";AutomationProperties.SetName(star,"Pin "+op.Label);star.Padding=new Thickness(7,4,7,4);star.MinHeight=28;star.Background=Brushes.Transparent;star.BorderThickness=new Thickness(0);star.Foreground=_settings.FavoriteOperations.Contains(op.Id)?PeelUi.AccentInk:PeelUi.Muted;star.Margin=new Thickness(0,0,12,0);Grid.SetColumn(star,2);row.Children.Add(star);
            var run=PeelUi.Button(op.Wheel==WheelKind.Convert?"Convert":"Run",()=>Run(op),compact:true);run.MinWidth=80;run.Background=PeelUi.Card;run.Foreground=PeelUi.Text;Grid.SetColumn(run,3);row.Children.Add(run);AutomationProperties.SetName(run,(op.Wheel==WheelKind.Convert?"Convert to ":"Run ")+op.Label);
            var panel=new Border{CornerRadius=new CornerRadius(10),Background=Brushes.Transparent,Child=row};
            panel.MouseEnter+=(_,_)=>panel.Background=PeelUi.Brush("#F5FAF7");panel.MouseLeave+=(_,_)=>panel.Background=Brushes.Transparent;_results.Children.Add(panel);
            _results.Children.Add(new Border{Height=1,Background=PeelUi.Brush("#EDF1EE"),Margin=new Thickness(10,0,10,0)});
        }
    }

    private void Run(Operation op,IReadOnlyDictionary<string,string>? parameters=null){_jobs.Start(op,_files.ToArray(),parameters);}
    private void RenderRecipes()
    {
        var note=PeelUi.Label("Your repeatable actions. Presets use the files selected on the left.",12,PeelUi.Muted);note.Margin=new Thickness(0,0,0,14);_results.Children.Add(note);
        var last=_catalog().FirstOrDefault(op=>op.Id==_settings.LastOperationId);
        if(last is not null){var box=new StackPanel();box.Children.Add(PeelUi.Label("Save last action · "+last.Label,13,weight:FontWeights.SemiBold));var name=PeelUi.Input("Preset name",last.Label);name.Margin=new Thickness(0,10,0,8);box.Children.Add(name);box.Children.Add(PeelUi.Button("Save preset",()=>{_settings.RememberRecipe(name.Text);RefreshResults();},true));var panel=PeelUi.Panel(box);panel.Margin=new Thickness(0,0,0,14);_results.Children.Add(panel);}
        if(_settings.Recipes.Count==0)_results.Children.Add(PeelUi.Label("Run a conversion or tool once, then save it here.",13,PeelUi.Muted));
        foreach(var recipe in _settings.Recipes.Where(r=>_search.Text.Length==0||r.Name.Contains(_search.Text,StringComparison.OrdinalIgnoreCase)))
        {
            var op=_catalog().FirstOrDefault(operation=>operation.Id==recipe.OperationId);var row=new DockPanel();
            var remove=PeelUi.Button("Remove",()=>{_settings.Recipes.Remove(recipe);_settings.Save();RefreshResults();},compact:true);DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
            var run=PeelUi.Button("Run",()=>{if(op is not null)Run(op,recipe.Parameters);},true,true);run.Margin=new Thickness(0,0,8,0);run.IsEnabled=op is not null&&op.AppliesTo(_files)&&op.IsAvailable(_tools,_files);run.ToolTip=run.IsEnabled?"Run on selected files":"Select compatible files first";DockPanel.SetDock(run,Dock.Right);row.Children.Add(run);
            var labels=new StackPanel();labels.Children.Add(PeelUi.Label(recipe.Name,14,weight:FontWeights.SemiBold));labels.Children.Add(PeelUi.Label((op?.Label??"Unavailable")+" · "+recipe.Parameters.Count+" saved settings",11,PeelUi.Muted));row.Children.Add(labels);var panel=PeelUi.Panel(row,13);panel.Margin=new Thickness(0,0,0,8);_results.Children.Add(panel);
        }
    }
    private void RenderHistory()
    {
        var clear=PeelUi.Button("Clear finished history",()=>_jobs.ForgetCompleted(),compact:true);clear.HorizontalAlignment=HorizontalAlignment.Left;clear.Margin=new Thickness(0,0,0,14);_results.Children.Add(clear);
        var jobs=_jobs.Records.Where(job=>_search.Text.Length==0||(job.Label+" "+string.Join(' ',job.Sources)).Contains(_search.Text,StringComparison.OrdinalIgnoreCase)).ToList();
        if(jobs.Count==0)_results.Children.Add(PeelUi.Label("Finished files and active jobs appear here. History stays on this PC.",13,PeelUi.Muted));
        foreach(var job in jobs)
        {
            var stack=new StackPanel();var title=new DockPanel();var state=PeelUi.Label(job.State,11,job.State=="Done"?PeelUi.AccentInk:PeelUi.Muted);DockPanel.SetDock(state,Dock.Right);title.Children.Add(state);title.Children.Add(PeelUi.Label(job.Label,14,weight:FontWeights.SemiBold));stack.Children.Add(title);stack.Children.Add(PeelUi.Label((job.Sources.Length==1?Path.GetFileName(job.Sources[0]):job.Sources.Length+" files")+" · "+job.Started.ToString("MMM d, HH:mm"),11,PeelUi.Muted));
            if(job.Detail is {Length:>0}){var detail=PeelUi.Label(job.Detail,12,PeelUi.Muted);detail.Margin=new Thickness(0,8,0,0);stack.Children.Add(detail);}
            if(job.State is "Working" or "Queued")stack.Children.Add(new ProgressBar{Minimum=0,Maximum=1,Value=job.Progress,IsIndeterminate=job.Progress<=0,Height=3,Margin=new Thickness(0,10,0,0),Foreground=PeelUi.Accent,Background=PeelUi.Rail});
            var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,10,0,0)};
            void AddButton(string label,Action action){var b=PeelUi.Button(label,action,compact:true);b.Margin=new Thickness(0,0,7,0);actions.Children.Add(b);}
            if(job.State is "Working" or "Queued")AddButton("Cancel",()=>_jobs.Cancel(job.Id));
            else
            {
                if(job.Outputs.FirstOrDefault() is {} output&&(File.Exists(output)||Directory.Exists(output))){AddButton("Open",()=>Process.Start(new ProcessStartInfo(output){UseShellExecute=true}));AddButton("Show in folder",()=>JobCard.Reveal(output));}
                var op=_catalog().FirstOrDefault(operation=>operation.Id==job.OperationId);
                if(op is not null&&job.Sources.All(p=>File.Exists(p)||Directory.Exists(p)))AddButton("Run again",()=>{SetFiles(job.Sources);_jobs.Start(op,_files.ToArray(),job.Parameters);});
            }
            stack.Children.Add(actions);var panel=PeelUi.Panel(stack,14);panel.Margin=new Thickness(0,0,0,9);_results.Children.Add(panel);
        }
    }
}
