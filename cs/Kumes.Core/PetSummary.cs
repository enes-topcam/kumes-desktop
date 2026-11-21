// Tavuk verisinden ekranda gösterilen değerler: yem kalan süresi ve oranı, sonraki evre, büyüme oranı, yaş.
// Masaüstü penceresi, mobil uygulama ve widget'lar aynı hesabı kullanır.
using Kumes.Steam;

namespace Kumes;

public sealed record PetSummary(
	TimeSpan? FoodLeft,          // null: yem bilgisi yok
	float FoodFraction,          // 0..1, tam torba ≈ FullRationDays
	bool FoodUrgent,             // 1 günden az kaldı
	string? NextStage,           // "Piliç", "Tavuk"... (son evredeyse null)
	TimeSpan? NextStageLeft,
	float GrowthFraction,
	int? AgeDays,
	DateTimeOffset? HatchedAt)
{
	public const float FullRationDays = 35; // bir torba yem ≈ 35 gün (ölçüm: 13 Kas -> 20 Ara 2026)

	public static readonly string[] Stages = ["Yumurta", "Civciv", "Piliç", "Tavuk"];

	static readonly Dictionary<string, string> StageNext = new() { ["Yumurta"] = "Civciv", ["Civciv"] = "Piliç", ["Piliç"] = "Tavuk" };

	public static PetSummary From(PetInfo p, DateTimeOffset now)
	{
		TimeSpan? food = p.FoodExpiresAt is { } f ? f - now : null;
		StageNext.TryGetValue(p.StageName ?? "", out var next);
		TimeSpan? nextLeft = p.NextUpgradeAt is { } n ? n - now : null;
		float growth = 1;
		if (p.NextUpgradeAt is { } nu)
		{
			var start = p.HatchedAt ?? nu - TimeSpan.FromDays(30);
			var span = nu - start;
			growth = span > TimeSpan.Zero ? (float)Math.Clamp((now - start) / span, 0, 1) : 0;
		}
		return new PetSummary(
			food,
			food is { } l ? (float)Math.Clamp(l.TotalDays / FullRationDays, 0, 1) : 0,
			food is { } u && u <= TimeSpan.FromDays(1),
			next,
			nextLeft,
			growth,
			p.HatchedAt is { } h ? Math.Max(0, (int)(now - h).TotalDays) : null,
			p.HatchedAt);
	}
}
