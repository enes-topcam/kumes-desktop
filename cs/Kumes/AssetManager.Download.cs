// CS2 kurulu değilse: tavuk modelleri, animasyonları ve renkleri kullanıcının KENDİ Steam hesabıyla Valve'ın içerik
// sunucusundan indirilir (oyunun tamamı değil, sadece gereken dosyalar: SteamService.DownloadCs2FilesAsync, mobil de
// bunu kullanır) ve Extract ile aynı biçimde hazırlanır (GLB + PNG + manifest). Dönüştürme ValveResourceFormat
// kütüphanesiyle uygulamanın içinde yapılır. Dosyalar sadece bu bilgisayarda kalır.
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumes.Steam;
using SkiaSharp;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using VResource = ValveResourceFormat.Resource;
using static Kumes.Lang;

namespace Kumes;

static partial class AssetManager
{
	static string DownloadDir => Path.Combine(Store.DataDir, "cs2-download"); // indirilen ham CS2 dosyaları (iş bitince silinir)
	static string DownloadCache => Path.Combine(Store.DataDir, "cs2-cache");  // CS2 dosya dizini önbelleği (her seferinde inmesin)

	// İndir ve hazırla (arka planda çağır). Önce anonim Steam girişiyle (hesap gerekmez; "Giriş yapmadan dene" de çalışsın);
	// Valve anonim erişimi kapatırsa ve giriş yapılmış hesap varsa (account) onunla. stage: 1-4 indirme adımı,
	// 5 hazırlanıyor; bytes: inen parça boyu.
	public static void ExtractFromSteam(SteamService anon, SteamService? account, Action<string> progress, Action<int>? stage, Action<long>? bytes, CancellationToken ct)
	{
		bool anonymous = true;
		async Task Fetch(IReadOnlyList<string> paths)
		{
			for (int attempt = 1; ; attempt++)
			{
				try
				{
					await (anonymous ? anon : account!).DownloadCs2FilesAsync(paths, DownloadDir, DownloadCache, ct, bytes, anonymous);
					return;
				}
				catch (SteamException e) when (anonymous && account != null && e.Code is "NOT_FOUND" or "STEAM_ERROR")
				{
					Log.Write($"anonim indirme olmadı ({e.Message}), Steam hesabıyla denenecek");
					anonymous = false;
					attempt = 0;
					continue;
				}
				catch (SteamException e) when (e.Code != "NEED_LOGIN" && attempt < 4)
				{
					Log.Write($"CS2 indirme denemesi {attempt} olmadı ({e.Message}), tekrar denenecek");
					await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
				}
			}
		}
		try { BuildFromFiles(Fetch, DownloadDir, OwnDir, progress, stage, ct); }
		finally { try { Directory.Delete(DownloadDir, true); } catch { } }
	}

	// Geliştirme (Kumes.exe --download-test <klasör>): aynı akış, dosyalar indirilmek yerine bu bilgisayardaki CS2'nin
	// pak01_dir.vpk'sından okunur. İndirme dışındaki her şeyi (eksik dosya turları, GLB, renkler, manifest) dener.
	public static void ExtractFromLocalVpk(string target)
	{
		var cs2 = FindCs2Dir() ?? throw new DirectoryNotFoundException("CS2 kurulumu bulunamadı.");
		var root = target + ".files";
		if (Directory.Exists(root)) Directory.Delete(root, true);
		using var pak = new SteamDatabase.ValvePak.Package();
		pak.Read(Path.Combine(cs2, "pak01_dir.vpk"));
		int count = 0;
		Task Fetch(IReadOnlyList<string> paths)
		{
			foreach (var path in paths)
			{
				var e = pak.FindEntry(path);
				if (e == null) { Log.Write($"CS2'de yok: {path}"); continue; }
				pak.ReadEntry(e, out var data);
				var f = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
				Directory.CreateDirectory(Path.GetDirectoryName(f)!);
				File.WriteAllBytes(f, data);
				count++;
			}
			Log.Write($"test: {paths.Count} dosya istendi (toplam {count})");
			return Task.CompletedTask;
		}
		BuildFromFiles(Fetch, root, target, t => Log.Write($"test: {t}"), null, CancellationToken.None);
	}

