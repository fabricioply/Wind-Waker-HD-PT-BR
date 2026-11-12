using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WindWaker.Installer;
public partial class App:Application {
 public const string TranslationHash="eccaa658b3bdca472a403f579cce2c777f9c1fb3fdb0302a4f6f96f42521a906";
 public const string OriginalHash="fa48e23be34d6c7141a5ebee951afc4bb722ce1e2ecdf515562d4ba0d2a05a7f";
 public static byte[] ReadAsset(string name){using var s=Assembly.GetExecutingAssembly().GetManifestResourceStream(name)??throw new IOException("Recurso ausente: "+name);using var m=new MemoryStream();s.CopyTo(m);return m.ToArray();}
 public static InstallerEngine Engine()=>new(ReadAsset("payload.zip"),TranslationHash,OriginalHash);
 protected override async void OnStartup(StartupEventArgs e) {
  base.OnStartup(e);
  DispatcherUnhandledException+=(s,a)=>{MessageBox.Show(a.Exception.Message,"Wind Waker HD • Tradução PT-BR",MessageBoxButton.OK,MessageBoxImage.Error);a.Handled=true;};
  try {
   if(e.Args.Contains("--watch")){ShutdownMode=ShutdownMode.OnExplicitShutdown;await Watcher.RunAsync();return;}
   if(e.Args.Length>=2&&e.Args[0]=="--verify-audio") {await VerifyAudio(e.Args[1]);Shutdown();return;}
   var preview=e.Args.Length>=2&&e.Args[0]=="--preview";
   var w=new MainWindow(preview);MainWindow=w;
   if(preview) {
    w.ConfigurePreview();
    var root=(FrameworkElement)w.Content;
    root.Measure(new Size(1000,790));root.Arrange(new Rect(0,0,1000,790));root.UpdateLayout();
    await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Render);
    var bitmap=new RenderTargetBitmap(1000,790,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
    var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
    var target=Path.GetFullPath(e.Args[1]);Directory.CreateDirectory(Path.GetDirectoryName(target)!);using(var f=File.Create(target))encoder.Save(f);
    File.WriteAllText(target+".json",JsonSerializer.Serialize(w.PreviewState(),new JsonSerializerOptions{WriteIndented=true}));
    w.Close();Shutdown();return;
   }
   w.Show();
  }catch(Exception ex){if(e.Args.Length>0)File.WriteAllText(Path.Combine(Path.GetTempPath(),"wwhd-installer-error.txt"),ex.ToString());else MessageBox.Show(ex.Message,"Não foi possível abrir o instalador",MessageBoxButton.OK,MessageBoxImage.Error);Shutdown(1);}
 }
 static async Task VerifyAudio(string target) {
  var audio=new MediaPlayer{Volume=0};var done=new TaskCompletionSource<double>();
  audio.MediaOpened+=(s,e)=>done.TrySetResult(audio.NaturalDuration.HasTimeSpan?audio.NaturalDuration.TimeSpan.TotalSeconds:0);
  audio.MediaFailed+=(s,e)=>done.TrySetException(e.ErrorException);
  audio.Open(new Uri(WindWaker.Installer.MainWindow.ExtractMusic()));audio.Play();
  var result=await Task.WhenAny(done.Task,Task.Delay(15000));
  if(result!=done.Task)throw new IOException("A reprodução de áudio não respondeu a tempo.");
  var seconds=await done.Task;audio.Stop();audio.Close();
  if(seconds<=0)throw new IOException("O áudio embutido não foi reconhecido.");
  File.WriteAllText(target,JsonSerializer.Serialize(new {decoded=true,duration_seconds=seconds,sha256=Convert.ToHexString(SHA256.HashData(ReadAsset("DRI.mp3"))).ToLowerInvariant()}));
 }
}
