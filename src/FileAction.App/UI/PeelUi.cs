using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Automation;
using System.Windows.Markup;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FileAction.App.UI;

/// <summary>Peel's white-and-mint design system, built with native WPF controls.</summary>
internal static class PeelUi
{
    public static readonly Brush Ink = Brush("#F5F8F6"), Rail = Brush("#FFFFFF"), Card = Brush("#FFFFFF"), Edge = Brush("#DEE7E1"), Text = Brush("#102018"), Muted = Brush("#64736A"), Accent = Brush("#37F062"), OnAccent = Brush("#103D20"), AccentInk = Brush("#147639"), Soft = Brush("#EAFBEF");
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static Brush Brush(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    public static TextBlock Label(string text, double size=14, Brush? color=null, FontWeight? weight=null) => new()
    { Text=text, FontSize=size, Foreground=color??Text, FontWeight=weight??FontWeights.Normal, TextWrapping=TextWrapping.Wrap, FontFamily=Font };
    public static TextBlock Icon(string glyph,double size=18,Brush? color=null) => new()
    { Text=glyph, FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize=size, Foreground=color??Text, VerticalAlignment=VerticalAlignment.Center, HorizontalAlignment=HorizontalAlignment.Center };
    public static Button Button(string label, Action action, bool primary=false, bool compact=false)
    {
        var button = new Button
        {
            Content=label, Foreground=primary?OnAccent:Text, Background=primary?Accent:Card,
            BorderBrush=primary?Brush("#2ADA55"):Edge, BorderThickness=new Thickness(1),
            Padding=compact?new Thickness(12,6,12,6):new Thickness(17,10,17,10),
            FontFamily=Font, FontSize=13, FontWeight=primary?FontWeights.SemiBold:FontWeights.Medium,
            Cursor=Cursors.Hand, MinHeight=compact?32:40, VerticalContentAlignment=VerticalAlignment.Center,
        };
        var template=new ControlTemplate(typeof(Button));
        var border=new FrameworkElementFactory(typeof(Border),"Surface");
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(compact?9:11));
        foreach(var property in new[]{Border.BackgroundProperty,Border.BorderBrushProperty,Border.BorderThicknessProperty,Border.PaddingProperty})
            border.SetBinding(property,new System.Windows.Data.Binding(property.Name){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        var content=new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetBinding(ContentPresenter.HorizontalAlignmentProperty,new System.Windows.Data.Binding("HorizontalContentAlignment"){RelativeSource=System.Windows.Data.RelativeSource.TemplatedParent});
        content.SetValue(ContentPresenter.VerticalAlignmentProperty,VerticalAlignment.Center);border.AppendChild(content);template.VisualTree=border;
        var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,primary?Brush("#5BF57F"):Soft,"Surface"));hover.Setters.Add(new Setter(Border.BorderBrushProperty,Brush("#9EE5AE"),"Surface"));template.Triggers.Add(hover);
        var press=new Trigger{Property=System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty,Value=true};press.Setters.Add(new Setter(UIElement.OpacityProperty,.72));template.Triggers.Add(press);
        var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,AccentInk,"Surface"));template.Triggers.Add(focus);
        var disabled=new Trigger{Property=UIElement.IsEnabledProperty,Value=false};disabled.Setters.Add(new Setter(UIElement.OpacityProperty,.4));template.Triggers.Add(disabled);
        button.Template=template;AutomationProperties.SetName(button,label);button.Click+=(_,_)=>action();return button;
    }
    public static TextBox Input(string name,string value="")
    {
        var input=new TextBox{Text=value,Background=Card,Foreground=Text,CaretBrush=AccentInk,BorderBrush=Edge,BorderThickness=new Thickness(1),Padding=new Thickness(13,11,13,11),FontFamily=Font,FontSize=13,MinHeight=42,VerticalContentAlignment=VerticalAlignment.Center,SelectionBrush=Brush("#B5F8C6")};AutomationProperties.SetName(input,name);return input;
    }
    public static FrameworkElement Mark(double size=34) => new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/Peel.png")),Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false};
    public static DropShadowEffect Shadow(double blur=22,double opacity=.07) => new(){Color=Color.FromRgb(28,78,44),BlurRadius=blur,ShadowDepth=4,Direction=270,Opacity=opacity};
    public static Border Panel(UIElement content,int padding=16) => new(){Background=Card,BorderBrush=Edge,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(15),Padding=new Thickness(padding),Child=content};
    public static void ApplyResources(FrameworkElement target)
    {
        const string xaml="""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <Style TargetType="TextBox"><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TextBox">
            <Border x:Name="Box" Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="1" CornerRadius="11"><ScrollViewer x:Name="PART_ContentHost"/></Border>
            <ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="Box" Property="BorderBrush" Value="#147639"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="CheckBox"><Setter Property="Cursor" Value="Hand"/><Setter Property="VerticalContentAlignment" Value="Center"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="CheckBox"><Grid>
              <Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
              <ContentPresenter VerticalAlignment="Center" Margin="0,0,12,0"/>
              <Border x:Name="Track" Grid.Column="1" Width="38" Height="23" Background="#D8E2DB" CornerRadius="12" BorderBrush="Transparent" BorderThickness="1"><Ellipse x:Name="Thumb" Fill="White" Width="17" Height="17" HorizontalAlignment="Left" Margin="2,0,2,0"/></Border>
            </Grid><ControlTemplate.Triggers>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Track" Property="Background" Value="#37F062"/><Setter TargetName="Thumb" Property="HorizontalAlignment" Value="Right"/></Trigger>
              <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Track" Property="BorderBrush" Value="#147639"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger>
            </ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBox"><Setter Property="Foreground" Value="#102018"/><Setter Property="Background" Value="White"/><Setter Property="BorderBrush" Value="#DEE7E1"/><Setter Property="FontFamily" Value="Segoe UI Variable Text, Segoe UI"/><Setter Property="MinHeight" Value="36"/>
            <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBox"><Grid>
              <ToggleButton x:Name="Button" Focusable="False" ClickMode="Press" IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
                <ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border x:Name="Surface" Background="White" BorderBrush="#DEE7E1" BorderThickness="1" CornerRadius="9"><Path Data="M0 0 L4 4 L8 0" Stroke="#64736A" StrokeThickness="1.3" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,12,0"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#EAFBEF"/></Trigger><Trigger Property="IsChecked" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#147639"/></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template>
              </ToggleButton>
              <ContentPresenter IsHitTestVisible="False" Margin="12,7,32,7" VerticalAlignment="Center" Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
              <Border x:Name="FocusRing" IsHitTestVisible="False" BorderBrush="Transparent" BorderThickness="1" CornerRadius="9"/>
              <Popup x:Name="PART_Popup" Placement="Bottom" AllowsTransparency="True" Focusable="False" IsOpen="{TemplateBinding IsDropDownOpen}">
                <Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" MaxHeight="280" Margin="0,4,0,4" Padding="4" Background="White" BorderBrush="#DEE7E1" BorderThickness="1" CornerRadius="11"><ScrollViewer CanContentScroll="True" VerticalScrollBarVisibility="Auto"><ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/></ScrollViewer></Border>
              </Popup>
            </Grid><ControlTemplate.Triggers><Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="FocusRing" Property="BorderBrush" Value="#147639"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.4"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
          </Style>
          <Style TargetType="ComboBoxItem"><Setter Property="Foreground" Value="#102018"/><Setter Property="Background" Value="White"/><Setter Property="Padding" Value="10,8"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ComboBoxItem"><Border x:Name="Row" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" CornerRadius="7"><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property="IsHighlighted" Value="True"><Setter TargetName="Row" Property="Background" Value="#EAFBEF"/></Trigger><Trigger Property="IsSelected" Value="True"><Setter TargetName="Row" Property="Background" Value="#C8F9D5"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
          <Style TargetType="ScrollBar"><Setter Property="Width" Value="9"/><Setter Property="Background" Value="Transparent"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ScrollBar"><Grid Background="Transparent"><Track x:Name="PART_Track" IsDirectionReversed="True"><Track.Thumb><Thumb><Thumb.Template><ControlTemplate TargetType="Thumb"><Border Background="#CCD9D0" CornerRadius="4" Margin="2,0"/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb></Track></Grid></ControlTemplate></Setter.Value></Setter></Style>
        </ResourceDictionary>
        """;
        target.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(xaml));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd,int attribute,ref int value,int size);
    public static void Configure(Window window,string title,int width,int height)
    {
        window.Title=title;window.Width=width;window.Height=height;window.Background=Ink;window.Foreground=Text;window.FontFamily=Font;window.WindowStartupLocation=WindowStartupLocation.CenterScreen;window.WindowStyle=WindowStyle.None;
        TextOptions.SetTextFormattingMode(window,TextFormattingMode.Display);TextOptions.SetTextRenderingMode(window,TextRenderingMode.ClearType);
        System.Windows.Shell.WindowChrome.SetWindowChrome(window,new System.Windows.Shell.WindowChrome{CaptionHeight=0,ResizeBorderThickness=new Thickness(6),GlassFrameThickness=new Thickness(0),CornerRadius=new CornerRadius(16)});
        window.SourceInitialized+=(_,_)=>{try{int corners=2;DwmSetWindowAttribute(new WindowInteropHelper(window).Handle,33,ref corners,sizeof(int));}catch(DllNotFoundException){}catch(EntryPointNotFoundException){}};
        window.Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/Peel.ico"));ApplyResources(window);
    }
    public static DockPanel TitleBar(Window window,string caption,Action? preferences=null)
    {
        var header=new DockPanel{Margin=new Thickness(21,12,18,12),LastChildFill=true,Background=Brush("#00FFFFFF")};
        var close=Button("×",window.Close,compact:true);close.ToolTip="Close to tray";close.Width=34;DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var minimize=Button("−",()=>window.WindowState=WindowState.Minimized,compact:true);minimize.Width=34;minimize.Margin=new Thickness(0,0,6,0);DockPanel.SetDock(minimize,Dock.Right);header.Children.Add(minimize);
        if(preferences is not null){var prefs=Button("Preferences",preferences,compact:true);prefs.Margin=new Thickness(0,0,14,0);DockPanel.SetDock(prefs,Dock.Right);header.Children.Add(prefs);}
        var identity=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center,Background=Brush("#00FFFFFF")};identity.Children.Add(Mark(34));var name=Label(caption,21,weight:FontWeights.SemiBold);name.Margin=new Thickness(9,0,0,0);identity.Children.Add(name);header.Children.Add(identity);
        identity.MouseLeftButtonDown+=(_,e)=>{if(e.ClickCount==2)window.WindowState=window.WindowState==WindowState.Maximized?WindowState.Normal:WindowState.Maximized;else window.DragMove();};return header;
    }
}