	// Modeli ve renkleri gereken dosyaları getirerek hazırlar; eksik parçaları (mesh, iskelet, animasyon, doku) turlar
	// halinde ister. fetch: verilen pak01 yollarını root altına aynı yollarla yazar.
	static void BuildFromFiles(Func<IReadOnlyList<string>, Task> fetch, string root, string target, Action<string> progress, Action<int>? stage, CancellationToken ct)
	{
		var tmp = target + ".tmp";
		if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
		Directory.CreateDirectory(tmp);
		var loader = new DownloadLoader(root, Animations);

		void Fetch(IEnumerable<string?> paths)
		{
			var list = paths.OfType<string>().Select(Compiled).Distinct().Where(p => !File.Exists(loader.Local(p))).ToList();
			if (list.Count > 0) fetch(list).GetAwaiter().GetResult();
		}

		var models = new Dictionary<string, string>();
		var coats = new Dictionary<string, Dictionary<string, object>>();
		var styles = new Dictionary<string, Dictionary<string, string>>();

		// 1) bütün modeller (tek istekte)
		progress(T("CS2 tavuk dosyaları indiriliyor (1/4)…"));
		stage?.Invoke(1);
		Fetch(Models.Values);

		// 2) GLB'ler (oyundaki animasyonlarla): eksik parçalar (mesh, iskelet, animasyon) bütün ırklar için birlikte
		// istenir. Model her turda yeniden okunur: animasyon listesi ilk okumada hafızaya alınıyor, sonradan inen
		// klipler eski nesnede görünmez.
		progress(T("CS2 tavuk dosyaları indiriliyor (2/4)…"));
		stage?.Invoke(2);
		var pending = Models.Keys.ToList();
		for (int round = 1; round <= 10 && pending.Count > 0; round++)
		{
			ct.ThrowIfCancellationRequested();
			var missing = new HashSet<string>();
			foreach (var breed in pending.ToList())
			{
				loader.Missing.Clear();
				var glb = Path.Combine(tmp, $"{breed}.glb");
				using (var res = loader.LoadFile(Models[breed]) ?? throw new InvalidOperationException($"model indirilemedi: {Models[breed]}"))
				{
					try
					{
						var exp = new GltfModelExporter(loader) { ProgressReporter = new Progress<string>(_ => { }), ExportMaterials = false, ExportAnimations = true };
						foreach (var a in Animations) exp.AnimationFilter.Add(a);
						exp.Export(res, glb, ct);
					}
					catch (Exception e) when (loader.Missing.Count > 0) { Log.Write($"{breed}: eksik dosyayla durdu ({e.GetType().Name})"); }
				}
				File.Delete(Path.Combine(tmp, $"{breed}_physics.glb"));
				if (loader.Missing.Count == 0 && File.Exists(glb)) { models[breed] = $"{breed}.glb"; pending.Remove(breed); }
				else missing.UnionWith(loader.Missing);
			}
			if (missing.Count == 0) break;
			Log.Write($"tur {round}: {missing.Count} eksik dosya ({string.Join(", ", pending)})");
			Fetch(missing);
		}
		if (pending.Count > 0) throw new InvalidOperationException($"model hazırlanamadı: {string.Join(", ", pending)}");

		// 3) renkler: modelin materyal grupları (grup adı = eşyanın style numarası) + modelin içinde geçen materyaller
		progress(T("CS2 tavuk dosyaları indiriliyor (3/4)…"));
		stage?.Invoke(3);
		var materials = new Dictionary<string, List<string>>();
		foreach (var (breed, modelPath) in Models)
		{
			using var res = loader.LoadFile(modelPath)!;
			var groups = ((Model)res.DataBlock!).GetMaterialGroups().ToList();
			materials[breed] = groups.SelectMany(g => g.Materials ?? []).Concat(MaterialRefs(loader.Local(modelPath)))
				.Select(m => m.Replace('\\', '/')).Where(m => !m.Contains("roasted")).Distinct().ToList();
			styles[breed] = new();
			foreach (var g in groups)
			{
				if (g.Name is null or "default" || g.Materials?.FirstOrDefault(m => !m.Contains("_fur")) is not string m) continue;
				if (CoatName(breed, Path.GetFileNameWithoutExtension(m)) is string coat) styles[breed][g.Name] = coat;
			}
		}
		Fetch(materials.Values.SelectMany(m => m));

		// her renk: kendi materyali + (civcivde) tüy katmanı materyali
		var plan = new List<(string Breed, string Coat, MatTextures Tex, MatTextures? Fur)>();
		foreach (var (breed, mats) in materials)
			foreach (var mat in mats.Where(m => !Path.GetFileNameWithoutExtension(m).EndsWith("_fur")))
			{
				var baseName = Path.GetFileNameWithoutExtension(mat);
				if (CoatName(breed, baseName) is not string coat) continue;
				var fur = mats.FirstOrDefault(m => Path.GetFileNameWithoutExtension(m) == baseName + "_fur");
				plan.Add((breed, coat, Textures(loader, mat), fur != null ? Textures(loader, fur) : null));
			}

		// 4) dokular (tek istekte) ve PNG'ye çevirme
		progress(T("CS2 tavuk dosyaları indiriliyor (4/4)…"));
		stage?.Invoke(4);
		Fetch(plan.SelectMany(c => new[] { c.Tex.Color, c.Tex.Normal, c.Tex.Ao, c.Fur?.Color, c.Fur?.Translucency }));
		stage?.Invoke(5);
		foreach (var breed in Models.Keys) coats[breed] = new();
		foreach (var (breed, coat, tex, fur) in plan)
		{
			ct.ThrowIfCancellationRequested();
			string? Png(string? vtex) => vtex == null ? null : TextureToPng(loader, vtex, tmp);
			var color = Png(tex.Color);
			if (color == null) continue;
			string? furColor = null, furAlpha = null;
			if (fur is { } ft && Png(ft.Color) is string fc && ft.Translucency is string tr && TextureToPng(loader, tr, tmp, alphaOnly: true) is string fa)
			{ furColor = fc; furAlpha = fa; }
			coats[breed][coat] = new { color, normal = Png(tex.Normal), ao = Png(tex.Ao), furColor, furAlpha };
		}
		foreach (var breed in Models.Keys) Log.Write($"{breed}: model ve {coats[breed].Count} renk hazır");

		var manifest = new
		{
			version = ManifestVersion,
			extractedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
			source = "steam", // CS2 kurulu değildi: dosyalar hesapla indirildi
			models,
			coats,
			styles,
			defaultCoat = new Dictionary<string, string> { ["silkie"] = "splash", ["chick"] = "yellow" },
		};
		File.WriteAllText(Path.Combine(tmp, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
		if (Directory.Exists(target)) Directory.Delete(target, true);
		Directory.Move(tmp, target);
	}

	static string Compiled(string path)
	{
		path = path.Replace('\\', '/').ToLowerInvariant();
		return path.EndsWith("_c") ? path : path + "_c";
	}

	// Modelin içinde geçen materyal yolları (civcivde renk grubu yok; materyaller sadece model dosyasında)
	static IEnumerable<string> MaterialRefs(string modelFile) =>
		Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(modelFile)), @"materials/[a-z0-9_/]+\.vmat").Select(m => m.Value);

