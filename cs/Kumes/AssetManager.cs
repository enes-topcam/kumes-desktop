// CS2 tavuk modellerini, animasyonlarını ve renklerini kullanıcının KENDİ CS2 kurulumundan çıkarır
// (src/cs2-assets.js'in C# karşılığı). Valve dosyaları uygulamayla dağıtılmaz; %APPDATA%\KumesCS\cs2-assets
// altında durur. Çıkarma için Source 2 Viewer CLI (tools/vrf/Source2Viewer-CLI.exe) kullanılır.
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kumes.Render;
using Kumes.Steam;
using Microsoft.Win32;

namespace Kumes;

sealed record AssetSet(string Dir, JsonDocument Manifest);

static partial class AssetManager
{
	// Animasyon listesi değişince artır -> otomatik yeniden çıkarılır. Biçim Electron'unkiyle (5) aynı;
	// Electron'un klasörü (daha az animasyonla) yedek olarak kullanılabilir.
	const int ManifestVersion = 8; // v8: civciv modeli (gövde + tüy katmanı)
	const int MinUsableVersion = 5;

	public static string OwnDir => Path.Combine(Store.DataDir, "cs2-assets");
	static string ElectronAssetDir => Path.Combine(Store.ElectronDir, "cs2-assets");

	static readonly Dictionary<string, string> Models = new()
	{
		["silkie"] = "models/chicken/chicken_silkie.vmdl_c",
		["catalana"] = "models/chicken/chicken.vmdl_c",
		["polish"] = "models/chicken/chicken_polish.vmdl_c",
		["chick"] = "models/chicken/chick.vmdl_c", // civciv (aşama 1): aynı iskelet ve animasyonlar
	};
	static readonly Dictionary<string, string[]> MaterialPrefix = new()
	{
		["silkie"] = ["chicken_silkie_"],
		["catalana"] = ["chicken_catalan_", "chicken_catalana_"],
		["polish"] = ["chicken_polish_", "chicken_polish"],
		["chick"] = ["chick_"],
	};
	static readonly string[] Animations =
	[
		"chick_idle01", "chick_idle02", "chick_idle03",
		"chick_walk", "chick_start_walk",
		"chick_squat_start", "chick_squat_loop01", "chick_squat_stand",
		"chick_sleep_loop01",
		"chick_feed_to_sad01_loop",
		"chick_fall_flap", "chick_land",
		// numaralar: sadece yerinde kalanlar (trick02/05/07 tavuğu ekrandan çıkaracak kadar zıplatıyor)
		"chick_react01", "chick_react02", "chick_trick01", "chick_trick08", "chick_trick12", "chick_trick13",
		// kanat açma/çırpma (kendiliğinden ara sıra)
		"chick_trick04",
		// v6: oyundaki diğer numaralar (ölçüldü: en fazla ~113 px yükseliyor, pencereye sığıyor),
		// bekleme/oturma çeşitleri, yem yeme. (chick_sleep_flinch* sadece additive çıkıyor, kullanılmıyor.)
		"chick_trick03", "chick_trick06", "chick_trick09", "chick_trick10", "chick_trick11",
		"chick_idle03alt", "chick_squat_loop03", "chick_squat_loop04",
		"chick_feed01",
		// v7 (test): tavuğu boyunun 3-4 katı zıplatanlar; oynarken pencere tavuğu takip ediyor
		"chick_trick02", "chick_trick05", "chick_trick07",
	];

	public static AssetSet? Load(string dir, int minVersion = MinUsableVersion)
	{
		try
		{
			var path = Path.Combine(dir, "manifest.json");
			if (!File.Exists(path)) return null;
			var doc = JsonDocument.Parse(File.ReadAllBytes(path));
			var m = doc.RootElement;
			if (!m.TryGetProperty("version", out var v) || v.GetInt32() < minVersion
				|| !m.TryGetProperty("models", out var models) || !models.EnumerateObject().Any())
			{
				doc.Dispose();
				return null;
			}
			return new AssetSet(dir, doc);
		}
		catch (Exception e)
		{
			Log.Write($"manifest okunamadı ({dir}): {e.Message}");
			return null;
		}
	}

	// Güncel sürümle çıkarılmış kendi modellerimiz (yoksa ya da eskiyse yeniden çıkarılır)
	public static AssetSet? LoadOwn() => Load(OwnDir, ManifestVersion);

