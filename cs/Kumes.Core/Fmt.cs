// Türkçe süre ve tarih biçimleri ("36 gün 18 saat", "18 Aralık, 20:19", "30 Eylül'de").
using System.Globalization;

namespace Kumes;

public static class Fmt
{
	static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

	public static string Duration(TimeSpan t)
	{
		if (t <= TimeSpan.Zero) return "bitti";
		if (t.Days > 0) return $"{t.Days}g {t.Hours}s";
		if (t.Hours > 0) return $"{t.Hours}s {t.Minutes}dk";
		return $"{t.Minutes}dk";
	}

	public static string Date(DateTimeOffset d) => d.ToLocalTime().ToString("d MMM HH:mm", Tr);

	// pencere için uzun biçimler: "36 gün 18 saat", "18 Aralık, 20:19", "30 Eylül"
	public static string DurationLong(TimeSpan t)
	{
		if (t <= TimeSpan.Zero) return "bitti";
		if (t.Days > 0) return t.Hours > 0 ? $"{t.Days} gün {t.Hours} saat" : $"{t.Days} gün";
		if (t.Hours > 0) return $"{t.Hours} saat {t.Minutes} dakika";
		return $"{Math.Max(1, t.Minutes)} dakika";
	}

	public static string DateLong(DateTimeOffset d) => d.ToLocalTime().ToString("d MMMM, HH:mm", Tr);
	public static string Day(DateTimeOffset d) => d.ToLocalTime().ToString("d MMMM", Tr);

	// "30 Eylül'de", "5 Ocak'ta": ay adına göre doğru bulunma eki (ses uyumu + sert ünsüz)
	static readonly string[] MonthLocative = ["'ta", "'ta", "'ta", "'da", "'ta", "'da", "'da", "'ta", "'de", "'de", "'da", "'ta"];
	public static string DayLocative(DateTimeOffset d) => Day(d) + MonthLocative[d.ToLocalTime().Month - 1];

	// İlgi eki: "Davuk'un", "Kiraz'ın", "Şeker'in", "Tombi'nin" (son ünlüye göre; ünlüyle bitiyorsa n kaynaştırma)
	public static string Genitive(string name)
	{
		const string vowels = "aıoueiöüAIOUEİÖÜ";
		char last = name.LastOrDefault(c => vowels.Contains(c));
		string v = char.ToLower(last, Tr) switch { 'a' or 'ı' => "ın", 'e' or 'i' => "in", 'o' or 'u' => "un", 'ö' or 'ü' => "ün", _ => "in" };
		bool endsVowel = name.Length > 0 && vowels.Contains(name[^1]);
		return $"{name}'{(endsVowel ? "n" : "")}{v}";
	}

	// Etkinlik zamanı: "Bugün, 20:19", "Dün, 09:41", "18 Aralık, 14:27"
	public static string When(DateTimeOffset d)
	{
		var l = d.ToLocalTime();
		var day = l.Date;
		if (day == DateTime.Today) return $"Bugün, {l:HH:mm}";
		if (day == DateTime.Today.AddDays(-1)) return $"Dün, {l:HH:mm}";
		return l.ToString("d MMMM, HH:mm", Tr);
	}

	public static string Ago(DateTimeOffset d)
	{
		var diff = DateTimeOffset.Now - d;
		if (diff < TimeSpan.FromMinutes(1)) return "az önce";
		if (diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes} dk önce";
		if (diff < TimeSpan.FromDays(1)) return $"{(int)diff.TotalHours} saat önce";
		return $"{(int)diff.TotalDays} gün önce";
	}
}
