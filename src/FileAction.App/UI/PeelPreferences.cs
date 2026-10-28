using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using FileAction.Core;
using FileAction.App.Input;
using FileAction.App.Shell;

namespace FileAction.App.UI;

/// <summary>Peel preferences, separate from upstream's long settings surface.</summary>
internal sealed class PeelPreferences:Window
{
    private static PeelPreferences? _open;
    private readonly AppSettings _settings;
    private readonly ToolLocator _tools;
    private readonly Action _changed,_recover;
    private readonly StackPanel _content=new();
    private readonly Dictionary<string,Button> _navigation=new();
    private readonly ScrollViewer _scroll;
    public static void ShowSingle(AppSettings settings,ToolLocator tools,Action changed,Action recover)
    {
        if(_open is null){_open=new(settings,tools,changed,recover);_open.Show();}_open.Activate();
    }
    internal PeelPreferences(AppSettings settings,ToolLocator tools,Action changed,Action recover)
    {
        _settings=settings;_tools=tools;_changed=changed;_recover=recover;
        PeelUi.Configure(this,"Peel · preferences",860,710);MinWidth=760;MinHeight=560;
        var root=new DockPanel{Background=PeelUi.Ink};PeelUi.ApplyResources(root);
        var header=PeelUi.TitleBar(this,"Preferences");DockPanel.SetDock(header,Dock.Top);root.Children.Add(header);
        var sidebar=new DockPanel{Width=194,Background=PeelUi.Card};DockPanel.SetDock(sidebar,Dock.Left);root.Children.Add(sidebar);
        var brand=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(22,24,20,24)};
        brand.Children.Add(PeelUi.Mark(35));var brandText=PeelUi.Label("Peel",22,weight:FontWeights.SemiBold);
        brandText.Margin=new Thickness(10,0,0,0);brandText.VerticalAlignment=VerticalAlignment.Center;brand.Children.Add(brandText);
        DockPanel.SetDock(brand,Dock.Top);sidebar.Children.Add(brand);
        var footer=PeelUi.Label("YOUR FILES. ALL LOCAL.",10,PeelUi.Muted,FontWeights.SemiBold);footer.Margin=new Thickness(24,20,12,22);
        DockPanel.SetDock(footer,Dock.Bottom);sidebar.Children.Add(footer);
        var nav=new StackPanel{Margin=new Thickness(12,0,12,0)};sidebar.Children.Add(nav);
        foreach(var (name,glyph) in new[]{("General","\uE713"),("Keys & wheel","\uE765"),("Quality","\uE790"),("Engines","\uE9D9"),("About","\uE946")})
        {
            var captured=name;var button=PeelUi.Button(name,()=>Build(captured));button.Margin=new Thickness(0,0,0,5);
            button.HorizontalAlignment=HorizontalAlignment.Stretch;button.HorizontalContentAlignment=HorizontalAlignment.Left;button.Padding=new Thickness(12,11,12,11);
            var line=new StackPanel{Orientation=Orientation.Horizontal};var icon=PeelUi.Icon(glyph,17);icon.Margin=new Thickness(0,0,10,0);icon.VerticalAlignment=VerticalAlignment.Center;
            line.Children.Add(icon);line.Children.Add(PeelUi.Label(name,13));button.Content=line;_navigation[name]=button;nav.Children.Add(button);
        }
        _content.Margin=new Thickness(30,26,30,30);
        _scroll=new ScrollViewer{Content=_content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
        root.Children.Add(_scroll);Content=root;Build("General");Closed+=(_,_)=>_open=null;
    }
    private void Save(){_settings.Save();_changed();}
    private void Intro(string title,string detail){_content.Children.Clear();_content.Children.Add(PeelUi.Label(title,24,weight:FontWeights.SemiBold));var sub=PeelUi.Label(detail,13,PeelUi.Muted);sub.Margin=new Thickness(0,7,0,24);_content.Children.Add(sub);}
    private void Heading(string title){var label=PeelUi.Label(title,12,PeelUi.Muted,FontWeights.SemiBold);label.Margin=new Thickness(2,15,0,9);_content.Children.Add(label);}
    private Border Setting(string title,string detail,UIElement? control=null)
    {
        var row=new Grid();row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var text=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,control is null?0:18,0)};
        text.Children.Add(PeelUi.Label(title,13,weight:FontWeights.SemiBold));
        if(!string.IsNullOrEmpty(detail)){var sub=PeelUi.Label(detail,12,PeelUi.Muted);sub.Margin=new Thickness(0,5,0,0);text.Children.Add(sub);}
        row.Children.Add(text);if(control is FrameworkElement element){element.VerticalAlignment=VerticalAlignment.Center;Grid.SetColumn(element,1);row.Children.Add(element);}
        var panel=PeelUi.Panel(row,17);panel.Margin=new Thickness(0,0,0,8);_content.Children.Add(panel);return panel;
    }
    private void Toggle(string title,string detail,bool value,Action<bool> changed)
    {
        var check=new CheckBox{IsChecked=value,VerticalAlignment=VerticalAlignment.Center};AutomationProperties.SetName(check,title);
        check.Checked+=(_,_)=>{changed(true);Save();};check.Unchecked+=(_,_)=>{changed(false);Save();};Setting(title,detail,check);
    }
    private sealed record Choice<T>(string Label,T Value);
    private static ComboBox Picker<T>(string name,IEnumerable<Choice<T>> choices,T selected,double width=156)
    {
        var combo=new ComboBox{Width=width,ItemsSource=choices.ToList(),DisplayMemberPath="Label",SelectedValuePath="Value",SelectedValue=selected,Padding=new Thickness(10,8,10,8),MinHeight=38,FontSize=13,Foreground=PeelUi.Text,Background=PeelUi.Card,BorderBrush=PeelUi.Edge};
        AutomationProperties.SetName(combo,name);return combo;
    }
    internal void SelectSection(string section)=>Build(section);
    private void Build(string section)
    {
        foreach(var (name,button) in _navigation)
        {
            var selected=name==section;button.Background=selected?PeelUi.Soft:Brushes.Transparent;button.BorderBrush=selected?PeelUi.Soft:Brushes.Transparent;
            button.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
            if(button.Content is StackPanel line)foreach(var child in line.Children.OfType<TextBlock>()){child.Foreground=selected?PeelUi.AccentInk:PeelUi.Muted;child.FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;}
        }
        _scroll.ScrollToTop();
        switch(section)
        {
            case "General":
                Intro("General","Small preferences. A smoother everyday workflow.");
                Heading("WINDOWS");
                Toggle("Launch at sign-in","Ready in the notification area when Windows starts.",ShellIntegration.IsAutostartEnabled,ShellIntegration.SetAutostart);
                Toggle("Add Peel to Explorer's context menu","Available under Show more options on Windows 11.",ShellIntegration.IsContextMenuEnabled,ShellIntegration.SetContextMenu);
                Heading("RESULTS & PRIVACY");
                Toggle("Keep recent jobs","Stores up to 30 completed jobs and their filenames on this PC.",_settings.KeepHistory,v=>{_settings.KeepHistory=v;if(!v)_settings.History.RemoveAll(job=>job.State is not ("Queued" or "Working"));});
                Toggle("Reveal completed files","Opens the result folder when a job finishes.",_settings.RevealResult,v=>_settings.RevealResult=v);
                Heading("OUTPUT FOLDER");
                var folder=new StackPanel();folder.Children.Add(PeelUi.Label(_settings.OutputDirectory is null?"Beside the original":"Custom folder",13,weight:FontWeights.SemiBold));
                var output=PeelUi.Label(_settings.OutputDirectory??"New copies stay close to their source files.",12,PeelUi.Muted);output.Margin=new Thickness(0,5,0,14);folder.Children.Add(output);
                var row=new WrapPanel();var choose=PeelUi.Button("Choose folder…",()=>{var d=new Microsoft.Win32.OpenFolderDialog();if(d.ShowDialog(this)==true){_settings.OutputDirectory=d.FolderName;Save();Build(section);}},compact:true);choose.Margin=new Thickness(0,0,8,6);row.Children.Add(choose);
                if(_settings.OutputDirectory is not null){var reset=PeelUi.Button("Use source folder",()=>{_settings.OutputDirectory=null;Save();Build(section);},compact:true);row.Children.Add(reset);}
                folder.Children.Add(row);_content.Children.Add(PeelUi.Panel(folder,17));break;
            case "Keys & wheel":
                Intro("Keys & wheel","Drag a file, then tap a modifier to open its quick actions.");
                Heading("DRAG GESTURES");
                foreach(var kind in Enum.GetValues<WheelKind>())
                {
                    var wheel=kind;var combo=Picker(AppSettings.WheelName(wheel)+" modifier",Enum.GetValues<ModifierKey>().Select(key=>new Choice<ModifierKey>(key==ModifierKey.None?"Off":key.ToString(),key)),_settings.KeyFor(wheel),122);
                    combo.SelectionChanged+=(_,_)=>
                    {
                        if(combo.SelectedValue is not ModifierKey key)return;var old=_settings.KeyFor(wheel);
                        void Set(WheelKind w,ModifierKey k){if(w==WheelKind.Convert)_settings.ConvertKey=k;else if(w==WheelKind.Tools)_settings.ToolsKey=k;else _settings.ActionsKey=k;}
                        foreach(var other in Enum.GetValues<WheelKind>())if(other!=wheel&&key!=ModifierKey.None&&_settings.KeyFor(other)==key)Set(other,old);
                        Set(wheel,key);Save();Build(section);
                    };Setting(AppSettings.WheelName(wheel),wheel switch{WheelKind.Convert=>"Change the file format.",WheelKind.Tools=>"Edit, compress, resize and more.",_=>"Copy, move, share and organize."},combo);
                }
                Heading("KEYBOARD SHORTCUT");
                var hotkey=Picker("Open workspace shortcut",Enum.GetValues<HotkeyPreset>().Select(key=>new Choice<HotkeyPreset>(AppSettings.HotkeyName(key),key)),_settings.Hotkey,175);
                hotkey.SelectionChanged+=(_,_)=>{if(hotkey.SelectedValue is HotkeyPreset key){_settings.Hotkey=key;Save();}};Setting("Open workspace","Available from any app.",hotkey);
                Heading("GESTURE CONTROL");
                Toggle("Pause drag gestures","The workspace and keyboard shortcut stay available.",_settings.Paused,v=>_settings.Paused=v);
                var recoveryFeedback=PeelUi.Label("",12,PeelUi.AccentInk);recoveryFeedback.Margin=new Thickness(0,10,0,0);
                _content.Children.Add(PeelUi.Button("Reconnect drag gestures",()=>{_recover();recoveryFeedback.Text="Reconnected. Drag a file to try the wheel.";},compact:true));_content.Children.Add(recoveryFeedback);
                var tip=PeelUi.Label("Pin actions with ★ in the workspace. Pinned actions appear first on the wheel. Hover the center arrows to change pages while dragging.",12,PeelUi.Muted);tip.Margin=new Thickness(0,16,0,0);_content.Children.Add(tip);break;
            case "Quality":
                Intro("Quality","The defaults for new conversions and compression tools.");
                void Number(string title,int value,int min,int max,Action<int> set)
                {
                    var input=PeelUi.Input(title,value.ToString());input.Width=96;input.HorizontalAlignment=HorizontalAlignment.Right;
                    var panel=Setting(title,$"Whole number from {min} to {max}.",input);var body=new StackPanel();var original=panel.Child;panel.Child=null;body.Children.Add(original!);
                    var feedback=PeelUi.Label("",12,PeelUi.Muted);feedback.Visibility=Visibility.Collapsed;feedback.Margin=new Thickness(0,9,0,0);body.Children.Add(feedback);panel.Child=body;
                    input.LostKeyboardFocus+=(_,_)=>{feedback.Visibility=Visibility.Visible;if(int.TryParse(input.Text,out var number)&&number>=min&&number<=max){set(number);Save();feedback.Text="Saved";feedback.Foreground=PeelUi.AccentInk;input.BorderBrush=PeelUi.Edge;}else{feedback.Text=$"Enter a whole number from {min} to {max}.";feedback.Foreground=Theme.Error;input.BorderBrush=Theme.Error;}};
                }
                Number("Image export quality",_settings.Quality.JpegQuality,50,100,v=>_settings.Quality.JpegQuality=v);
                Number("Image compression quality",_settings.Quality.CompressQuality,30,95,v=>_settings.Quality.CompressQuality=v);
                Number("PDF image resolution (DPI)",_settings.Quality.PdfDpi,72,600,v=>_settings.Quality.PdfDpi=v);
                var qualityTip=PeelUi.Label("Higher values keep more detail and usually produce larger files.",12,PeelUi.Muted);qualityTip.Margin=new Thickness(2,12,0,0);_content.Children.Add(qualityTip);break;
            case "Engines":
                Intro("Engines","Local helpers start only when a job needs them.");
                foreach(var tool in Enum.GetValues<ExternalTool>())
                {
                    var stack=new StackPanel();var path=_tools.Find(tool);var line=new DockPanel();var status=PeelUi.Label(path is null?"Unavailable":"Ready",11,path is null?PeelUi.Muted:PeelUi.AccentInk,FontWeights.SemiBold);DockPanel.SetDock(status,Dock.Right);line.Children.Add(status);line.Children.Add(PeelUi.Label(ToolLocator.DisplayName(tool),14,weight:FontWeights.SemiBold));stack.Children.Add(line);
                    var text=PeelUi.Label(path??"Actions that need this helper are hidden.",11,PeelUi.Muted);text.Margin=new Thickness(0,9,0,0);stack.Children.Add(text);var panel=PeelUi.Panel(stack,17);panel.Margin=new Thickness(0,0,0,9);_content.Children.Add(panel);
                }
                _content.Children.Add(PeelUi.Button("Check engines again",()=>{_tools.Refresh();Build(section);},compact:true));var engineTip=PeelUi.Label("Document conversion works without Microsoft Office.",12,PeelUi.Muted);engineTip.Margin=new Thickness(0,14,0,0);_content.Children.Add(engineTip);break;
            default:
                Intro("About Peel","Good tools get out of your way.");
                var identity=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,2,0,22)};identity.Children.Add(PeelUi.Mark(64));var aboutTitle=new StackPanel{VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(16,0,0,0)};aboutTitle.Children.Add(PeelUi.Label("Peel",27,weight:FontWeights.SemiBold));aboutTitle.Children.Add(PeelUi.Label("Version 0.3.1 · Native for Windows",12,PeelUi.Muted));identity.Children.Add(aboutTitle);_content.Children.Add(identity);
                Setting("Made for the everyday","Quick file conversions, useful tools and a place for your favorite actions. Your files stay on this PC.");
                Heading("BUILT ON GOOD FOUNDATIONS");
                var credits=PeelUi.Label("An independent app built on FileAction by Metamorfolog, inspired by Tangerine. Peel adds its own workspace, presets, recent jobs and visual identity. Inherited code and helper engines retain their original attribution.",12,PeelUi.Muted);_content.Children.Add(credits);
                Heading("FORMAT NOTES");
                _content.Children.Add(PeelUi.Label("Coverage limits: Apple Pages/RTFD and AirDrop are unavailable. HEIC input is supported; this engine does not export HEIC. Editable PDF conversion and automated privacy suggestions need review.",12,PeelUi.Muted));
                var creditsButton=PeelUi.Button("Third-party notices",()=>{var file=Path.Combine(AppContext.BaseDirectory,"THIRD-PARTY-NOTICES.md");if(File.Exists(file))System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file){UseShellExecute=true});},compact:true);creditsButton.Margin=new Thickness(0,22,0,0);_content.Children.Add(creditsButton);break;
        }
    }
}