	// Gösterilecek en iyi set: güncel kendi setimiz, yoksa eski setimiz, o da yoksa Electron'unki
	public static AssetSet? LoadBest() => LoadOwn() ?? Load(OwnDir) ?? Load(ElectronAssetDir);

	// Irk + style numarasından model ve renk dokularını bul
	public static (string Model, CoatTextures Coat) Resolve(AssetSet set, PetInfo? pet)
	{
		var m = set.Manifest.RootElement;
		var models = m.GetProperty("models");
		var breed = pet?.Kind == "chick" ? "chick" : (pet?.Breed ?? "Silkie").ToLowerInvariant();
		if (!models.TryGetProperty(breed, out _)) breed = models.TryGetProperty("silkie", out _) ? "silkie" : models.EnumerateObject().First().Name;
		var model = Path.Combine(set.Dir, models.GetProperty(breed).GetString()!);

		var coats = m.GetProperty("coats").GetProperty(breed);
		string? coatKey = null;
		if (pet?.Style is uint style && m.TryGetProperty("styles", out var styles) && styles.TryGetProperty(breed, out var bs)
			&& bs.TryGetProperty(style.ToString(), out var ck))
			coatKey = ck.GetString();
		if (coatKey == null || !coats.TryGetProperty(coatKey, out _))
			coatKey = m.TryGetProperty("defaultCoat", out var dc) && dc.TryGetProperty(breed, out var d) && coats.TryGetProperty(d.GetString()!, out _)
				? d.GetString() : coats.EnumerateObject().First().Name;
		var coat = coats.GetProperty(coatKey!);
		string? P(string k) => coat.TryGetProperty(k, out var v) && v.GetString() is string s ? Path.Combine(set.Dir, s) : null;
		return (model, new CoatTextures(P("color")!, P("normal"), P("ao"), P("furColor"), P("furAlpha")));
	}

	// Kümes penceresinin arka planı: CS2'nin kendi Dust2 (B bölgesi) ekran görüntüsü, senin kurulumundan
	public static string BackdropPath => Store.File("backdrop.png");
	// yem sayacının köşesindeki eşya: oyunun envanterdeki "Sunnyside" yem torbası görseli
	public static string FeedBagPath => Store.File("feedbag.png");
	static readonly (string VpkPath, Func<string> Out)[] PanoramaImages =
	[
		("panorama/images/map_icons/screenshots/720p/de_dust2_0_png.vtex_c", () => BackdropPath),
		("panorama/images/econ/pets/chicken_feed_bag_2_png.vtex_c", () => FeedBagPath),
	];

	public static bool PanoramaImagesMissing => PanoramaImages.Any(i => !File.Exists(i.Out()));

	public static void ExtractBackdrop()
	{
		var exe = FindCli() ?? throw new FileNotFoundException("Source2Viewer-CLI bulunamadı (tools/vrf).");
		var cs2 = FindCs2Dir() ?? throw new DirectoryNotFoundException("CS2 kurulumu bulunamadı.");
		foreach (var (vpkPath, outPath) in PanoramaImages)
		{
			if (File.Exists(outPath())) continue;
			var tmp = Store.File("panorama.tmp");
			if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
			Directory.CreateDirectory(tmp);
			RunCli(exe, "-i", Path.Combine(cs2, "pak01_dir.vpk"), "-f", vpkPath, "-o", tmp, "-d");
			var png = Directory.GetFiles(tmp, "*.png", SearchOption.AllDirectories).FirstOrDefault()
				?? throw new FileNotFoundException($"Görsel çıkarılamadı: {vpkPath}");
			File.Move(png, outPath(), overwrite: true);
			Directory.Delete(tmp, true);
		}
	}

	// Tavuğun oyundaki renginin okunur adı (ör. style 2 -> "Splash"); bilinmiyorsa null
	public static string? CoatName(AssetSet set, PetInfo? pet)
	{
		if (pet?.Style is not uint style || pet.Breed is not string b) return null;
		var m = set.Manifest.RootElement;
		if (!m.TryGetProperty("styles", out var styles) || !styles.TryGetProperty(b.ToLowerInvariant(), out var bs)
			|| !bs.TryGetProperty(style.ToString(), out var ck) || ck.GetString() is not string key) return null;
		var words = key.Replace('_', ' ');
		return words.Length > 0 ? char.ToUpperInvariant(words[0]) + words[1..] : null;
	}

	// ---------- çıkarma ----------

