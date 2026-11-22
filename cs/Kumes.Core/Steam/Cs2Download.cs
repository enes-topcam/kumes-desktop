// CS2 dosyalarını (tavuk modeli, renkleri, Dust2 görseli, yem torbası) kullanıcının KENDİ Steam hesabıyla
// Valve'ın içerik sunucularından indirir: Steam istemcisinin oyun indirirken yaptığı gibi.
// Oyunun tamamı değil: sadece game/csgo/pak01_dir.vpk'nın dizini ve istenen dosyaların bulunduğu ~1 MB'lık
// parçalar. Dosyalar sadece bu cihazda kalır; biz hiçbir sunucuda Valve dosyası tutmayız.
// (Telefonda CS2 yok; masaüstünde de CS2 kurulu değilse ya da Source2Viewer-CLI yoksa bu kullanılır.)
using System.Text;
using SteamKit2;
using SteamKit2.CDN;

namespace Kumes.Steam;

public sealed record Cs2DownloadReport(uint DepotId, ulong ManifestId, long DownloadedBytes, int Chunks, IReadOnlyList<(string Path, int Bytes)> Files);

public partial class SteamService
{
	const string VpkDir = "game/csgo/pak01_dir.vpk";

	// vpkPaths: pak01 içindeki yollar (ör. "panorama/images/econ/pets/chicken_feed_bag_2_png.vtex_c").
	// outDir altına aynı yollarla yazılır. cacheDir: dizin ve manifest önbelleği (her seferinde 7 MB inmesin).
	// onBytes: her parça inince indirilen bayt (arka plan iş parçacığından; masaüstündeki indirme penceresi için)
	public async Task<Cs2DownloadReport> DownloadCs2FilesAsync(IReadOnlyList<string> vpkPaths, string outDir, string cacheDir, CancellationToken ct,
		Action<long>? onBytes = null)
	{
		var saved = store.Load() ?? throw new SteamException("NEED_LOGIN", "Steam girişi gerekiyor.");
		using var conn = new Connection();
		var user = conn.Client.GetHandler<SteamUser>()!;
		var loggedOn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		conn.Manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
		{
			if (cb.Result == EResult.OK) loggedOn.TrySetResult();
			else loggedOn.TrySetException(new SteamException(cb.Result is EResult.InvalidPassword or EResult.AccessDenied or EResult.Expired or EResult.Revoked
				? "NEED_LOGIN" : "STEAM_ERROR", $"Steam girişi olmadı: {cb.Result}"));
		});
		conn.OnDisconnected = () => loggedOn.TrySetException(new SteamException("STEAM_ERROR", "Steam bağlantısı koptu."));

