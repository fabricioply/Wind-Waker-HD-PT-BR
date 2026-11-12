using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WindWaker.Installer;

/// <summary>Liga/desliga o vigia que avisa sobre atualizações quando o jogo abre (inicia com o Windows, só no usuário atual).</summary>
public static class Autostart {
 const string RunKey=@"Software\Microsoft\Windows\CurrentVersion\Run",ValueName="WindWakerPTBR-Updater";
 static string Base=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WindWakerPTBR");
 public static string WatcherExe=>Path.Combine(Base,"Updater","WindWakerHD-Traducao-PTBR.exe");
 static string OptOutFile=>Path.Combine(Base,"watch-optout");

 public static bool IsEnabled(){try{using var k=Registry.CurrentUser.OpenSubKey(RunKey);return k?.GetValue(ValueName) is string;}catch(Exception e)when(e is System.Security.SecurityException or IOException or UnauthorizedAccessException){return false;}}
 /// <summary>Preferência do usuário: ligado, a menos que ele tenha desmarcado antes.</summary>
 public static bool Preferred()=>!File.Exists(OptOutFile);

 public static void Enable() {
  var source=Environment.ProcessPath;
  if(source==null)throw new IOException("Não foi possível localizar o executável do instalador.");
  var target=WatcherExe;Directory.CreateDirectory(Path.GetDirectoryName(target)!);
  if(!string.Equals(Path.GetFullPath(source),Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase)) {
   try{var s=new FileInfo(source);var t=new FileInfo(target);if(!t.Exists||t.Length!=s.Length||t.LastWriteTimeUtc!=s.LastWriteTimeUtc)File.Copy(source,target,true);}
   catch(IOException)when(File.Exists(target)){} // vigia já em execução: mantém a cópia atual
  }
  using(var k=Registry.CurrentUser.CreateSubKey(RunKey))k.SetValue(ValueName,"\""+target+"\" --watch");
  try{File.Delete(OptOutFile);}catch(IOException){}
  Process.Start(new ProcessStartInfo(target,"--watch"){UseShellExecute=false,CreateNoWindow=true}); // se já houver um vigia, o novo encerra sozinho
 }
 public static void Disable() {
  using(var k=Registry.CurrentUser.OpenSubKey(RunKey,true))k?.DeleteValue(ValueName,false);
  Directory.CreateDirectory(Base);File.WriteAllText(OptOutFile,"O usuário desativou os avisos de atualização.");
 }
}