	public static string? FindCli()
	{
		// exe'nin yanında ya da üst klasörlerde (geliştirirken: depo kökü/tools/vrf)
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
		{
			var p = Path.Combine(dir.FullName, "tools", "vrf", "Source2Viewer-CLI.exe");
			if (File.Exists(p)) return p;
		}
		return null;
	}

	public static string? FindCs2Dir()
	{
		var roots = new List<string>();
		void AddRoot(string? p) { if (!string.IsNullOrEmpty(p)) { p = Path.GetFullPath(p); if (!roots.Contains(p, StringComparer.OrdinalIgnoreCase)) roots.Add(p); } }
		try { AddRoot(Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") as string); } catch { }
		AddRoot(@"C:\Program Files (x86)\Steam");
		AddRoot(@"C:\Program Files\Steam");
		foreach (var root in roots.ToArray())
		{
			try
			{
				var vdf = File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf"));
				foreach (Match m in LibraryPath().Matches(vdf)) AddRoot(m.Groups[1].Value.Replace(@"\\", @"\"));
			}
			catch { /* yok */ }
		}
		foreach (var root in roots)
		{
			var dir = Path.Combine(root, "steamapps", "common", "Counter-Strike Global Offensive", "game", "csgo");
			if (File.Exists(Path.Combine(dir, "pak01_dir.vpk"))) return dir;
		}
		return null;
	}

	static string? VpkStamp(string vpk)
	{
		try
		{
			var fi = new FileInfo(vpk);
			if (!fi.Exists) return null;
			// Electron ile aynı: milisaniyeyi yuvarla (Math.round(mtimeMs))
			long ms = (long)Math.Round((fi.LastWriteTimeUtc - DateTime.UnixEpoch).Ticks / 10_000.0);
			return $"{ms}:{fi.Length}";
		}
		catch { return null; }
	}

	// CS2 güncellendi mi? (paket dosyası, modeller çıkarıldığından beri değiştiyse)
	public static bool Cs2Changed(AssetSet set)
	{
		if (!set.Manifest.RootElement.TryGetProperty("vpkStamp", out var st) || st.GetString() is not string old) return false;
		var cs2 = FindCs2Dir();
		if (cs2 == null) return false;
		var now = VpkStamp(Path.Combine(cs2, "pak01_dir.vpk"));
		return now != null && now != old;
	}

	static string RunCli(string exe, params string[] args)
	{
		var psi = new ProcessStartInfo(exe)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
		};
		foreach (var a in args) psi.ArgumentList.Add(a);
		using var p = Process.Start(psi)!;
		var err = p.StandardError.ReadToEndAsync();
		var output = p.StandardOutput.ReadToEnd();
		p.WaitForExit();
		output += err.Result;
		if (p.ExitCode != 0) throw new InvalidOperationException($"Source2Viewer-CLI hata kodu {p.ExitCode}: {output[Math.Max(0, output.Length - 400)..]}");
		return output;
	}

	static string? CoatName(string breed, string baseName)
	{
		foreach (var prefix in MaterialPrefix[breed])
		{
			if (baseName == prefix.TrimEnd('_')) return "default";
			if (baseName.StartsWith(prefix)) return baseName[prefix.Length..];
		}
		return null;
	}

	// Modelin veri bloğundaki materyal grupları: grup adı = eşyanın "style" numarası.
	static Dictionary<string, string> ParseStyles(string breed, string dataBlock)
	{
		var styles = new Dictionary<string, string>();
		foreach (Match m in MaterialGroup().Matches(dataBlock))
		{
			if (m.Groups[1].Value == "default") continue;
			var baseName = Path.GetFileNameWithoutExtension(m.Groups[2].Value);
			if (CoatName(breed, baseName) is string coat) styles[m.Groups[1].Value] = coat;
		}
		return styles;
	}

	static (string? Color, string? Normal, string? Ao, string? Translucency) ReadVmat(string file)
	{
		var text = File.ReadAllText(file);
		string? Get(string key)
		{
			var m = Regex.Match(text, $"\"{key}\"\\s+\"([^\"]+\\.png)\"");
			return m.Success ? m.Groups[1].Value : null;
		}
		return (Get("TextureColor"), Get("TextureNormal"), Get("TextureAmbientOcclusion"), Get("TextureTranslucency"));
	}

	// Tüm modelleri ve renkleri çıkarır; ilerlemeyi progress ile bildirir. Ana iş parçacığında ÇAĞIRMA.
	public static void Extract(Action<string> progress)
	{
		var exe = FindCli() ?? throw new FileNotFoundException("Source2Viewer-CLI bulunamadı (tools/vrf).");
		var cs2 = FindCs2Dir() ?? throw new DirectoryNotFoundException("CS2 kurulumu bulunamadı.");
		var vpk = Path.Combine(cs2, "pak01_dir.vpk");
		var gameinfo = Path.Combine(cs2, "gameinfo.gi");
		var tmp = OwnDir + ".tmp";
		if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
		Directory.CreateDirectory(tmp);

		int step = 0, total = Models.Count * 2;
		foreach (var (breed, model) in Models)
		{
			progress($"CS2 modelleri çıkarılıyor ({++step}/{total})…");
			RunCli(exe, "-i", vpk, "-f", model, "-o", Path.Combine(tmp, $"{breed}.glb"), "-d",
				"--gltf_export_format", "glb", "--gltf_export_animations",
				"--gltf_animation_list", string.Join(",", Animations), "--game", gameinfo);
			File.Delete(Path.Combine(tmp, $"{breed}_physics.glb"));
		}

		var styles = new Dictionary<string, Dictionary<string, string>>();
		foreach (var (breed, model) in Models)
		{
			try { styles[breed] = ParseStyles(breed, RunCli(exe, "-i", vpk, "-f", model, "-b", "DATA")); }
			catch { styles[breed] = new(); }
		}

		foreach (var breed in Models.Keys)
		{
			progress($"CS2 renkleri çıkarılıyor ({++step}/{total})…");
			foreach (var prefix in MaterialPrefix[breed])
				RunCli(exe, "-i", vpk, "-f", $"materials/models/chicken/{prefix}", "-e", "vmat_c", "-o", tmp, "-d", "--game", gameinfo);
		}

		// manifest (Electron sürümüyle aynı biçim)
		var matDir = Path.Combine(tmp, "materials", "models", "chicken");
		var vmats = Directory.Exists(matDir) ? Directory.GetFiles(matDir, "*.vmat") : [];
		var models = new Dictionary<string, string>();
		var coats = new Dictionary<string, Dictionary<string, object>>();
		foreach (var breed in Models.Keys)
		{
			if (File.Exists(Path.Combine(tmp, $"{breed}.glb"))) models[breed] = $"{breed}.glb";
			coats[breed] = new();
			foreach (var f in vmats)
			{
				var baseName = Path.GetFileNameWithoutExtension(f);
				if (baseName.Contains("roasted") || baseName.EndsWith("_fur") || CoatName(breed, baseName) is not string coat) continue;
				var (color, normal, ao, _) = ReadVmat(f);
				bool Ok(string? p) => p != null && File.Exists(Path.Combine(tmp, p));
				if (!Ok(color)) continue;
				// civcivin tüy katmanı ayrı malzemede (<renk>_fur.vmat): rengi ve saydamlık maskesi aynı renge bağlanır
				var furVmat = Path.Combine(matDir, baseName + "_fur.vmat");
				string? furColor = null, furAlpha = null;
				if (File.Exists(furVmat))
				{
					var (fc, _, _, ft) = ReadVmat(furVmat);
					if (Ok(fc) && Ok(ft)) { furColor = fc; furAlpha = ft; }
				}
				coats[breed][coat] = new { color, normal = Ok(normal) ? normal : null, ao = Ok(ao) ? ao : null, furColor, furAlpha };
			}
		}
		if (models.Count == 0) throw new InvalidOperationException("Model çıkarılamadı.");
		var manifest = new
		{
			version = ManifestVersion,
			extractedAt = DateTimeOffset.Now.ToUnixTimeMilliseconds(),
			models,
			coats,
			styles,
			vpkStamp = VpkStamp(vpk),
			defaultCoat = new Dictionary<string, string> { ["silkie"] = "splash", ["chick"] = "yellow" },
		};
		File.WriteAllText(Path.Combine(tmp, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

		if (Directory.Exists(OwnDir)) Directory.Delete(OwnDir, true);
		Directory.Move(tmp, OwnDir);
	}

	[GeneratedRegex("\"path\"\\s+\"([^\"]+)\"")]
	private static partial Regex LibraryPath();

	[GeneratedRegex("m_name\\s*=\\s*\"([^\"]+)\"\\s*m_materials\\s*=\\s*\\[\\s*resource:\"([^\"]+)\"")]
	private static partial Regex MaterialGroup();
}