	sealed record MatTextures(string? Color, string? Normal, string? Ao, string? Translucency);

	static MatTextures Textures(DownloadLoader loader, string material)
	{
		if (loader.LoadFile(material)?.DataBlock is not Material m) return new(null, null, null, null);
		string? T(params string[] keys) => keys.Select(k => m.TextureParams.TryGetValue(k, out var t) && !string.IsNullOrEmpty(t) ? t : null).FirstOrDefault(t => t != null);
		// tüy katmanının saydamlığı ayrı bir dokuda değil, renk dokusunun alfa kanalında (CS2'den çıkarma aracı onu
		// "<renk>_trans.png" olarak ayırıyor; burada da TextureAlphaToPng ayırıyor)
		var color = T("g_tColor");
		return new(color, T("g_tNormal"), T("g_tAmbientOcclusion"), T("g_tTranslucency") ?? color);
	}

	// .vtex_c -> PNG (tmp altında aynı yolla); manifest için göreli yol döner. alphaOnly: sadece alfa kanalı (gri)
	static string? TextureToPng(DownloadLoader loader, string vtex, string tmp, bool alphaOnly = false)
	{
		var rel = Compiled(vtex)[..^2]; // ".vtex"
		rel = Path.ChangeExtension(rel, null) + (alphaOnly ? "_trans.png" : ".png");
		var file = Path.Combine(tmp, rel.Replace('/', Path.DirectorySeparatorChar));
		if (File.Exists(file)) return rel;
		if (loader.LoadFile(vtex)?.DataBlock is not Texture t) return null;
		using var bmp = t.GenerateBitmap();
		using var gray = alphaOnly ? new SKBitmap(bmp.Width, bmp.Height, SKColorType.Gray8, SKAlphaType.Opaque) : null;
		if (gray != null)
			for (int y = 0; y < bmp.Height; y++)
				for (int x = 0; x < bmp.Width; x++)
				{
					byte a = bmp.GetPixel(x, y).Alpha;
					gray.SetPixel(x, y, new SKColor(a, a, a));
				}
		using var data = (gray ?? bmp).Encode(SKEncodedImageFormat.Png, 100);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		File.WriteAllBytes(file, data.ToArray());
		return rel;
	}

