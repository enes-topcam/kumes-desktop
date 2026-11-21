// Kalıcı dosyalar (%APPDATA%\KumesCS): ayarlar, son tavuk verisi + gönderilen bildirimler, kayıt dosyası.
using System.Text.Encodings.Web;
using System.Text.Json;
using Kumes.Steam;

namespace Kumes;

sealed class Settings
{
	public bool Visible { get; set; } = true;
	public bool HideWhileCs2 { get; set; } = true;
	public bool AlwaysOnTop { get; set; } = true;
	public List<string> Accounts { get; set; } = new(); // bağlı Steam hesapları (accounts/<ad> klasörleri)
	public string? Selected { get; set; }                // Ana Sayfa'da ve masaüstünde gösterilen tavuğun hesabı
	public bool Demo { get; set; }                       // giriş yapmadan deneniyor (örnek tavuk)
}

sealed class NotifyFlags
{
	public bool Day { get; set; }
	public bool Hours { get; set; }
	public bool Expired { get; set; }
}

sealed class PetCache
{
	public PetInfo? Pet { get; set; }
	public Dictionary<string, NotifyFlags> Notified { get; set; } = new(); // yem bitiş zamanı -> bildirimler
	public SteamProfile? Profile { get; set; }                              // Steam'deki görünen ad, profil fotoğrafı
	public List<FoodSample> History { get; set; } = new();                  // ölçüm geçmişi (tavuk detayındaki Geçmiş)
}

static class Store
{
	static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
	// KUMES_DATA: geliştirirken ayrı bir veri klasörüyle denemek için (gerçek hesaplara dokunmadan)
	public static string DataDir => Environment.GetEnvironmentVariable("KUMES_DATA") is { Length: > 0 } d ? d : Path.Combine(AppData, "KumesCS");
	public static string ElectronDir => Path.Combine(AppData, "Kumes"); // eski (Electron) sürümün klasörü
	public static string File(string name) => Path.Combine(DataDir, name);

	static readonly JsonSerializerOptions Json = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static T Load<T>(string name) where T : new()
	{
		try
		{
			var path = File(name);
			if (System.IO.File.Exists(path)) return JsonSerializer.Deserialize<T>(System.IO.File.ReadAllBytes(path), Json) ?? new T();
		}
		catch (Exception e)
		{
			Log.Write($"{name} okunamadı: {e.Message}");
		}
		return new T();
	}

	public static void Save<T>(string name, T value)
	{
		try
		{
			var path = File(name);
			System.IO.File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(value, Json));
			System.IO.File.Move(path + ".tmp", path, overwrite: true);
		}
		catch (Exception e)
		{
			Log.Write($"{name} kaydedilemedi: {e.Message}");
		}
	}

	// Geçmiş boşsa eldeki gerçek eski ölçümlerle başlat: ilk denemenin (KumesProbe) çıktısı ve Electron önbelleği
	public static void SeedHistory(PetCache cache)
	{
		if (cache.History.Count > 0) return;
		var samples = new List<PetInfo>();
		try
		{
			var probe = File("probe-result.json");
			if (System.IO.File.Exists(probe))
			{
				using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(probe));
				var r = doc.RootElement;
				if (r.TryGetProperty("pet", out var p) && r.TryGetProperty("at", out var at))
				{
					DateTimeOffset? T(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetDateTimeOffset() : null;
					samples.Add(new PetInfo(p.GetProperty("Found").GetBoolean(),
						StageName: p.TryGetProperty("StageName", out var st) ? st.GetString() : null,
						FoodExpiresAt: T("FoodExpiresAt"), NextUpgradeAt: T("NextUpgradeAt"))
					{ FetchedAt = at.GetDateTimeOffset() });
				}
			}
		}
		catch (Exception e) { Log.Write($"ilk deneme çıktısı okunamadı: {e.Message}"); }
		if (ImportElectronPet() is PetInfo old) samples.Add(old);
		if (cache.Pet is PetInfo cur) samples.Add(cur);
		foreach (var s in samples.Where(x => x.Found).OrderBy(x => x.FetchedAt)) PetStats.Add(cache.History, s);
		if (cache.History.Count > 0) Log.Write($"yem geçmişi {cache.History.Count} eski ölçümle başlatıldı");
	}

	// İlk açılışta Electron sürümünün son verisini al: Steam'e bağlanmadan tavuk hemen görünsün
	public static PetInfo? ImportElectronPet()
	{
		try
		{
			var path = Path.Combine(ElectronDir, "pet-cache.json");
			if (!System.IO.File.Exists(path)) return null;
			using var doc = JsonDocument.Parse(System.IO.File.ReadAllBytes(path));
			if (!doc.RootElement.TryGetProperty("pet", out var p) || p.ValueKind != JsonValueKind.Object) return null;
			string? S(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
			uint? U(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetUInt32() : null;
			int I(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
			DateTimeOffset? T(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number
				? DateTimeOffset.FromUnixTimeMilliseconds(v.GetInt64()) : null;
			return new PetInfo(
				Found: p.TryGetProperty("found", out var f) && f.ValueKind == JsonValueKind.True,
				ItemId: S("itemId"), Name: S("name"), PetId: U("petId"), Breed: S("breed"), Kind: S("kind"),
				Level: U("level") ?? 0, StageName: S("stageName"),
				FoodExpiresAt: T("foodExpiresAt"), NextUpgradeAt: T("nextUpgradeAt"), HatchedAt: T("hatchedAt"),
				Seed: U("seed"), Style: U("style"), FeedCount: I("feedCount"), RewardFeedCount: I("rewardFeedCount"))
			{
				FetchedAt = T("fetchedAt") ?? DateTimeOffset.Now,
			};
		}
		catch (Exception e)
		{
			Log.Write($"Electron verisi alınamadı: {e.Message}");
			return null;
		}
	}
}

// Kayıt dosyası (kumes.log, 512 KB'ı geçince kumes.old.log'a döner); ortak koddaki Log buraya yazar
static class FileLog
{
	static readonly object gate = new();

	public static void Write(string msg)
	{
		var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}";
		lock (gate)
		{
			try
			{
				var path = Store.File("kumes.log");
				var info = new FileInfo(path);
				if (info.Exists && info.Length > 512 * 1024) System.IO.File.Move(path, Store.File("kumes.old.log"), overwrite: true);
				System.IO.File.AppendAllText(path, line);
			}
			catch { /* yoksay */ }
		}
	}
}
