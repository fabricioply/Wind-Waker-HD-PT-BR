using Microsoft.Win32;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WindWaker.Installer;
public partial class MainWindow:Window {
 readonly bool preview;
 readonly MediaPlayer player=new();
 readonly DispatcherTimer pathTimer=new(){Interval=TimeSpan.FromMilliseconds(400)};
 GameLocation? game;
 bool busy,playing=true,muted,suppressPath;
 string? lastBackup;
 static readonly HttpClient http=Updater.CreateClient();
 bool checkingUpdate;
 readonly List<string> messages=[];
 public MainWindow(bool preview=false) {
  this.preview=preview;InitializeComponent();ShowVersion();WatchCheck.IsChecked=Autostart.Preferred();
  pathTimer.Tick+=(s,e)=>{pathTimer.Stop();ValidatePath();};
  Loaded+=async(s,e)=>{await DetectGame(true);if(!preview){StartMusic();await CheckForUpdates(false);}};
  Closing+=OnClosing;
  player.MediaEnded+=(s,e)=>{if(playing){player.Position=TimeSpan.Zero;player.Play();}};
  player.MediaOpened+=(s,e)=>{MusicStatus.Text="Trilha do instalador • volume ajustável";};
  player.MediaFailed+=(s,e)=>{playing=false;MusicButton.IsEnabled=false;MusicStatus.Text="Áudio indisponível neste Windows";Log("Áudio: "+e.ErrorException.Message);};
 }
 public static string ExtractMusic() {
  var bytes=App.ReadAsset("DRI.mp3");var hash=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
  var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WindWakerPTBR","cache",hash[..16]);
  Directory.CreateDirectory(folder);var path=Path.Combine(folder,"Dragon-Roost-Island.mp3");
  if(!File.Exists(path)||InstallerEngine.HashFile(path)!=hash){var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";File.WriteAllBytes(temp,bytes);File.Move(temp,path,true);}
  return path;
 }
 void StartMusic(){try{player.Volume=Volume.Value;player.Open(new Uri(ExtractMusic()));player.Play();}catch(Exception e){playing=false;MusicButton.IsEnabled=false;MusicStatus.Text="Não foi possível iniciar o áudio";Log(e.Message);}}
 static List<string> Candidates() {
  var p=new List<string>{AppContext.BaseDirectory,Environment.CurrentDirectory};
  var cursor=AppContext.BaseDirectory;for(int n=0;n<4;n++){var parent=Directory.GetParent(cursor);if(parent==null)break;p.Add(parent.FullName);cursor=parent.FullName;}
  foreach(var d in DriveInfo.GetDrives()) {
   try{if(!d.IsReady)continue;foreach(var name in new[]{"Recomp Library\\zeldawwhdrecomp","Games\\Wind Waker HD","Games\\zeldawwhdrecomp","WWHD","Wind Waker HD"})p.Add(Path.Combine(d.RootDirectory.FullName,name));}catch(IOException){}
  }
  foreach(var name in new[]{"WWHD","WindWakerHD","ZeldaWWHDRecomp"})p.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),name));
  var docs=Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);p.Add(Path.Combine(docs,"Recomp Library","zeldawwhdrecomp"));
  try {
   foreach(var hive in new[]{Registry.CurrentUser,Registry.LocalMachine})using(var uninstall=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall")) {
    if(uninstall==null)continue;foreach(var keyName in uninstall.GetSubKeyNames()){using var key=uninstall.OpenSubKey(keyName);if((key?.GetValue("DisplayName")as string)?.Contains("Wind Waker",StringComparison.OrdinalIgnoreCase)==true&&key?.GetValue("InstallLocation")is string location)p.Add(location);}
   }
  }catch(System.Security.SecurityException){}
  return p;
 }
 async Task DetectGame(bool first=false) {
  DetectButton.IsEnabled=false;OperationText.Text="Procurando nas bibliotecas do Recomp e pastas conhecidas…";
  try {
   var library=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"com.recomplauncher.app","library.json");
   var paths=await Task.Run(()=>InstallerEngine.Detect(Candidates(),library).ToList());
   if(paths.Count>0){SetPath(paths[0].Root);Log("Instalação detectada: "+paths[0].Root);OperationText.Text=paths.Count==1?"Jogo encontrado. Escolha instalar ou restaurar.":$"Encontradas {paths.Count} instalações. Use Escolher pasta para selecionar outra.";}
   else {SetPath("");OperationText.Text="Não encontramos o jogo automaticamente. Selecione a pasta do Recomp.";if(first&&!preview)ChooseFolder();}
  }catch(Exception ex){Log(ex.Message);OperationText.Text="Selecione a pasta do jogo manualmente.";}
  finally{DetectButton.IsEnabled=!busy;}
 }
 void SetPath(string path){suppressPath=true;PathBox.Text=path;suppressPath=false;ValidatePath();}
 void ValidatePath() {
  if(!IsInitialized||busy)return;
  game=InstallerEngine.Resolve(PathBox.Text);
  var valid=game!=null;
  InstallButton.IsEnabled=valid;RestoreButton.IsEnabled=valid;
  BackupButton.IsEnabled=valid&&Directory.Exists(game!.Backup);
  StatusMark.Text=valid?"✓":"◇";
  StatusTitle.Text=valid?"Wind Waker HD Recomp encontrado":"Selecione a pasta do Wind Waker HD Recomp";
  StatusDetail.Text=valid?"Pacote USA localizado. A tradução será aplicada pelo gerenciador de mods.":"Escolha a pasta principal, a pasta data ou o executável do Recomp.";
  StatusCard.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(valid?"#E7EFE3":"#F3E8D0"));
  if(valid)lastBackup=game!.Backup;
 }
 void ChooseFolder() {
  var picker=new OpenFolderDialog{Title="Selecione a pasta do Wind Waker HD Recomp",Multiselect=false};
  if(game!=null)picker.InitialDirectory=game.Root;
  if(picker.ShowDialog(this)==true)SetPath(picker.FolderName);
 }
 void Log(string message) {
  if(!Dispatcher.CheckAccess()){Dispatcher.Invoke(()=>Log(message));return;}
  messages.Add(DateTime.Now.ToString("HH:mm:ss")+"  "+message);
  LogBox.Text=string.Join(Environment.NewLine,messages.TakeLast(80));LogBox.ScrollToEnd();
 }
 void SetBusy(bool value) {
  busy=value;InstallButton.IsEnabled=!value&&game!=null;RestoreButton.IsEnabled=!value&&game!=null;
  BrowseButton.IsEnabled=!value;DetectButton.IsEnabled=!value;PathBox.IsReadOnly=value;Progress.IsIndeterminate=value;
 }
 async Task Perform(bool restore) {
  pathTimer.Stop();ValidatePath();var selected=game;if(selected==null){ChooseFolder();return;}
  if(restore&&MessageBox.Show(this,"A tradução será desativada e guardada no backup. Seus saves e outros mods serão mantidos.\n\nRestaurar o idioma original?","Restaurar original",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
  SetBusy(true);Progress.Value=0;
  try {
   var report=new Action<string>(text=>Dispatcher.Invoke(()=>{OperationText.Text=text;Log(text);}));
   var engine=App.Engine();
   var backup=await Task.Run(()=>restore?engine.Restore(selected,true,report):engine.Install(selected,true,report));
   lastBackup=selected.Backup;Progress.IsIndeterminate=false;Progress.Value=100;
   StatusTitle.Text=restore?"Idioma original restaurado":"Tudo pronto para jogar em português!";
   StatusDetail.Text=restore?"A tradução está desativada. O backup permanece disponível.":"Reabra o jogo e mantenha Inglês selecionado no menu de idioma do Recomp.";
   Log("Cópia de segurança: "+backup);BackupButton.IsEnabled=true;ApplyWatchPreference();
  }catch(Exception ex){Progress.IsIndeterminate=false;Progress.Value=0;OperationText.Text="Não foi possível concluir. Veja os detalhes abaixo.";StatusTitle.Text="A operação não foi concluída";StatusDetail.Text=ex.Message;Log("ERRO: "+ex.Message);MessageBox.Show(this,ex.Message,"Wind Waker HD • Tradução PT-BR",MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{SetBusy(false);}
 }
 void ApplyWatchPreference(){try{if(WatchCheck.IsChecked==true)Autostart.Enable();else Autostart.Disable();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException){Log("Avisos de atualização: "+ex.Message);}}
 void Watch_Click(object s,RoutedEventArgs e)=>ApplyWatchPreference();
 void ShowVersion(){var v=Updater.PayloadVersion(App.ReadAsset("payload.zip"));VersionText.Text=$"Pacote PT-BR {(v==null?"":v.ToString(3))} • Recomp USA • Projeto de fãs";}
 async Task CheckForUpdates(bool manual) {
  if(busy||checkingUpdate)return;
  pathTimer.Stop();ValidatePath();var selected=game;
  if(selected==null){if(manual)MessageBox.Show(this,"Selecione primeiro a pasta do Wind Waker HD Recomp.","Wind Waker HD • Tradução PT-BR",MessageBoxButton.OK,MessageBoxImage.Information);return;}
  var installed=Updater.InstalledVersion(selected);
  if(!manual&&installed==null)return; // verificação automática só para quem já instalou a tradução
  checkingUpdate=true;UpdateButton.IsEnabled=false;
  try {
   if(manual)OperationText.Text="Procurando atualizações da tradução…";
   var embedded=Updater.PayloadVersion(App.ReadAsset("payload.zip"));
   UpdateInfo? latest;
   using(var cts=new CancellationTokenSource(TimeSpan.FromSeconds(20)))latest=await Updater.FetchLatestAsync(http,ct:cts.Token);
   if(latest==null){Log("Atualização: a release mais recente não traz um pacote de atualização válido.");if(manual)OperationText.Text="Nenhuma atualização disponível no momento.";return;}
   var current=installed??embedded;
   if(current!=null&&latest.Version<=current) {
    Log($"Tradução em dia (versão {current.ToString(3)}).");
    if(manual){
     OperationText.Text=installed==null?$"A versão mais recente ({latest.Version.ToString(3)}) já acompanha este instalador. Clique em Instalar tradução.":$"Você já tem a versão mais recente ({current.ToString(3)}).";
     MessageBox.Show(this,OperationText.Text,"Wind Waker HD • Tradução PT-BR",MessageBoxButton.OK,MessageBoxImage.Information);
    }
    return;
   }
   Log($"Atualização encontrada: {latest.Version.ToString(3)} (atual: {(installed==null?"não instalada":installed.ToString(3))}).");
   var detail=installed==null?$"Versão mais recente: {latest.Version.ToString(3)}":$"Versão instalada: {installed.ToString(3)}  →  nova versão: {latest.Version.ToString(3)}";
   var answer=MessageBox.Show(this,"A tradução foi atualizada!\n\nGostaria de atualizar?\n\n"+detail,"Atualização da tradução",MessageBoxButton.YesNo,MessageBoxImage.Question);
   if(answer==MessageBoxResult.Yes)await ApplyUpdate(selected,latest,embedded);
   else OperationText.Text="Atualização adiada. Você pode instalá-la depois em Verificar atualizações.";
  }catch(Exception ex)when(ex is HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException or InvalidOperationException) {
   Log("Atualização: "+ex.Message);
   if(manual){OperationText.Text="Não foi possível verificar atualizações. Confira sua conexão com a internet.";MessageBox.Show(this,"Não foi possível verificar atualizações.\n\nConfira sua conexão com a internet e tente novamente.","Wind Waker HD • Tradução PT-BR",MessageBoxButton.OK,MessageBoxImage.Warning);}
  }
  finally{checkingUpdate=false;UpdateButton.IsEnabled=true;}
 }
 async Task ApplyUpdate(GameLocation selected,UpdateInfo info,Version? embedded) {
  SetBusy(true);Progress.Value=0;
  try {
   var report=new Action<string>(text=>Dispatcher.Invoke(()=>{OperationText.Text=text;Log(text);}));
   InstallerEngine engine;
   if(embedded==info.Version&&App.TranslationHash.Equals(info.PackSha256,StringComparison.OrdinalIgnoreCase)){report("Esta versão já acompanha o instalador; não é preciso baixar nada.");engine=App.Engine();}
   else {
    Progress.IsIndeterminate=false;report("Baixando a atualização…");
    byte[] payload;
    using(var cts=new CancellationTokenSource(TimeSpan.FromMinutes(10)))
     payload=await Updater.DownloadPayloadAsync(http,info,new Progress<double>(p=>{Progress.Value=p*100;OperationText.Text=$"Baixando a atualização… {p:P0}";}),cts.Token);
    report("Download conferido (SHA-256 e versão).");engine=new InstallerEngine(payload,info.PackSha256,App.OriginalHash);Progress.IsIndeterminate=true;
   }
   var backup=await Task.Run(()=>engine.Install(selected,true,report));
   lastBackup=selected.Backup;Progress.IsIndeterminate=false;Progress.Value=100;
   StatusTitle.Text="Tradução atualizada para a versão "+info.Version.ToString(3);
   StatusDetail.Text="Reabra o jogo e mantenha Inglês selecionado no menu de idioma do Recomp.";
   Log("Cópia de segurança: "+backup);BackupButton.IsEnabled=true;ApplyWatchPreference();
   MessageBox.Show(this,"Tradução atualizada para a versão "+info.Version.ToString(3)+"!\n\nSe o jogo estiver aberto, feche-o e abra de novo para ver as mudanças.","Atualização concluída",MessageBoxButton.OK,MessageBoxImage.Information);
  }catch(Exception ex){Progress.IsIndeterminate=false;Progress.Value=0;OperationText.Text="Não foi possível atualizar. Veja os detalhes abaixo.";StatusTitle.Text="A atualização não foi concluída";StatusDetail.Text=ex.Message;Log("ERRO: "+ex.Message);MessageBox.Show(this,ex.Message,"Wind Waker HD • Tradução PT-BR",MessageBoxButton.OK,MessageBoxImage.Warning);}
  finally{SetBusy(false);}
 }
 void OnClosing(object? sender,CancelEventArgs e){if(busy){e.Cancel=true;OperationText.Text="Aguarde a operação terminar antes de fechar o instalador.";return;}pathTimer.Stop();player.Stop();player.Close();}
 void Path_Changed(object s,TextChangedEventArgs e){if(suppressPath||!IsInitialized)return;pathTimer.Stop();pathTimer.Start();}
 async void Detect_Click(object s,RoutedEventArgs e)=>await DetectGame();
 void Browse_Click(object s,RoutedEventArgs e)=>ChooseFolder();
 async void Install_Click(object s,RoutedEventArgs e)=>await Perform(false);
 async void Restore_Click(object s,RoutedEventArgs e)=>await Perform(true);
 void Backup_Click(object s,RoutedEventArgs e){if(lastBackup!=null&&Directory.Exists(lastBackup))Process.Start(new ProcessStartInfo(lastBackup){UseShellExecute=true});}
 async void Update_Click(object s,RoutedEventArgs e)=>await CheckForUpdates(true);
 void Music_Click(object s,RoutedEventArgs e){playing=!playing;if(playing)player.Play();else player.Pause();MusicButton.Content=playing?"Pausar":"Reproduzir";}
 void Mute_Click(object s,RoutedEventArgs e){muted=!muted;player.IsMuted=muted;MuteButton.Content=muted?"Ativar som":"Silenciar";}
 void Volume_Changed(object s,RoutedPropertyChangedEventArgs<double> e){player.Volume=e.NewValue;}
 void Minimize_Click(object s,RoutedEventArgs e)=>WindowState=WindowState.Minimized;
 void Close_Click(object s,RoutedEventArgs e)=>Close();
 public void ConfigurePreview() {
  var library=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"com.recomplauncher.app","library.json");
  var detected=InstallerEngine.Detect(Candidates(),library).FirstOrDefault();
  SetPath(detected?.Root??@"C:\Jogos\Wind Waker HD Recomp");
  OperationText.Text="Pronto para navegar. Escolha uma das opções acima.";MusicStatus.Text="Trilha do instalador • volume ajustável";
 }
 public object PreviewState()=>new {detected_root=game?.Root,install_enabled=InstallButton.IsEnabled,restore_enabled=RestoreButton.IsEnabled,embedded_music_bytes=App.ReadAsset("DRI.mp3").Length,embedded_payload_bytes=App.ReadAsset("payload.zip").Length};
}