	// İndirilen dosyalardan okuyan yükleyici; olmayanları not eder (sonra indirilir). Animasyon grafiği oyundaki
	// bütün klipleri ister (~73 MB): sadece Kümes'in kullandıkları indirilir, diğerleri yok sayılır.
	sealed class DownloadLoader(string root, IEnumerable<string> clips) : IFileLoader
	{
		public readonly HashSet<string> Missing = [];
		readonly HashSet<string> wanted = new(clips, StringComparer.OrdinalIgnoreCase);

		public string Local(string path) => Path.Combine(root, path.Replace('\\', '/').ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar));

		VResource? Read(string path)
		{
			path = path.Replace('\\', '/').ToLowerInvariant();
			if (path.StartsWith("animation/anims/") && !wanted.Contains(Path.GetFileName(path).Split('.')[0])) return null;
			var f = Local(path);
			if (!File.Exists(f)) { Missing.Add(path); return null; }
			// ad önce verilip akıştan okunur: animasyon adları CS2'deki gibi göreli yol olsun
			// ("animation/anims/chicken/world/chick_idle01"); dosya yoluyla okununca tam yol oluyor
			// bellekten oku: VRF akışı kaynak kapanana kadar açık tutar, dosya açık kalırsa indirme klasörü silinemez
			var r = new VResource { FileName = path };
			r.Read(new MemoryStream(File.ReadAllBytes(f)));
			return r;
		}

		public VResource? LoadFile(string file) => Read(Compiled(file));
		public VResource? LoadFileCompiled(string file) => Read(Compiled(file));
		public ValveResourceFormat.CompiledShader.ShaderCollection? LoadShader(string shaderName) => null;

		public Stream? GetFileStream(string file)
		{
			var f = Local(file);
			if (File.Exists(f)) return new MemoryStream(File.ReadAllBytes(f));
			Missing.Add(file.Replace('\\', '/').ToLowerInvariant());
			return null;
		}
	}
}
