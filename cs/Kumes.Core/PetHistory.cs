// Tavuk ölçüm geçmişi: her güncellemede görülen durum ve ondan çıkan gerçek beslenmeler (İstatistikler).
using Kumes.Steam;

namespace Kumes;

// Bir güncellemede görülen durum. Yem bitişi belirgin uzadıysa arada beslenmiş demektir.
public sealed class FoodSample
{
	public DateTimeOffset At { get; set; }
	public DateTimeOffset? FoodExpiresAt { get; set; }
	public DateTimeOffset? NextUpgradeAt { get; set; }
	public string? Stage { get; set; }
}

// Geçmişten çıkan gerçek ölçümler (tahmin yok: sadece Kümes'in gördüğü güncellemeler)
public sealed record Feeding(DateTimeOffset SeenAt, TimeSpan Added);

public static class PetStats
{
	public static List<Feeding> Feedings(List<FoodSample> h)
	{
		var list = new List<Feeding>();
		for (int i = 1; i < h.Count; i++)
			if (h[i - 1].FoodExpiresAt is DateTimeOffset a && h[i].FoodExpiresAt is DateTimeOffset b && b - a > TimeSpan.FromHours(12))
				list.Add(new Feeding(h[i].At, b - a));
		return list;
	}

	// Yeni ölçüm: yem bitişi ya da aşama değiştiyse, ya da son ölçüm 6 saatten eskiyse kaydet
	public static bool Add(List<FoodSample> h, PetInfo p)
	{
		if (!p.Found) return false;
		var last = h.Count > 0 ? h[^1] : null;
		if (last != null && last.FoodExpiresAt == p.FoodExpiresAt && last.Stage == p.StageName && p.FetchedAt - last.At < TimeSpan.FromHours(6)) return false;
		h.Add(new FoodSample { At = p.FetchedAt, FoodExpiresAt = p.FoodExpiresAt, NextUpgradeAt = p.NextUpgradeAt, Stage = p.StageName });
		if (h.Count > 3000) h.RemoveRange(0, h.Count - 3000);
		return true;
	}
}
