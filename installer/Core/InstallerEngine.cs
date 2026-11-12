using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WindWaker.Installer;
public record GameLocation(string Root, string DataRoot, string? ConfiguredGameRoot = null) {
    public string Pack => Path.Combine(ConfiguredGameRoot ?? Path.Combine(DataRoot,"game"),"content","Common","Pack","permanent_2d_UsEnglish.pack");
    public string Manager => Path.Combine(DataRoot,"user","ModManager");
    public string Mod => Path.Combine(Manager,"Mods","wind-waker-ptbr");
    public string Profiles => Path.Combine(Manager,"profiles.json");
    public string Backup => Path.Combine(Manager,"Backups","wind-waker-ptbr");
}
public class InstallerEngine {
    readonly byte[] payload;
    readonly string packHash,sourceHash;
    const string PackEntry="content/Common/Pack/permanent_2d_UsEnglish.pack";
    const string ModId="wind-waker-ptbr";
    static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    public InstallerEngine(byte[] payload, string packHash, string sourceHash) {
        this.payload=payload;this.packHash=packHash;this.sourceHash=sourceHash;
    }
    public static string HashFile(string path) {using var s=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();}
    static string Full(string p)=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    public static GameLocation? Resolve(string input) {
        if(string.IsNullOrWhiteSpace(input))return null;
        try {
            string? p=Full(input.Trim().Trim('"'));
            if(File.Exists(p))p=Path.GetDirectoryName(p);
            for(int n=0;p!=null&&n<6;n++,p=Path.GetDirectoryName(p)) {
                if(Path.GetFileName(p).Equals("data",StringComparison.OrdinalIgnoreCase)&&File.Exists(Path.Combine(p,"install.json"))&&Path.GetDirectoryName(p) is string parent) {p=parent;}
                foreach(var data in new[]{Path.Combine(p,"data"),p}) {
                    var statePath=Path.Combine(data,"install.json");
                    string? configured=null;
                    if(File.Exists(statePath)) {
                        try {
                            var state=JsonNode.Parse(File.ReadAllText(statePath))?.AsObject();
                            var value=state?["game_dir"]?.GetValue<string>();
                            if(!string.IsNullOrWhiteSpace(value))configured=Full(Path.Combine(data,value));
                        }catch(Exception e)when(e is JsonException or InvalidOperationException or IOException or ArgumentException){continue;}
                    }
                    var loc=new GameLocation(p,data,configured);
                    var executable=File.Exists(Path.Combine(p,"Wind Waker HD.exe"))||File.Exists(Path.Combine(p,"wwhd.exe"))||File.Exists(Path.Combine(data,"bin","wwhd.exe"));
                    if(File.Exists(loc.Pack)&&(executable||File.Exists(statePath)))return loc;
                }
            }
        }catch(Exception e)when(e is IOException or ArgumentException or UnauthorizedAccessException){}
        return null;
    }
    public static IEnumerable<GameLocation> Detect(IEnumerable<string> candidates,string? libraryJson=null) {
        var all=new List<string>(candidates);
        if(libraryJson!=null&&File.Exists(libraryJson)) {
            try {
                var installed=JsonNode.Parse(File.ReadAllText(libraryJson))?["installed"]?.AsObject();
                if(installed!=null)foreach(var (_,v)in installed) {
                    var name=v?["name"]?.GetValue<string>()??"";
                    var id=v?["id"]?.GetValue<string>()??"";
                    if(name.Contains("Wind Waker",StringComparison.OrdinalIgnoreCase)||id.Contains("wwhd",StringComparison.OrdinalIgnoreCase)||v?["repo"]?.GetValue<string>().Contains("ZeldaWWHD",StringComparison.OrdinalIgnoreCase)==true||installed.ContainsKey("zeldawwhdrecomp")&&ReferenceEquals(v,installed["zeldawwhdrecomp"])) {
                        var p=v?["path"]?.GetValue<string>();if(p!=null)all.Add(p);
                    }
                }
            }catch(Exception e)when(e is IOException or JsonException or InvalidOperationException){}
        }
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var c in all){var game=Resolve(c);if(game!=null&&seen.Add(game.Root))yield return game;}
    }
    public static void AssertClosed(GameLocation game) {
        foreach(var name in new[]{"wwhd","Wind Waker HD"})foreach(var p in Process.GetProcessesByName(name)) {
            using(p) {
                try {
                    var path=p.MainModule?.FileName;
                    if(path==null||Full(path).StartsWith(game.Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Feche o Wind Waker HD antes de instalar ou restaurar a tradução.");
                }catch(System.ComponentModel.Win32Exception){throw new InvalidOperationException("Feche as instâncias do Wind Waker HD antes de continuar.");}
            }
        }
    }
    static void SafePath(string path,string root) {
        path=Full(path);root=Full(root);
        if(path!=root&&!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Caminho fora da instalação selecionada.");
        string? p=path;
        while(p!=null) {
            if((File.Exists(p)||Directory.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Não é seguro modificar uma instalação com links ou junções neste caminho: "+p);
            if(p.Equals(root,StringComparison.OrdinalIgnoreCase))break;
            p=Path.GetDirectoryName(p);
        }
    }
    static void SafeTree(string path,string root) {
        SafePath(path,root);
        if(!Directory.Exists(path))return;
        foreach(var item in Directory.EnumerateFileSystemEntries(path)) {
            SafePath(item,root);
            if(Directory.Exists(item))SafeTree(item,root);
        }
    }
    static JsonObject ReadProfiles(string path) {
        if(Directory.Exists(path))throw new IOException("O caminho profiles.json é uma pasta. Corrija a instalação antes de continuar.");
        if(!File.Exists(path))return new JsonObject{["format_version"]=1,["active"]="Default",["profiles"]=new JsonObject{["Default"]=new JsonObject{["enabled"]=new JsonObject()}}};
        try {
            var j=JsonNode.Parse(File.ReadAllText(path))?.AsObject()??throw new JsonException();
            if(j["profiles"]is not JsonObject profileMap)throw new JsonException();
            foreach(var(_,profile)in profileMap) {
                if(profile is not JsonObject p)throw new JsonException();
                if(p["enabled"]is not null&&p["enabled"]is not JsonObject)throw new JsonException();
                if(p["enabled"]is JsonObject enabled)foreach(var(_,state)in enabled)
                    if(state is not JsonValue v||!v.TryGetValue<bool>(out _))throw new JsonException();
            }
            return j;
        }catch(Exception e)when(e is JsonException or InvalidOperationException) {throw new IOException("O perfil de mods está inválido. Ele foi preservado; nenhum arquivo foi substituído.",e);}
    }
    static void Enable(JsonObject j) {
        var name=j["active"]?.GetValue<string>();if(string.IsNullOrWhiteSpace(name)){name="Default";j["active"]=name;}
        var profiles=j["profiles"]!.AsObject();
        if(profiles[name]is not JsonObject)profiles[name]=new JsonObject();
        var p=profiles[name]!.AsObject();if(p["enabled"]is not JsonObject)p["enabled"]=new JsonObject();p["enabled"]![ModId]=true;
    }
    static void Disable(JsonObject j) {
        foreach(var(_,profile)in j["profiles"]!.AsObject())
            if(profile?["enabled"]is JsonObject enabled&&enabled.ContainsKey(ModId))enabled[ModId]=false;
    }
    static void AtomicWrite(string path,byte[] bytes) {
        var temp=path+".tmp-"+Guid.NewGuid().ToString("N");
        File.WriteAllBytes(temp,bytes);
        try{File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
    }
    void ValidatePayload() {
        using var z=new ZipArchive(new MemoryStream(payload),ZipArchiveMode.Read);
        if(z.Entries.Count!=2)throw new IOException("O pacote da tradução contém arquivos inesperados.");
        foreach(var e in z.Entries)if(e.FullName is not "manifest.json"&&e.FullName!=PackEntry)throw new IOException("Caminho não permitido no pacote da tradução.");
        var pack=z.GetEntry(PackEntry)??throw new IOException("Tradução incompleta.");
        using var stream=pack.Open();
        if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(packHash,StringComparison.OrdinalIgnoreCase))throw new IOException("A tradução embutida não passou na verificação de integridade.");
        using var manifest=z.GetEntry("manifest.json")!.Open();
        if(JsonNode.Parse(manifest)?["id"]?.GetValue<string>()!=ModId)throw new IOException("O identificador do pacote é inválido.");
    }
    void Preflight(GameLocation game,bool checkRunning,bool installing) {
        if(Resolve(game.Root)!=game)throw new IOException("Selecione uma instalação válida do Wind Waker HD Recomp (USA).");
        if(checkRunning)AssertClosed(game);
        SafePath(game.Manager,game.Root);SafeTree(game.Mod,game.Root);SafePath(game.Backup,game.Root);SafePath(game.Profiles,game.Root);SafePath(game.Pack,game.ConfiguredGameRoot ?? game.Root);
        if(installing&&HashFile(game.Pack)!=sourceHash)throw new IOException("O pacote original deste jogo não corresponde à versão USA compatível. Use o Recomp com os arquivos originais; a instalação foi cancelada sem substituir o jogo.");
    }
    void BackupOriginal(GameLocation game) {
        var original=Path.Combine(game.Backup,"Original");SafePath(original,game.Root);
        if(Directory.Exists(original)) {
            if(!File.Exists(Path.Combine(original,"permanent_2d_UsEnglish.pack"))||HashFile(Path.Combine(original,"permanent_2d_UsEnglish.pack"))!=sourceHash)throw new IOException("A cópia de segurança original está incompleta ou foi alterada. Preserve essa pasta e verifique-a antes de continuar.");
            return;
        }
        var staging=original+"-preparing-"+Guid.NewGuid().ToString("N");SafePath(staging,game.Root);Directory.CreateDirectory(staging);
        File.Copy(game.Pack,Path.Combine(staging,"permanent_2d_UsEnglish.pack"));
        if(File.Exists(game.Profiles))File.Copy(game.Profiles,Path.Combine(staging,"profiles.json"));
        File.WriteAllText(Path.Combine(staging,"README.txt"),"Backup inicial. Os arquivos originais do jogo e saves não são modificados pelo instalador. As restaurações desativam somente a tradução, preservando mods adicionados depois.",Encoding.UTF8);
        Directory.Move(staging,original);
    }
    static FileStream Acquire(GameLocation game) {
        Directory.CreateDirectory(game.Manager);
        try{return new FileStream(Path.Combine(game.Manager,".ptbr-installer.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
        catch(IOException){throw new IOException("Outro instalador está usando esta pasta. Feche-o e tente novamente.");}
    }
    static string Operation(GameLocation game,string kind) {
        var op=Path.Combine(game.Backup,DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+"-"+kind+"-"+Guid.NewGuid().ToString("N")[..6]);
        SafePath(op,game.Root);Directory.CreateDirectory(op);
        if(File.Exists(game.Profiles))File.Copy(game.Profiles,Path.Combine(op,"profiles-before.json"));
        return op;
    }
    public string Install(GameLocation game,bool checkRunning=true,Action<string>? progress=null) {
        progress?.Invoke("Conferindo instalação e integridade da tradução…");
        Preflight(game,checkRunning,true);ValidatePayload();
        using var guard=Acquire(game);
        var profiles=ReadProfiles(game.Profiles);Enable(profiles);
        var oldProfile=File.Exists(game.Profiles)?File.ReadAllBytes(game.Profiles):null;
        progress?.Invoke("Preservando a cópia de segurança original…");BackupOriginal(game);
        var op=Operation(game,"instalacao");var previous=Path.Combine(op,"previous-mod");
        var staging=Path.Combine(game.Manager,"Mods",".ptbr-staging-"+Guid.NewGuid().ToString("N"));SafePath(staging,game.Root);Directory.CreateDirectory(staging);
        bool movedOld=false,movedNew=false,profileCommitted=false;
        try {
            progress?.Invoke("Preparando os arquivos em uma pasta temporária…");
            using(var zip=new ZipArchive(new MemoryStream(payload)))foreach(var entry in zip.Entries) {
                var target=Path.GetFullPath(Path.Combine(staging,entry.FullName.Replace('/',Path.DirectorySeparatorChar)));SafePath(target,staging);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);using var src=entry.Open();using var dst=File.Create(target);src.CopyTo(dst);
            }
            if(HashFile(Path.Combine(staging,PackEntry))!=packHash)throw new IOException("A verificação dos arquivos gravados falhou.");
            progress?.Invoke("Instalando e ativando no perfil de mods…");
            if(Directory.Exists(game.Mod)){Directory.Move(game.Mod,previous);movedOld=true;}
            Directory.Move(staging,game.Mod);movedNew=true;
            AtomicWrite(game.Profiles,Encoding.UTF8.GetBytes(profiles.ToJsonString(JsonOptions)));
            profileCommitted=true;
            File.WriteAllText(Path.Combine(op,"result.json"),new JsonObject{["action"]="install",["pack_sha256"]=packHash,["completed_at"]=DateTimeOffset.Now.ToString("O")}.ToJsonString(JsonOptions));
            progress?.Invoke("Tradução instalada. Backup preservado e saves intactos.");return op;
        }catch {
            if(movedNew&&Directory.Exists(game.Mod)){var failed=Path.Combine(op,"failed-new-mod");SafeTree(game.Mod,game.Root);Directory.Move(game.Mod,failed);}
            if(movedOld&&Directory.Exists(previous))Directory.Move(previous,game.Mod);
            // Profile commits are atomic. A failure leaves the prior profile in place.
            if(oldProfile!=null&&File.Exists(game.Profiles)&&!File.ReadAllBytes(game.Profiles).SequenceEqual(oldProfile))AtomicWrite(game.Profiles,oldProfile);
            if(oldProfile==null&&profileCommitted&&File.Exists(game.Profiles))File.Delete(game.Profiles);
            throw;
        }finally {
            if(Directory.Exists(staging)){SafeTree(staging,game.Root);Directory.Move(staging,Path.Combine(op,"unused-staging"));}
        }
    }
    public string Restore(GameLocation game,bool checkRunning=true,Action<string>? progress=null) {
        progress?.Invoke("Conferindo a instalação antes de restaurar…");Preflight(game,checkRunning,false);
        using var guard=Acquire(game);var profiles=ReadProfiles(game.Profiles);Disable(profiles);
        var oldProfile=File.Exists(game.Profiles)?File.ReadAllBytes(game.Profiles):null;
        var op=Operation(game,"restauracao");var removed=Path.Combine(op,"translation-disabled");bool moved=false,profileCommitted=false;
        try {
            progress?.Invoke("Guardando a tradução e preservando os outros mods…");
            if(Directory.Exists(game.Mod)){Directory.Move(game.Mod,removed);moved=true;}
            AtomicWrite(game.Profiles,Encoding.UTF8.GetBytes(profiles.ToJsonString(JsonOptions)));
            profileCommitted=true;
            File.WriteAllText(Path.Combine(op,"result.json"),"{\"action\":\"restore\",\"original_game_unchanged\":true}");
            progress?.Invoke("Idioma original restaurado. Seus saves e outros mods foram preservados.");return op;
        }catch {
            if(moved&&Directory.Exists(removed))Directory.Move(removed,game.Mod);
            if(oldProfile!=null&&File.Exists(game.Profiles)&&!File.ReadAllBytes(game.Profiles).SequenceEqual(oldProfile))AtomicWrite(game.Profiles,oldProfile);
            if(oldProfile==null&&profileCommitted&&File.Exists(game.Profiles))File.Delete(game.Profiles);
            throw;
        }
    }
}
