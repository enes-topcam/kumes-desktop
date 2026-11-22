// Arayüz dili: Türkçe yazı anahtardır, İngilizce karşılığı sözlükte durur (yoksa Türkçe gösterilir).
// Varsayılan Türkçe; mobil English'i hiç açmadığı için etkilenmez. Masaüstü kendi yazılarını Add ile ekler.
//   T("Ana Sayfa")                          -> "Home"
//   Tf("{0} hesabı", name)                  -> "{0}'s account" biçimiyle doldurulur
namespace Kumes;

public static class Lang
{
	public static bool English { get; set; }

	static readonly Dictionary<string, string> En = new()
	{
		// ortak kod: tavuk evreleri (PetInfo.StageName Türkçe kalır, ekranda çevrilir)
		["Yumurta"] = "Egg",
		["Civciv"] = "Chick",
		["Piliç"] = "Young chicken",
		["Tavuk"] = "Chicken",
		["Tavuğum"] = "My chicken",

		// ortak kod: Steam hataları (SteamException.Message ekranda gösterilir)
		["Steam girişi gerekiyor."] = "Steam sign-in required.",
		["Steam oturumunun süresi dolmuş, tekrar giriş yap."] = "Your Steam session expired. Sign in again.",
		["Steam çok fazla deneme yapıldığını söylüyor."] = "Steam says there were too many attempts.",
		["Steam hatası: {0}"] = "Steam error: {0}",
		["CS2 başka bir yerde açık; kapanınca güncellenecek."] = "CS2 is open somewhere else. It will update when CS2 closes.",
		["Steam'de başka bir oyun açık; kapanınca güncellenecek."] = "Another game is open on Steam. It will update when that game closes.",
		["Steam oturumu kapattı: {0}"] = "Steam closed the session: {0}",
		["Veri okunamadı: {0}"] = "Could not read the data: {0}",
		["Steam bağlantısı koptu."] = "Lost the connection to Steam.",
		["CS2 sunucusuna bağlanılamadı (zaman aşımı)."] = "Could not reach the CS2 server (timed out).",
		["Steam'e bağlanılamadı."] = "Could not connect to Steam.",
		["Kullanıcı adı ya da şifre yanlış."] = "Wrong username or password.",
		["Çok fazla deneme yapıldı, biraz bekleyip tekrar dene."] = "Too many attempts. Wait a bit and try again.",
		["Giriş isteğinin süresi doldu, tekrar dene."] = "The sign-in request expired. Try again.",
		["Giriş onaylanmadı (zaman aşımı)."] = "The sign-in was not approved (timed out).",

		// ortak kod: CS2 dosyası indirme (Cs2Download)
		["Steam girişi olmadı: {0}"] = "Steam sign-in failed: {0}",
		["Steam içerik sunucusu bulunamadı."] = "No Steam content server found.",
		["CS2 dosyaları Steam deposunda bulunamadı."] = "CS2 files weren't found in the Steam depot.",
		["Manifest indirilemedi: {0}"] = "Couldn't download the file list: {0}",
		["CS2 dosya dizini okunamadı (beklenmeyen VPK biçimi)."] = "Couldn't read the CS2 file index (unexpected VPK format).",
		["Depoda yok: {0}"] = "Not in the depot: {0}",
		["Dosya parçası indirilemedi: {0}"] = "Couldn't download a file chunk: {0}",
	};

	public static void Add(IEnumerable<KeyValuePair<string, string>> entries)
	{
		foreach (var (k, v) in entries) En[k] = v;
	}

	public static string T(string tr) => English && En.TryGetValue(tr, out var e) ? e : tr;
	public static string Tf(string tr, params object?[] args) => string.Format(T(tr), args);

	// Sözlükte karşılığı olmayan anahtarlar (geliştirme denetimi için)
	public static bool Has(string tr) => En.ContainsKey(tr);
}
