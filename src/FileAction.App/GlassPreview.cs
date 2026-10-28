using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FileAction.App.Native;
using FileAction.App.UI;
using FileAction.App.Wheel;
using FileAction.Core;

namespace FileAction.App;

/// <summary>An isolated live compositor test. Does not load settings, hooks, clipboard or workers.</summary>
internal static class GlassPreview
{
    /// <param name="theme">"dark" or "light" forces the glass; otherwise it follows Windows.</param>
    /// <param name="scene">"bright" uses a pale, colourful background instead of the dark one.</param>
    public static int Run(string folder,string? theme=null,string? scene=null)
    {
        Directory.CreateDirectory(folder);
        var app=new Application{ShutdownMode=ShutdownMode.OnLastWindowClose};
        Theme.Override=theme switch{"dark"=>true,"light"=>false,_=>null};Theme.Refresh();Loc.Current=Language.English;
        var bright=scene=="bright";var ink=bright?PeelUi.Brush("#1B2B3A"):Brushes.White;
        var root=new Grid{Background=Pattern(bright)};
        var top=new StackPanel{Margin=new Thickness(28,24,28,0),VerticalAlignment=VerticalAlignment.Top};
        top.Children.Add(new TextBlock{Text="Peel · Live glass",FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=ink});
        top.Children.Add(new TextBlock{Text="A moving background tests the real Windows compositor. Escape closes this preview.",FontSize=13,Foreground=ink,Margin=new Thickness(0,8,0,0)});root.Children.Add(top);
        var references=new StackPanel{Margin=new Thickness(30,150,0,0),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top};
        foreach(var label in new[]{"MUSIC.mp3","PHOTO.png","PROJECTS","NOTES.txt"})references.Children.Add(new TextBlock{Text=label,FontSize=29,FontWeight=FontWeights.SemiBold,Foreground=ink,Margin=new Thickness(0,0,0,44)});root.Children.Add(references);
        var moving=new System.Windows.Shapes.Ellipse{Width=180,Height=180,Fill=PeelUi.Accent,Opacity=.8,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
        var move=new TranslateTransform(520,60);moving.RenderTransform=move;root.Children.Add(moving);
        move.BeginAnimation(TranslateTransform.XProperty,new DoubleAnimation(440,730,TimeSpan.FromSeconds(2.5)){AutoReverse=true,RepeatBehavior=RepeatBehavior.Forever});
        var host=new Window{Title="Peel · Glass background preview",Width=960,Height=680,Content=root,WindowStartupLocation=WindowStartupLocation.CenterScreen,Background=Brushes.Black,ResizeMode=ResizeMode.NoResize};
        WheelWindow? wheel=null;
        void Close(){wheel?.Finish();host.Close();}
        host.PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)Close();};host.Closed+=(_,_)=>wheel?.Finish();
        host.Loaded+=(_,_)=>
        {
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(350)};
            timer.Tick+=(_,_)=>
            {
                timer.Stop();
                var settings=new AppSettings();var tools=new ToolLocator();
                var ops=WheelController.BuildOperations();
                wheel=new WheelWindow(WheelMode.Click,WheelKind.Convert,[new FileItem("sample.mp3")],settings,(kind,items)=>Core.Operations.Catalog.For(ops,kind,items,tools),(_,_)=>{});
                wheel.Title="Peel · Glass wheel preview";
                var centre=host.PointToScreen(new Point(655,350));wheel.ShowAt(new Win32.POINT{X=(int)centre.X,Y=(int)centre.Y});
                wheel.Closed+=(_,_)=>{if(host.IsVisible)host.Close();};
                File.WriteAllText(Path.Combine(folder,"preview-opened.txt"),"Live native wheel displayed over animated synthetic background. NativeBlurActive="+wheel.NativeBlurActive);
            };timer.Start();
        };
        return app.Run(host);
    }
    /// <summary>Plays a scripted set of notifications: a message, a job that finishes and one that fails.</summary>
    public static int RunToasts(string folder,string? theme=null)
    {
        Directory.CreateDirectory(folder);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Theme.Override=theme switch{"dark"=>true,"light"=>false,_=>null};Theme.Refresh();Loc.Current=Language.English;
        var source=Path.Combine(folder,"Viagem praia.mov");var output=Path.Combine(folder,"Viagem praia.mp4");var image=Path.Combine(folder,"capa-reels.png");
        foreach(var file in new[]{source,output,image})if(!File.Exists(file))File.WriteAllBytes(file,[0]);
        void After(int ms,Action action){var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(ms)};timer.Tick+=(_,_)=>{timer.Stop();action();};timer.Start();}
        void Play(Shell.ToastCard card,int startMs,int durationMs,double until,Action finish)
        {
            After(startMs,()=>{var started=Environment.TickCount64;var tick=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(120)};
                tick.Tick+=(_,_)=>{var value=Math.Min(until,(Environment.TickCount64-started)/(double)durationMs);card.Report(value);if(value>=until){tick.Stop();finish();}};tick.Start();});
        }
        app.Startup+=(_,_)=>
        {
            After(400,()=>Shell.Toasts.Info("Peel is ready","Grab a file, tap Shift while dragging and drop it on a format."));
            After(1400,()=>{var job=Shell.Toasts.Job("Convert to MP4","Viagem praia.mov",source,()=>{});Play(job,900,3200,1,()=>job.Complete([output],null));});
            After(2300,()=>{var job=Shell.Toasts.Job("Convert to WEBP","capa-reels.png",image,()=>{});Play(job,500,2600,0.42,()=>job.Fail("FFmpeg couldn't read this file: unsupported codec."));});
            After(16000,()=>app.Shutdown());
            File.WriteAllText(Path.Combine(folder,"toasts-started.txt"),"Scripted notifications started.");
        };
        return app.Run();
    }

    /// <summary>Listens to the real clipboard and offers downloads, without hooks, settings or the tray.</summary>
    public static int RunLinks(string folder,string? theme=null)
    {
        Directory.CreateDirectory(folder);
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        Theme.Override=theme switch{"dark"=>true,"light"=>false,_=>null};Theme.Refresh();Loc.Current=Language.English;
        app.Startup+=(_,_)=>
        {
            var watcher=new Shell.LinkWatcher();var tools=new ToolLocator();
            watcher.Copied+=link=>{File.AppendAllText(Path.Combine(folder,"links.txt"),link.Site+" "+link.Url+Environment.NewLine);Jobs.VideoOffers.Show(link,tools);};
            var stop=new DispatcherTimer{Interval=TimeSpan.FromMinutes(3)};stop.Tick+=(_,_)=>{watcher.Dispose();app.Shutdown();};stop.Start();
            File.WriteAllText(Path.Combine(folder,"links-started.txt"),"Listening to the clipboard.");
        };
        return app.Run();
    }

    /// <summary>Downloads missing helper engines into tools\ with its progress card, then exits.</summary>
    public static int RunToolSetup()
    {
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};Theme.Refresh();Loc.Current=Language.English;
        app.Startup+=async(_,_)=>
        {
            await Shell.ToolSetup.EnsureAsync(new ToolLocator());
            var stop=new DispatcherTimer{Interval=TimeSpan.FromSeconds(9)};stop.Tick+=(_,_)=>app.Shutdown();stop.Start();
        };
        return app.Run();
    }

    private static DrawingBrush Pattern(bool bright)
    {
        var drawing=new DrawingGroup();drawing.Children.Add(new GeometryDrawing(PeelUi.Brush(bright?"#EAF3FB":"#172B34"),null,new RectangleGeometry(new Rect(0,0,960,680))));
        string[] light=["#DCEBFA","#F7E3EC","#E3F6E6","#FFF2D6"];
        for(int y=0;y<8;y++)for(int x=0;x<12;x++)drawing.Children.Add(new GeometryDrawing(PeelUi.Brush(bright?light[(x+2*y)%4]:(x+y)%2==0?"#234454":"#35505B"),null,new RectangleGeometry(new Rect(x*80,y*85,80,85))));
        var brush=new DrawingBrush(drawing){Stretch=Stretch.Fill};brush.Freeze();return brush;
    }
}
