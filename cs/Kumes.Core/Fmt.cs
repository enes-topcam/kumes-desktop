// Süre ve tarih biçimleri. Türkçe: "36 gün 18 saat", "18 Aralık, 20:19", "30 Eylül'de".
// İngilizce (Lang.English): "36 days 18 hours", "December 18, 20:19". Mobil sadece Türkçeyi kullanır.
using System.Globalization;

namespace Kumes;

public static class Fmt
{
	static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");
	static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
	static bool E => Lang.English;

	static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

	public static string Duration(TimeSpan t)
	{
		if (t <= TimeSpan.Zero) return E ? "none" : "bitti";
		if (E)
		{
			if (t.Days > 0) return $"{t.Days}d {t.Hours}h";
			if (t.Hours > 0) return $"{t.Hours}h {t.Minutes}m";
			return $"{t.Minutes}m";
		}
		if (t.Days > 0) return $"{t.Days}g {t.Hours}s";
		if (t.Hours > 0) return $"{t.Hours}s {t.Minutes}dk";
		return $"{t.Minutes}dk";
	}

	public static string Date(DateTimeOffset d) => E ? d.ToLocalTime().ToString("MMM d, HH:mm", En) : d.ToLocalTime().ToString("d MMM HH:mm", Tr);

	// pencere için uzun biçimler: "36 gün 18 saat", "18 Aralık, 20:19", "30 Eylül"
	public static string DurationLong(TimeSpan t)
	{
		if (t <= TimeSpan.Zero) return E ? "over" : "bitti";
		if (E)
		{
			if (t.Days > 0) return t.Hours > 0 ? $"{Plural(t.Days, "day", "days")} {Plural(t.Hours, "hour", "hours")}" : Plural(t.Days, "day", "days");
			if (t.Hours > 0) return $"{Plural(t.Hours, "hour", "hours")} {Plural(t.Minutes, "minute", "minutes")}";
			return Plural(Math.Max(1, t.Minutes), "minute", "minutes");
		}
		if (t.Days > 0) return t.Hours > 0 ? $"{t.Days} gün {t.Hours} saat" : $"{t.Days} gün";
		if (t.Hours > 0) return $"{t.Hours} saat {t.Minutes} dakika";
		return $"{Math.Max(1, t.Minutes)} dakika";
	}

	// Kısa gün sayısı: "52 gün" / "52 days"
	public static string Days(int n) => E ? Plural(n, "day", "days") : $"{n} gün";

	public static string DateLong(DateTimeOffset d) => E ? d.ToLocalTime().ToString("MMMM d, HH:mm", En) : d.ToLocalTime().ToString("d MMMM, HH:mm", Tr);
	public static string Day(DateTimeOffset d) => E ? d.ToLocalTime().ToString("MMMM d", En) : d.ToLocalTime().ToString("d MMMM", Tr);

	// "30 Eylül'de", "5 Ocak'ta": ay adına göre doğru bulunma eki (ses uyumu + sert ünsüz)
	static readonly string[] MonthLocative = ["'ta", "'ta", "'ta", "'da", "'ta", "'da", "'da", "'ta", "'de", "'de", "'da", "'ta"];
	public static string DayLocative(DateTimeOffset d) => E ? "on " + Day(d) : Day(d) + MonthLocative[d.ToLocalTime().Month - 1];

	// İlgi eki: "Davuk'un", "Kiraz'ın", "Şeker'in", "Tombi'nin" (son ünlüye göre; ünlüyle bitiyorsa n kaynaştırma)
	public static string Genitive(string name)
	{
		if (E) return name.EndsWith('s') ? name + "'" : name + "'s";
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
		if (day == DateTime.Today) return $"{(E ? "Today" : "Bugün")}, {l:HH:mm}";
		if (day == DateTime.Today.AddDays(-1)) return $"{(E ? "Yesterday" : "Dün")}, {l:HH:mm}";
		return E ? l.ToString("MMMM d, HH:mm", En) : l.ToString("d MMMM, HH:mm", Tr);
	}

	public static bool IsJustNow(DateTimeOffset d) => DateTimeOffset.Now - d < TimeSpan.FromMinutes(1);

	public static string Ago(DateTimeOffset d)
	{
		var diff = DateTimeOffset.Now - d;
		if (E)
		{
			if (diff < TimeSpan.FromMinutes(1)) return "just now";
			if (diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes} min ago";
			if (diff < TimeSpan.FromDays(1)) return $"{Plural((int)diff.TotalHours, "hour", "hours")} ago";
			return $"{Plural((int)diff.TotalDays, "day", "days")} ago";
		}
		if (diff < TimeSpan.FromMinutes(1)) return "az önce";
		if (diff < TimeSpan.FromHours(1)) return $"{(int)diff.TotalMinutes} dk önce";
		if (diff < TimeSpan.FromDays(1)) return $"{(int)diff.TotalHours} saat önce";
		return $"{(int)diff.TotalDays} gün önce";
	}
}
