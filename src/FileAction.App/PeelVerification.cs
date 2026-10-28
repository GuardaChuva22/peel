using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FileAction.App.Actions;
using FileAction.App.UI;
using FileAction.App.Jobs;
using FileAction.App.Wheel;
using FileAction.Core;
using FileAction.Core.Operations;
using ImageMagick;

namespace FileAction.App;

internal static class PeelVerification
{
    public static int Run(string folder)
    {
        var log=new List<string>();
        void Check(bool value,string text){if(!value)throw new InvalidOperationException(text);log.Add("PASS "+text);}
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);AppSettings.StorageOverride=Path.Combine(folder,"isolated-settings");
        try
        {
            var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Loc.Current=Language.English;Theme.Refresh();
            var imagePath=Path.Combine(folder,"sample-photo.png");
            using(var image=new MagickImage(MagickColors.DarkSlateGray,640,400))
            {image.Draw(new ImageMagick.Drawing.Drawables().FillColor(new MagickColor("#D2EE82")).Circle(420,180,500,180));image.Write(imagePath);}
            var textPath=Path.Combine(folder,"notes.txt");File.WriteAllText(textPath,"Peel test notes.");
            var settings=new AppSettings{Hotkey=HotkeyPreset.CtrlAltF};var tools=new ToolLocator();var jobs=new JobRunner(settings,tools);
            IReadOnlyList<Operation> Operations()=>[..WheelController.BuildOperations(),..FileActions.Build(settings,()=>{})];
            var window=new PeelWindow(settings,tools,jobs,Operations,()=>{});
            Render(window,Path.Combine(folder,"peel-empty.png"));
            window.SetFiles([imagePath]);
            Check(window.SearchActions("Convert","webp").Single().Id=="image.to.webp","search returns compatible image conversion");
            Check(!window.SearchActions("Convert","").Any(op=>op.Id=="image.to.png"),"current file format is not offered as a conversion");
            Check(window.SearchActions("Tools","crop").Any(op=>op.Id=="image.crop"),"image editing is searchable");
            window.SetFiles([imagePath,textPath]);
            Check(window.SearchActions("Convert","").Count==0,"mixed incompatible selections do not offer bogus conversions");
            Check(window.SearchActions("Actions","").Any(op=>op.Id=="action.path"),"mixed selections retain file actions");
            window.SetFiles([imagePath]);window.SelectSection("Convert");Render(window,Path.Combine(folder,"peel-convert.png"));
            window.SetSearchText("webp");Render(window,Path.Combine(folder,"peel-single-result.png"));window.SetSearchText("");window.SelectSection("Tools");Render(window,Path.Combine(folder,"peel-tools.png"));
            var op=Catalog.Build().Single(op=>op.Id=="image.crop");
            jobs.Start(op,[new(imagePath)],new Dictionary<string,string>{{"x","20"},{"y","30"},{"width","200"},{"height","120"}});
            PumpUntil(()=>jobs.ActiveCount==0&&settings.History.Count>0,TimeSpan.FromSeconds(30));
            var job=settings.History.Single();
            Check(job.State=="Done"&&job.Outputs.Length==1,"workspace job lifecycle records a completed real conversion");
            using(var cropped=new MagickImage(job.Outputs[0]))Check(cropped.Width==200&&cropped.Height==120,"journal result is a valid cropped image");
            Check(File.Exists(imagePath),"workspace conversion retains its source");
            settings.RememberRecipe("Social crop");
            Check(settings.Recipes.Single().Parameters["width"]=="200","preset captures real tool parameters");
            settings.RememberRecipe("social crop");Check(settings.Recipes.Count==1,"preset names replace case-insensitively");
            settings.Save();var reloaded=AppSettings.Load();Check(reloaded.Recipes.Single().OperationId=="image.crop"&&reloaded.History.Single().State=="Done","presets and history persist across settings reload");
            jobs.Start(op,[new(imagePath)],settings.Recipes.Single().Parameters);PumpUntil(()=>jobs.ActiveCount==0,TimeSpan.FromSeconds(30));
            Check(settings.History.Count==2&&settings.History[0].Outputs[0]!=job.Outputs[0],"saved parameters can be rerun without overwriting results");
            window.SelectSection("Presets");Render(window,Path.Combine(folder,"peel-presets.png"));
            window.SelectSection("Recent");Render(window,Path.Combine(folder,"peel-recent.png"));
            var broken=Path.Combine(folder,"broken.png");File.WriteAllText(broken,"bad png");var convert=Catalog.Build().Single(op=>op.Id=="image.to.jpg");
            jobs.Start(convert,[new(broken)]);PumpUntil(()=>jobs.ActiveCount==0,TimeSpan.FromSeconds(30));Check(settings.History[0].State=="Failed"&&settings.History[0].Detail is {Length:>0},"failed jobs remain reviewable with a real error");
            jobs.ForgetCompleted();Check(settings.History.Count==0,"clear history removes records while preserving files");Check(File.Exists(job.Outputs[0]),"clearing history does not delete converted files");
            settings.KeepHistory=false;jobs.Start(convert,[new(imagePath)]);PumpUntil(()=>jobs.ActiveCount==0,TimeSpan.FromSeconds(30));Check(settings.History.Count==0,"history opt-out retains no completed file records");
            var prefs=new PeelPreferences(settings,tools,()=>{},()=>{});Render(prefs,Path.Combine(folder,"peel-preferences.png"));
            foreach(var section in new[]{"Keys & wheel","Quality","Engines","About"}){prefs.SelectSection(section);Render(prefs,Path.Combine(folder,"peel-preferences-"+section.Split(' ')[0].ToLowerInvariant()+".png"));}
            window.Width=820;window.Height=560;window.SelectSection("Convert");Render(window,Path.Combine(folder,"peel-compact.png"));
            prefs.Close();window.Close();
            File.WriteAllLines(Path.Combine(folder,"verification.txt"),log);return 0;
        }
        catch(Exception ex){log.Add("FAIL "+ex);File.WriteAllLines(Path.Combine(folder,"verification.txt"),log);return 1;}
    }
    private static void PumpUntil(Func<bool> done,TimeSpan limit)
    {
        var deadline=DateTime.UtcNow+limit;
        while(!done())
        {
            if(DateTime.UtcNow>deadline)throw new TimeoutException("Peel verification timed out.");
            var frame=new DispatcherFrame();var timer=new DispatcherTimer(TimeSpan.FromMilliseconds(15),DispatcherPriority.Background,(_,_)=>frame.Continue=false,Dispatcher.CurrentDispatcher);timer.Start();Dispatcher.PushFrame(frame);timer.Stop();
        }
    }
    private static void Render(Window window,string path)
    {
        var root=(FrameworkElement)window.Content;
        root.Measure(new Size(window.Width,window.Height));root.Arrange(new Rect(0,0,window.Width,window.Height));root.UpdateLayout();
        var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.Width,(int)window.Height,96,96,System.Windows.Media.PixelFormats.Pbgra32);image.Render(root);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));using var output=File.Create(path);encoder.Save(output);
    }
}
