using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace NexusFormatter {
 internal enum DialogTone { Information,Warning,Error }
 // UI only: no drive access or operation callbacks. Only the explicit action
 // button returns true; cancel, Escape, X and window-close all decline.
 internal sealed class NexusDialog {
  internal readonly Window Window;
  internal readonly Button Cancel,Accept,Dismiss;
  internal readonly ScrollViewer MessageScroll;
  internal NexusDialog(IntPtr owner,string title,string message,string action,DialogTone tone) {
   Window=(Window)XamlReader.Parse(Program.Resource("Dialog.xaml"));
   Window.Resources=(ResourceDictionary)XamlReader.Parse(Program.Resource("Theme.xaml"));
   Window.Title=title;
   if(owner!=IntPtr.Zero)new System.Windows.Interop.WindowInteropHelper(Window).Owner=owner;
   else {Window.WindowStartupLocation=WindowStartupLocation.CenterScreen;Window.ShowInTaskbar=true;}
   Window.Width=Math.Min(570,Math.Max(280,SystemParameters.WorkArea.Width-32));
   Window.Height=Math.Min(action==null?440:585,Math.Max(240,SystemParameters.WorkArea.Height-32));
   ((TextBlock)Window.FindName("DialogHeading")).Text=title;
   ((TextBlock)Window.FindName("DialogMessage")).Text=message;
   var symbol=(TextBlock)Window.FindName("StatusSymbol");
   symbol.Text=tone==DialogTone.Information?"i":"!";
   symbol.Foreground=new SolidColorBrush((Color)ColorConverter.ConvertFromString(tone==DialogTone.Error?"#FFB5AB":tone==DialogTone.Warning?"#FFD39A":"#F04CEC"));
   MessageScroll=(ScrollViewer)Window.FindName("MessageScroll");
   Cancel=(Button)Window.FindName("CancelDialog");Accept=(Button)Window.FindName("AcceptDialog");Dismiss=(Button)Window.FindName("DismissButton");
   if(action==null) {
    Accept.Visibility=Visibility.Collapsed;Cancel.Content="Close";Cancel.Margin=new Thickness(0);
    ((TextBlock)Window.FindName("DialogNote")).Visibility=Visibility.Collapsed;
   } else {Accept.Content=action;}
   // Do not use an affirmative default, including when Enter is pressed
   // immediately after opening. Tab/click still allows an intentional action.
   Cancel.Click+=delegate{Window.DialogResult=false;};
   Dismiss.Click+=delegate{Window.DialogResult=false;};
   Accept.Click+=delegate{if(action!=null)Window.DialogResult=true;};
   Window.PreviewKeyDown+=delegate(object sender,KeyEventArgs e){if(e.Key==Key.Escape){e.Handled=true;Window.DialogResult=false;}};
   Window.Loaded+=delegate{Cancel.Focus();Keyboard.Focus(Cancel);};
   ((Grid)Window.FindName("DragArea")).MouseLeftButtonDown+=delegate(object sender,MouseButtonEventArgs e){if(e.ButtonState==MouseButtonState.Pressed)Window.DragMove();};
   var stream=typeof(Program).Assembly.GetManifestResourceStream("AppIcon.png");
   if(stream!=null)using(stream) {
    var logo=new BitmapImage();logo.BeginInit();logo.CacheOption=BitmapCacheOption.OnLoad;logo.StreamSource=stream;logo.EndInit();logo.Freeze();
    Window.Icon=logo;((Image)Window.FindName("DialogLogo")).Source=logo;
   }
  }
  internal bool Show(){return Window.ShowDialog()==true;}
  internal static bool Confirm(IntPtr owner,string title,string message,string action) {
   return new NexusDialog(owner,title,message,action,DialogTone.Warning).Show();
  }
  internal static void Notice(IntPtr owner,string title,string message,DialogTone tone) {
   new NexusDialog(owner,title,message,null,tone).Show();
  }
 }
}
