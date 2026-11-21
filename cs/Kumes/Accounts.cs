// Masaüstü: bağlı Steam hesapları. Her hesabın kendi klasörü var (%APPDATA%\KumesCS\accounts\<hesap adı>):
// steam-session.bin (DPAPI), pet-cache.json (son veri, bildirimler, profil, ölçüm geçmişi), avatar.jpg.
// Seçili hesabın tavuğu masaüstünde yaşar ve Ana Sayfa'da görünür; Koleksiyon hepsini listeler.
using Kumes.Steam;

namespace Kumes;

sealed class DesktopAccount
{
	public const string DeviceName = "Kumes Masaustu (C#)";

	public DesktopAccount(string id)
	{
		Id = id;
		Store = new TokenStore(Path.Combine(Dir, "steam-session.bin"));
		Steam = new SteamService(Store, DeviceName, SteamService.DesktopLoginId);
		Cache = Kumes.Store.Load<PetCache>(CacheName);
	}

	// ---- Giriş yapmadan dene: örnek tavuk ----
	// Steam'e hiç bağlanmaz, diske yazılmaz, bildirim göndermez; gerçek bir hesapla giriş yapılınca kalkar.
	public const string DemoId = "demo";
	public bool IsDemo { get; }

	DesktopAccount(PetCache cache)
	{
		Id = DemoId;
		IsDemo = true;
		Store = new TokenStore(Path.Combine(Dir, "steam-session.bin"));
		Steam = new SteamService(Store, DeviceName, SteamService.DesktopLoginId);
		Cache = cache;
		State = "demo";
	}

	public static DesktopAccount Demo()
	{
		var now = DateTimeOffset.Now;
		var pet = new PetInfo(true, Name: "Pamuk", PetId: 4, Breed: "Silkie", Kind: "chicken", Level: 2, StageName: "Piliç",
			FoodExpiresAt: now + TimeSpan.FromHours(19 * 24 + 7), NextUpgradeAt: now + TimeSpan.FromHours(3 * 24 + 14),
			HatchedAt: now - TimeSpan.FromDays(38));
		var cache = new PetCache { Pet = pet };
		PetStats.Add(cache.History, pet);
		return new DesktopAccount(cache);
	}

	public string Id { get; }
	public TokenStore Store { get; }
	public SteamService Steam { get; }
	public PetCache Cache { get; }
	public string Dir => DirOf(Id);
	string CacheName => Path.Combine("accounts", Id, "pet-cache.json");
	public string AvatarPath => Path.Combine(Dir, "avatar.jpg");

	public string State = "idle";  // idle, fetching, ok, error, need-login, cs2
	public string Message = "";
	public byte[]? Avatar;         // düz alfa BGRA
	public int AvatarW, AvatarH;

	public bool LoggedIn => IsDemo || Store.Exists;
	public string DisplayName => IsDemo ? "Demo" : Cache.Profile?.PersonaName ?? Id;

	// Hesap adı klasör adı olur (kaldırınca klasör silinir): Steam hesap adları gibi sadece harf, rakam, alt çizgi
	public static bool ValidId(string id) => id.Length is > 0 and <= 64 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_');

	public static string DirOf(string id) => ValidId(id)
		? Kumes.Store.File(Path.Combine("accounts", id))
		: throw new ArgumentException($"geçersiz hesap adı: {id}");

	public void Save()
	{
		if (IsDemo) return;
		Directory.CreateDirectory(Dir);
		Kumes.Store.Save(CacheName, Cache);
	}

	public void LoadAvatar()
	{
		try
		{
			if (!File.Exists(AvatarPath)) { Avatar = null; return; }
			var img = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(AvatarPath), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
			var bgra = img.Data;
			for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
			Avatar = bgra;
			AvatarW = img.Width;
			AvatarH = img.Height;
		}
		catch (Exception e) { Log.Write($"profil fotoğrafı okunamadı: {e.Message}"); }
	}

	// Tek hesaplı eski sürüm: kök klasördeki oturum, önbellek ve profil fotoğrafı ilk hesabın klasörüne taşınır
	public static void MigrateLegacy(Settings settings)
	{
		if (settings.Accounts.Count > 0) return;
		var legacy = new TokenStore(Kumes.Store.File("steam-session.bin"));
		if (legacy.Load() is not { } s) return;
		var id = s.AccountName.ToLowerInvariant();
		if (!ValidId(id)) return;
		var dir = DirOf(id);
		Directory.CreateDirectory(dir);
		foreach (var name in new[] { "steam-session.bin", "pet-cache.json", "avatar.jpg" })
		{
			var from = Kumes.Store.File(name);
			try { if (File.Exists(from)) File.Move(from, Path.Combine(dir, name), overwrite: true); }
			catch (Exception e) { Log.Write($"{name} taşınamadı: {e.Message}"); }
		}
		settings.Accounts.Add(id);
		settings.Selected = id;
		Kumes.Store.Save("settings.json", settings);
		Log.Write($"eski oturum hesaba taşındı: {id}");
	}

	// Geliştirme modları için: seçili hesabın son tavuk verisi
	public static PetInfo? SelectedPet()
	{
		var s = Kumes.Store.Load<Settings>("settings.json");
		var id = s.Selected ?? s.Accounts.FirstOrDefault();
		return id == null ? null : Kumes.Store.Load<PetCache>(Path.Combine("accounts", id, "pet-cache.json")).Pet;
	}
}

// Giriş sırasında geçici depo: hesap adı Steam'den gelince oturum o hesabın klasörüne yazılır
sealed class MemorySessionStore : ISessionStore
{
	SavedSession? session;
	public bool Exists => session != null;
	public SavedSession? Load() => session;
	public void Save(SavedSession s) => session = s;
	public void Clear(string reason) => session = null;
}