		await conn.ConnectAsync(ct);
		user.LogOn(new SteamUser.LogOnDetails
		{
			Username = saved.AccountName,
			AccessToken = saved.RefreshToken,
			ShouldRememberPassword = true,
			MachineName = deviceName,
			LoginID = loginId + 1, // tavuk güncellemesiyle aynı anda çalışırsa birbirini atmasın
		});
		await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
		try
		{
			var dl = new DepotReader(conn.Client, cacheDir) { OnBytes = onBytes };
			await dl.OpenAsync(ct);
			var index = await dl.LoadVpkIndexAsync(ct);
			var files = new List<(string, int)>();
			foreach (var path in vpkPaths)
			{
				ct.ThrowIfCancellationRequested();
				var key = path.Replace('\\', '/').ToLowerInvariant();
				if (!index.TryGetValue(key, out var e)) { Log.Write($"CS2'de yok: {path}"); continue; }
				var data = new byte[e.Preload.Length + e.Length];
				e.Preload.CopyTo(data, 0);
				if (e.Length > 0)
				{
					var (archive, offset) = e.Archive == 0x7fff
						? (VpkDir, (ulong)(dl.VpkDataStart + e.Offset))
						: ($"game/csgo/pak01_{e.Archive:000}.vpk", (ulong)e.Offset);
					await dl.ReadAsync(archive, offset, data.AsMemory(e.Preload.Length), ct);
				}
				var target = Path.Combine(outDir, key.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(target)!);
				await File.WriteAllBytesAsync(target, data, ct);
				files.Add((key, data.Length));
			}
			Log.Write($"CS2 dosyaları indirildi: {files.Count} dosya, {dl.DownloadedBytes / 1024} KB ({dl.Chunks} parça)");
			return new Cs2DownloadReport(dl.DepotId, dl.ManifestId, dl.DownloadedBytes, dl.Chunks, files);
		}
		finally
		{
			conn.Closing = true;
			user.LogOff();
			await Task.Delay(300, CancellationToken.None);
		}
	}

	sealed record VpkEntry(ushort Archive, uint Offset, uint Length, byte[] Preload);

	// CS2 içerik deposundan dosya parçası okur
	sealed class DepotReader(SteamClient client, string cacheDir)
	{
		const uint AppId = 730;
		readonly SteamApps apps = client.GetHandler<SteamApps>()!;
		readonly SteamContent content = client.GetHandler<SteamContent>()!;
		readonly Client cdn = new(client);
		readonly Dictionary<string, DepotManifest.FileData> files = new(StringComparer.OrdinalIgnoreCase);
		List<Server> servers = [];
		byte[] depotKey = [];
		public uint DepotId { get; private set; }
		public ulong ManifestId { get; private set; }
		public long DownloadedBytes { get; private set; }
		public int Chunks { get; private set; }
		public long VpkDataStart { get; private set; }
		public Action<long>? OnBytes { get; init; }

		public async Task OpenAsync(CancellationToken ct)
		{
			// CS2'nin depoları ve güncel (public) manifest numaraları
			var tokens = await apps.PICSGetAccessTokens(AppId, null).ToTask();
			tokens.AppTokens.TryGetValue(AppId, out var token);
			var info = await apps.PICSGetProductInfo(new SteamApps.PICSRequest(AppId, token), null).ToTask();
			var app = info.Results!.SelectMany(r => r.Apps.Values).First(a => a.ID == AppId);
			var depots = app.KeyValues["depots"].Children
				.Where(d => uint.TryParse(d.Name, out _))
				.Select(d => (Id: uint.Parse(d.Name!), Gid: d["manifests"]["public"]["gid"].Value ?? d["manifests"]["public"].Value,
					OsList: d["config"]["oslist"].Value))
				.Where(d => ulong.TryParse(d.Gid, out _))
				// önce bilinen içerik deposu (2347770) ya da geçen sefer bulunan; sonra işletim sistemine bağlı olmayanlar
				.OrderBy(d => d.Id == KnownDepot() ? 0 : d.OsList is null or "" ? 1 : 2)
				.ToList();
			Log.Write($"CS2 depoları: {string.Join(", ", depots.Select(d => $"{d.Id}{(string.IsNullOrEmpty(d.OsList) ? "" : $"({d.OsList})")}"))}");

			servers = (await content.GetServersForSteamPipe()).Where(s => s.Type is "SteamCache" or "CDN")
				.OrderBy(s => s.WeightedLoad).ToList();
			if (servers.Count == 0) throw new SteamException("STEAM_ERROR", "Steam içerik sunucusu bulunamadı.");

			foreach (var d in depots)
			{
				ct.ThrowIfCancellationRequested();
				var gid = ulong.Parse(d.Gid!);
				var key = await apps.GetDepotDecryptionKey(d.Id, AppId).ToTask();
				if (key.Result != EResult.OK) { Log.Write($"depo {d.Id}: anahtar yok ({key.Result})"); continue; }
				var manifest = await LoadManifestAsync(d.Id, gid, key.DepotKey, ct);
				var vpk = manifest.Files?.FirstOrDefault(f => Norm(f.FileName) == VpkDir);
				if (vpk == null) continue;
				DepotId = d.Id; ManifestId = gid; depotKey = key.DepotKey;
				try { File.WriteAllText(Path.Combine(cacheDir, "depot.txt"), d.Id.ToString()); } catch { }
				foreach (var f in manifest.Files!) files[Norm(f.FileName)] = f;
				Log.Write($"CS2 içerik deposu: {d.Id}, manifest {gid}, {manifest.Files!.Count} dosya");
				return;
			}
			throw new SteamException("NOT_FOUND", "CS2 dosyaları Steam deposunda bulunamadı.");
		}

		uint KnownDepot()
		{
			try { return uint.Parse(File.ReadAllText(Path.Combine(cacheDir, "depot.txt")).Trim()); }
			catch { return 2347770; } // "Counter-Strike 2 Content": game/csgo/pak01_*.vpk burada
		}

		static string Norm(string? name) => (name ?? "").Replace('\\', '/').ToLowerInvariant();

		async Task<DepotManifest> LoadManifestAsync(uint depot, ulong gid, byte[] key, CancellationToken ct)
		{
			var cache = Path.Combine(cacheDir, $"{depot}_{gid}.manifest");
			if (File.Exists(cache))
			{
				try { return DepotManifest.LoadFromFile(cache)!; } catch { File.Delete(cache); }
			}
			var code = await content.GetManifestRequestCode(depot, AppId, gid, "public");
			Exception? last = null;
			foreach (var s in servers.Take(4))
			{
				try
				{
					var m = await cdn.DownloadManifestAsync(depot, gid, code, s, key);
					if (m.FilenamesEncrypted) m.DecryptFilenames(key);
					Directory.CreateDirectory(cacheDir);
					foreach (var old in Directory.GetFiles(cacheDir, $"{depot}_*.manifest")) File.Delete(old);
					m.SaveToFile(cache);
					return m;
				}
				catch (Exception e) when (e is not OperationCanceledException) { last = e; }
			}
			throw new SteamException("STEAM_ERROR", $"Manifest indirilemedi: {last?.Message}");
		}

		// pak01_dir.vpk dizini: hangi dosya hangi arşivde, nerede (önbellekte manifest numarasıyla)
		public async Task<Dictionary<string, VpkEntry>> LoadVpkIndexAsync(CancellationToken ct)
		{
			var cache = Path.Combine(cacheDir, $"vpkdir_{ManifestId}.bin");
			byte[] head;
			if (File.Exists(cache)) head = await File.ReadAllBytesAsync(cache, ct);
			else
			{
				var h = new byte[28];
				await ReadAsync(VpkDir, 0, h, ct);
				uint treeSize = BitConverter.ToUInt32(h, 8);
				head = new byte[28 + treeSize];
				await ReadAsync(VpkDir, 0, head, ct);
				Directory.CreateDirectory(cacheDir);
				foreach (var old in Directory.GetFiles(cacheDir, "vpkdir_*.bin")) File.Delete(old);
				await File.WriteAllBytesAsync(cache, head, ct);
			}
			if (BitConverter.ToUInt32(head, 0) != 0x55AA1234 || BitConverter.ToUInt32(head, 4) != 2)
				throw new SteamException("PARSE_ERROR", "CS2 dosya dizini okunamadı (beklenmeyen VPK biçimi).");
			VpkDataStart = 28 + BitConverter.ToUInt32(head, 8);
			return ParseTree(head);
		}

		static Dictionary<string, VpkEntry> ParseTree(byte[] d)
		{
			var map = new Dictionary<string, VpkEntry>(StringComparer.Ordinal);
			int pos = 28, end = (int)(28 + BitConverter.ToUInt32(d, 8));
			string Str()
			{
				int e = Array.IndexOf(d, (byte)0, pos);
				var s = Encoding.UTF8.GetString(d, pos, e - pos);
				pos = e + 1;
				return s;
			}
			while (pos < end)
			{
				var ext = Str();
				if (ext.Length == 0) break;
				while (true)
				{
					var dir = Str();
					if (dir.Length == 0) break;
					while (true)
					{
						var name = Str();
						if (name.Length == 0) break;
						ushort pre = BitConverter.ToUInt16(d, pos + 4);
						ushort arc = BitConverter.ToUInt16(d, pos + 6);
						uint off = BitConverter.ToUInt32(d, pos + 8), len = BitConverter.ToUInt32(d, pos + 12);
						pos += 18;
						var preload = d.AsSpan(pos, pre).ToArray();
						pos += pre;
						var full = (dir == " " ? "" : dir + "/") + name + (ext == " " ? "" : "." + ext);
						map[full.ToLowerInvariant()] = new VpkEntry(arc, off, len, preload);
					}
				}
			}
			return map;
		}

		// Bir dosyanın [offset, offset+dest.Length) aralığını, onu kapsayan parçaları indirip doldurur
		public async Task ReadAsync(string file, ulong offset, Memory<byte> dest, CancellationToken ct)
		{
			if (!files.TryGetValue(file, out var f)) throw new SteamException("NOT_FOUND", $"Depoda yok: {file}");
			ulong end = offset + (ulong)dest.Length;
			foreach (var c in f.Chunks.OrderBy(c => c.Offset))
			{
				ulong cs = c.Offset, ce = c.Offset + c.UncompressedLength;
				if (ce <= offset || cs >= end) continue;
				var buf = await ChunkAsync(c, ct);
				ulong from = Math.Max(cs, offset), to = Math.Min(ce, end);
				buf.AsSpan((int)(from - cs), (int)(to - from)).CopyTo(dest.Span[(int)(from - offset)..]);
			}
		}

		async Task<byte[]> ChunkAsync(DepotManifest.ChunkData c, CancellationToken ct)
		{
			var buf = new byte[c.UncompressedLength];
			Exception? last = null;
			foreach (var s in servers.Take(4))
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					int n = await cdn.DownloadDepotChunkAsync(DepotId, c, s, buf, depotKey);
					if (n != buf.Length) throw new IOException($"parça eksik geldi ({n}/{buf.Length})");
					DownloadedBytes += c.CompressedLength; Chunks++;
					OnBytes?.Invoke(c.CompressedLength);
					return buf;
				}
				catch (Exception e) when (e is not OperationCanceledException) { last = e; }
			}
			throw new SteamException("STEAM_ERROR", $"Dosya parçası indirilemedi: {last?.Message}");
		}
	}
}
