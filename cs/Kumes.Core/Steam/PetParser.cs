// CS2 envanterinden tavuk verisini çıkarır (eski src/pet-parser.js'in karşılığı).
using System.Buffers.Binary;
using System.Text;
using Kumes.Steam.Proto;

namespace Kumes.Steam;

public record PetInfo(
	bool Found,
	string? ItemId = null,
	string? Name = null,
	uint? PetId = null,
	string? Breed = null,
	string? Kind = null,
	uint Level = 0,
	string? StageName = null,
	DateTimeOffset? FoodExpiresAt = null,
	DateTimeOffset? NextUpgradeAt = null,
	DateTimeOffset? HatchedAt = null,
	uint? Seed = null,
	uint? Style = null,
	int FeedCount = 0,
	int RewardFeedCount = 0)
{
	public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.Now;
}

public static class PetParser
{
	const uint DefPet = 4681, DefEgg = 4948, DefFeed = 4949;
	const uint AttrDeployment = 180, AttrUpgradeLevel = 268, AttrPetId = 296, AttrFoodExpiration = 303,
		AttrNextUpgrade = 304, AttrSeed = 313, AttrCustomName = 111, AttrCustomName2 = 328, AttrFreeReward = 277;

	static readonly Dictionary<uint, string> Breeds = new() { [3] = "Catalana", [4] = "Silkie", [5] = "Polish" };

	static byte[]? AttrBytes(CSOEconItem item, uint def)
	{
		var a = item.attribute.FirstOrDefault(x => x.def_index == def);
		if (a == null) return null;
		if (a.value_bytes is { Length: > 0 }) return a.value_bytes;
		var b = new byte[4];
		BinaryPrimitives.WriteUInt32LittleEndian(b, a.value);
		return b;
	}

	static uint? AttrUInt(CSOEconItem item, uint def)
	{
		var b = AttrBytes(item, def);
		return b is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32LittleEndian(b) : null;
	}

	static DateTimeOffset? AttrTime(CSOEconItem item, uint def)
	{
		var v = AttrUInt(item, def);
		return v is > 0 ? DateTimeOffset.FromUnixTimeSeconds(v.Value) : null;
	}

	// Attr 328: protobuf mesajı, alan 1 = string (0x0a <varint uzunluk> <utf8>)
	static string? DecodeProtoString(byte[]? buf)
	{
		if (buf is not { Length: >= 2 }) return null;
		if (buf[0] == 0x0a)
		{
			int len = 0, shift = 0, i = 1;
			while (i < buf.Length)
			{
				byte b = buf[i++];
				len |= (b & 0x7f) << shift;
				if ((b & 0x80) == 0) break;
				shift += 7;
			}
			len = Math.Min(len, buf.Length - i);
			var s = Encoding.UTF8.GetString(buf, i, len);
			return s.Length > 0 ? s : null;
		}
		var raw = Encoding.UTF8.GetString(buf).Replace("\0", "");
		return raw.Length > 0 ? raw : null;
	}

	static string? DecodeName(CSOEconItem item)
	{
		var n2 = DecodeProtoString(AttrBytes(item, AttrCustomName2));
		if (n2 != null) return n2;
		if (!string.IsNullOrEmpty(item.custom_name)) return item.custom_name;
		var n1 = AttrBytes(item, AttrCustomName);
		return n1 is { Length: > 2 } ? Encoding.UTF8.GetString(n1, 2, n1.Length - 2) : null;
	}

	static bool IsPetCandidate(CSOEconItem item)
	{
		if (item.def_index == DefFeed) return false;
		if (item.def_index is DefPet or DefEgg) return true;
		return item.attribute.Any(a => a.def_index == AttrPetId);
	}

	public static PetInfo Parse(IReadOnlyList<CSOEconItem> items)
	{
		var feed = items.Where(i => i.def_index == DefFeed).ToList();
		int rewardFeed = feed.Count(i => AttrUInt(i, AttrFreeReward) == 1);
		int feedCount = feed.Count - rewardFeed;

		var item = items.Where(IsPetCandidate).OrderBy(i => i.def_index == DefPet ? 0 : 1).FirstOrDefault();
		if (item == null) return new PetInfo(false, FeedCount: feedCount, RewardFeedCount: rewardFeed);

		var petId = AttrUInt(item, AttrPetId);
		var level = AttrUInt(item, AttrUpgradeLevel) ?? 0;
		var nextUpgrade = AttrTime(item, AttrNextUpgrade);

		string kind = item.def_index == DefEgg || petId == 1 ? "egg" : petId == 2 ? "chick" : "chicken";
		string stage = kind switch
		{
			"egg" => "Yumurta",
			"chick" => "Civciv",
			_ => level >= 3 || nextUpgrade == null ? "Tavuk" : "Piliç",
		};

		return new PetInfo(
			Found: true,
			ItemId: item.id.ToString(),
			Name: DecodeName(item) ?? "Tavuğum",
			PetId: petId,
			Breed: petId is uint p && Breeds.TryGetValue(p, out var br) ? br : null,
			Kind: kind,
			Level: level,
			StageName: stage,
			FoodExpiresAt: AttrTime(item, AttrFoodExpiration),
			NextUpgradeAt: nextUpgrade,
			HatchedAt: AttrTime(item, AttrDeployment),
			Seed: AttrUInt(item, AttrSeed),
			Style: item.style,
			FeedCount: feedCount,
			RewardFeedCount: rewardFeed);
	}
}
