using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;

namespace WindWaker.Installer;

/// <summary>
/// Modo --watch: roda sem janela, percebe quando o Wind Waker HD é aberto e, se houver uma tradução mais nova,
/// pergunta se o usuário quer atualizar. A instalação começa sozinha assim que o jogo é fechado.
/// </summary>
public static class Watcher {
 static readonly string[] ProcessNames={"wwhd","Wind Waker HD"};

 static List<(int Pid,GameLocation Game)> RunningGames() {
  var list=new List<(int,GameLocation)>();
  foreach(var name in ProcessNames)foreach(var p in Process.GetProcessesByName(name)) {
   using(p) {
    try{var path=p.MainModule?.FileName;var game=path==null?null:InstallerEngine.Resolve(path);if(game!=null)list.Add((p.Id,game));}
    catch(Exception e)when(e is System.ComponentModel.Win32Exception or InvalidOperationException){}
   }
  }
  return list;
 }
 static bool IsRunning(GameLocation game)=>RunningGames().Any(r=>r.Game.Root==game.Root);

 static MessageBoxResult Ask(string text,string title,MessageBoxButton buttons,MessageBoxImage icon) {
  // Janela invisível sempre-no-topo como dona da caixa, para ela aparecer na frente do jogo.
  var owner=new Window{Width=0,Height=0,WindowStyle=WindowStyle.None,ShowInTaskbar=false,Topmost=true,ShowActivated=true,Left=-10000,Top=-10000};
  owner.Show();
  try{return MessageBox.Show(owner,text,title,buttons,icon,MessageBoxResult.Yes);}finally{owner.Close();}
 }

 public static async Task RunAsync() {
  using var mutex=new Mutex(true,@"Local\WindWakerPTBR-Watcher",out var first);
  if(!first){Application.Current.Shutdown();return;}
  using var http=Updater.CreateClient();
  var seen=new HashSet<int>();
  while(Autostart.IsEnabled()) {
   try {
    var running=RunningGames();
    seen.IntersectWith(running.Select(r=>r.Pid));
    foreach(var (pid,game) in running)if(seen.Add(pid))await OnGameStarted(game,http);
   }catch(Exception e)when(e is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException){}
   await Task.Delay(3000);
  }
  Application.Current.Shutdown();
 }

 static async Task OnGameStarted(GameLocation game,HttpClient http) {
  var installed=Updater.InstalledVersion(game);
  if(installed==null)return; // tradução não instalada (ou restaurada ao original): não incomoda
  UpdateInfo? latest;
  using(var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20)))latest=await Updater.FetchLatestAsync(http,ct:cts.Token);
  if(latest==null||latest.Version<=installed)return;
  var answer=Ask($"A tradução foi atualizada!\n\nGostaria de atualizar?\n\nVersão instalada: {installed.ToString(3)}  →  nova versão: {latest.Version.ToString(3)}\n\nO jogo precisa estar fechado para concluir. Se disser Sim, a atualização é baixada agora e instalada automaticamente assim que você fechar o jogo.","Wind Waker HD • Atualização da tradução",MessageBoxButton.YesNo,MessageBoxImage.Question);
  if(answer!=MessageBoxResult.Yes)return;
  try {
   byte[] payload;
   using(var cts=new CancellationTokenSource(TimeSpan.FromMinutes(10)))payload=await Updater.DownloadPayloadAsync(http,latest,null,cts.Token);
   while(IsRunning(game))await Task.Delay(2000);
   var engine=new InstallerEngine(payload,latest.PackSha256,App.OriginalHash);
   await Task.Run(()=>engine.Install(game,true,null));
   Ask($"Tradução atualizada para a versão {latest.Version.ToString(3)}!\n\nAbra o jogo novamente para ver as mudanças.","Wind Waker HD • Atualização concluída",MessageBoxButton.OK,MessageBoxImage.Information);
  }catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) {
   Ask("Não foi possível atualizar a tradução agora.\n\n"+ex.Message+"\n\nAbra o instalador e use Verificar atualizações para tentar de novo.","Wind Waker HD • Atualização da tradução",MessageBoxButton.OK,MessageBoxImage.Warning);
  }
 }
}
