using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WindWaker.Installer;

/// <summary>Uma versão da tradução publicada nas Releases do GitHub.</summary>
public record UpdateInfo(Version Version, string Tag, string PayloadUrl, string PayloadSha256, string PackSha256, long PayloadSize);

/// <summary>
/// Procura novas versões da tradução nas Releases e baixa o pacote (manifest.json + .pack) para o InstallerEngine instalar.
/// Cada release precisa publicar dois arquivos: <c>update.json</c> e o zip indicado por ele (ver tools/make_update_assets.py).
/// </summary>
public static class Updater {
    public const string Repo = "fabricioply/Wind-Waker-HD-PT-BR";
    public const string ManifestAsset = "update.json";
    public static readonly Uri LatestReleaseApi = new($"https://api.github.com/repos/{Repo}/releases/latest");
    public static readonly string DownloadPrefix = $"https://github.com/{Repo}/releases/download/";
    public const long MaxPayloadBytes = 64L * 1024 * 1024;
    const long MaxManifestBytes = 64 * 1024;
    const long MaxApiBytes = 2 * 1024 * 1024;

    public static HttpClient CreateClient() {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WindWakerHD-PTBR-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static Version? ParseVersion(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length > 0 && (text[0] == 'v' || text[0] == 'V')) text = text[1..];
        var core = text.Split('-', '+')[0];
        if (!Version.TryParse(core, out var v)) return null;
        // "1.0" e "1.0.0" têm de ser equivalentes
        return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
    }

    /// <summary>Versão do manifest.json da tradução já instalada no ModManager (null se não instalada).</summary>
    public static Version? InstalledVersion(GameLocation game) {
        try {
            var path = Path.Combine(game.Mod, "manifest.json");
            if (!File.Exists(path)) return null;
            return ParseVersion(JsonNode.Parse(File.ReadAllText(path))?["version"]?.GetValue<string>());
        } catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Versão do manifest.json dentro de um payload.zip.</summary>
    public static Version? PayloadVersion(byte[] payload) {
        try {
            using var z = new ZipArchive(new MemoryStream(payload), ZipArchiveMode.Read);
            using var s = z.GetEntry("manifest.json")?.Open();
            if (s == null) return null;
            return ParseVersion(JsonNode.Parse(s)?["version"]?.GetValue<string>());
        } catch (Exception e) when (e is InvalidDataException or JsonException or InvalidOperationException) { return null; }
    }

    static bool IsSha256(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);

    static async Task<byte[]> ReadLimited(HttpClient http, Uri url, long limit, CancellationToken ct) {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > limit) throw new IOException("Resposta maior que o esperado.");
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920]; int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0) {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > limit) throw new IOException("Resposta maior que o esperado.");
        }
        return buffer.ToArray();
    }

    /// <summary>Consulta a release mais recente. Retorna null se ela não traz um update.json válido.</summary>
    public static async Task<UpdateInfo?> FetchLatestAsync(HttpClient http, Uri? api = null, string? downloadPrefix = null, CancellationToken ct = default) {
        api ??= LatestReleaseApi; downloadPrefix ??= DownloadPrefix;
        var release = JsonNode.Parse(await ReadLimited(http, api, MaxApiBytes, ct).ConfigureAwait(false))?.AsObject();
        var tag = release?["tag_name"]?.GetValue<string>();
        var tagVersion = ParseVersion(tag);
        if (release == null || tag == null || tagVersion == null || release["assets"] is not JsonArray assets) return null;

        string? Url(string name) {
            foreach (var a in assets) {
                if (a?["name"]?.GetValue<string>() != name) continue;
                var url = a["browser_download_url"]?.GetValue<string>();
                // O pacote só pode vir do próprio repositório da tradução.
                return url != null && url.StartsWith(downloadPrefix, StringComparison.OrdinalIgnoreCase) ? url : null;
            }
            return null;
        }

        var manifestUrl = Url(ManifestAsset);
        if (manifestUrl == null) return null;
        var manifest = JsonNode.Parse(Encoding.UTF8.GetString(await ReadLimited(http, new Uri(manifestUrl), MaxManifestBytes, ct).ConfigureAwait(false)))?.AsObject();
        var version = ParseVersion(manifest?["version"]?.GetValue<string>());
        var payloadName = manifest?["payload"]?.GetValue<string>();
        var payloadSha = manifest?["payload_sha256"]?.GetValue<string>()?.ToLowerInvariant();
        var packSha = manifest?["pack_sha256"]?.GetValue<string>()?.ToLowerInvariant();
        var size = manifest?["payload_size"]?.GetValue<long>() ?? 0;
        if (version == null || version != tagVersion || string.IsNullOrEmpty(payloadName) || !IsSha256(payloadSha) || !IsSha256(packSha) || size <= 0 || size > MaxPayloadBytes) return null;
        var payloadUrl = Url(payloadName);
        if (payloadUrl == null) return null;
        return new UpdateInfo(version, tag, payloadUrl, payloadSha!, packSha!, size);
    }

    /// <summary>Baixa o pacote e confere tamanho, SHA-256 e versão antes de devolvê-lo.</summary>
    public static async Task<byte[]> DownloadPayloadAsync(HttpClient http, UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default) {
        using var response = await http.GetAsync(info.PayloadUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream((int)Math.Min(info.PayloadSize, MaxPayloadBytes));
        var chunk = new byte[81920]; int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0) {
            buffer.Write(chunk, 0, n);
            if (buffer.Length > info.PayloadSize) throw new IOException("O arquivo baixado é maior que o esperado.");
            progress?.Report((double)buffer.Length / info.PayloadSize);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length != info.PayloadSize) throw new IOException("O download da atualização veio incompleto.");
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(info.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new IOException("A atualização baixada não passou na verificação de integridade.");
        if (PayloadVersion(bytes) != info.Version) throw new IOException("A versão do pacote baixado não confere com a release.");
        return bytes;
    }
}
