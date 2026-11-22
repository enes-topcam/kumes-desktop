// Tavuğun evreye göre şekli (CS2 modellerindeki "procedural_geometry_poses"): kafa, kuyruk, bacak, kanat, boyun,
// şişmanlık, sakal ve ibik birer özellik; her biri modelde iki tek karelik pozla (…_min, …_max) tanımlı. Pozlar kemik
// başına FARK tutar (konum eklenir, dönüş ve ölçek çarpılır). Özelliğin 0..1 değeri iki poz arasında karıştırılır.
// Değer aralığı evreye göre: "adolescent" (2. evre, piliç) büyük kafa, kısa kuyruk ve küçük sakal. Oyunda bir de "adult"
// (3. evre) aralığı var; Kümes 3. evreyi modelin kendi haliyle çiziyor (bkz. For).
// Aralıklar üç modelde de aynı (22 Kasım 2026, vmdl DATA). Oyun her tavuğa aralığın içinden kendi değerini veriyor;
// nasıl seçtiği dosyalarda yok, burada tavuğun seed'inden sabit bir değer türetilir (oyundakiyle birebir olmayabilir).
using System.Numerics;
using Kumes.Steam;

namespace Kumes.Render;

sealed class PoseShape
{
	public readonly Vector3[] T;
	public readonly Quaternion[] R;
	public readonly Vector3[] S;
	// Şekil bacak ve boynu uzatınca tavuk hem uzar hem ayakları zeminin altına iner. Masaüstündeki tavuk penceresi
	// şekilsiz modelin boyuna göre: toplam boy Scale ile ona (2. evrede biraz altına) getirilir, oranlar (büyük kafa,
	// kısa kuyruk…) kalır; GroundLift ayakları zemine oturtur (model birimi, dünya Y, ölçekten sonra).
	public float Scale { get; private set; } = 1;
	public float GroundLift { get; private set; }

	// özellik -> pozların adı (ibik ön ve arka iki ayrı poz, ayarlarda tek "comb")
	static readonly (string Name, string[] Seqs)[] Characteristics =
	[
		("fatness", ["chicken_hips"]),
		("tail", ["chicken_tail_scale"]),
		("legs", ["chicken_legs"]),
		("wing_size", ["chicken_wing_size"]),
		("wing_width", ["chicken_wing_width"]),
		("neck", ["chicken_neck"]),
		("head", ["chicken_head"]),
		("wattle", ["chicken_waddle"]),
		("comb", ["chicken_comb_front", "chicken_comb_back"]),
	];

	static readonly Dictionary<string, (float Min, float Max)> Adolescent = new()
	{
		["fatness"] = (0, 0.5f), ["tail"] = (0, 0.5f), ["legs"] = (0.5f, 1), ["wing_size"] = (0, 1), ["wing_width"] = (0, 1),
		["neck"] = (0, 1), ["head"] = (0.7f, 0.8f), ["comb"] = (0, 0.2f), ["wattle"] = (0, 0.2f),
	};

	// Dışa aktarılacak poz adları (AssetManager.Animations'a eklenir)
	public static IEnumerable<string> SequenceNames =>
		Characteristics.SelectMany(c => c.Seqs).SelectMany(s => new[] { s + "_min", s + "_max" });

	public static bool IsPose(string name) =>
		!name.Contains('/') && name.StartsWith("chicken_", StringComparison.Ordinal)
		&& (name.EndsWith("_min", StringComparison.Ordinal) || name.EndsWith("_max", StringComparison.Ordinal));

	PoseShape(int n)
	{
		T = new Vector3[n];
		R = Enumerable.Repeat(Quaternion.Identity, n).ToArray();
		S = Enumerable.Repeat(Vector3.One, n).ToArray();
	}

	// Tavuğa göre şekil: sadece 2. evre (Piliç) şekillenir (ergen aralığı: iri kafa, küçük ibik ve sakal, kısa kuyruk).
	// 3. evre modelin kendi hali (kullanıcı kararı, 22 Kasım 2026: yetişkin aralığı uygulanınca boyun ve bacak fazla
	// uzuyordu, eski Kümes tavuğu oyundakine daha çok benziyor). Civciv, yumurta ve koleksiyon resimleri de şekilsiz.
	public static PoseShape? For(GlbModel model, PetInfo? pet)
	{
		if (model.Poses.Count == 0) return null; // eski çıkarılmış model: pozlar yok
		if (pet == null || pet.Kind == "chick" || pet.StageName != "Piliç") return null;
		var ranges = Adolescent;

		uint seed = pet?.Seed ?? (pet?.Name is { } nm ? (uint)StableHash(nm) : 0);
		var values = new Dictionary<string, float>();
		for (int i = 0; i < Characteristics.Length; i++)
		{
			var name = Characteristics[i].Name;
			if (!ranges.TryGetValue(name, out var r)) continue;
			float u = Hash01(seed, (uint)i);
			values[name] = r.Min + (r.Max - r.Min) * u;
		}
		return Build(model, values, 0.9f); // 2. evre 3. evreden biraz küçük
	}

	public static PoseShape Build(GlbModel model, IReadOnlyDictionary<string, float> values, float heightFactor = 1)
	{
		int n = model.Skeleton.Count;
		var shape = new PoseShape(n);
		var minT = new Vector3[n]; var maxT = new Vector3[n];
		var minR = new Quaternion[n]; var maxR = new Quaternion[n];
		var minS = new Vector3[n]; var maxS = new Vector3[n];
		int rootMotion = Array.IndexOf(model.Skeleton.Names, "root_motion");
		foreach (var (name, seqs) in Characteristics)
		{
			if (!values.TryGetValue(name, out var v)) continue;
			foreach (var seq in seqs)
			{
				if (!model.Poses.TryGetValue(seq + "_min", out var lo) || !model.Poses.TryGetValue(seq + "_max", out var hi)) continue;
				First(lo, n, rootMotion, minT, minR, minS);
				First(hi, n, rootMotion, maxT, maxR, maxS);
				for (int b = 0; b < n; b++)
				{
					shape.T[b] += Vector3.Lerp(minT[b], maxT[b], v);
					// aynı dönüş iki işaretle yazılmış olabilir ((0,0,0,1) ve (0,0,0,-1)): karıştırmadan önce aynı yarıya al
					var hiR = Quaternion.Dot(minR[b], maxR[b]) < 0 ? Quaternion.Negate(maxR[b]) : maxR[b];
					shape.R[b] = Quaternion.Normalize(shape.R[b] * Quaternion.Slerp(minR[b], hiR, v));
					shape.S[b] *= Vector3.Lerp(minS[b], maxS[b], v);
				}
			}
		}
		var (baseFeet, baseTop) = Extent(model, null);
		var (feet, top) = Extent(model, shape);
		shape.Scale = top - feet > 1e-4f ? (baseTop - baseFeet) / (top - feet) * heightFactor : 1;
		if (Environment.GetEnvironmentVariable("KUMES_SHAPE_RAW") == "1") shape.Scale = 1; // geliştirme: oyundaki ham boy
		shape.GroundLift = baseFeet - feet * shape.Scale;
		return shape;
	}

	// Dinlenme pozunda (ölçek ve kaydırma olmadan) ayakların (foot/talon) en alçak ve kemiklerin en yüksek noktası
	static (float Feet, float Top) Extent(GlbModel model, PoseShape? shape)
	{
		float scale = shape?.Scale ?? 1, lift = shape?.GroundLift ?? 0;
		if (shape != null) { shape.Scale = 1; shape.GroundLift = 0; }
		var mixer = new AnimationMixer(model.Skeleton) { Shape = shape };
		mixer.Update(0, Matrix4x4.Identity);
		if (shape != null) { shape.Scale = scale; shape.GroundLift = lift; }
		float feet = float.MaxValue, top = float.MinValue;
		var names = model.Skeleton.Names;
		for (int i = 0; i < names.Length; i++)
		{
			float y = mixer.World[i].Translation.Y;
			top = Math.Max(top, y);
			if (names[i].StartsWith("foot", StringComparison.Ordinal) || names[i].StartsWith("talon", StringComparison.Ordinal))
				feet = Math.Min(feet, y);
		}
		return (feet == float.MaxValue ? 0 : feet, top == float.MinValue ? 0 : top);
	}

	// Pozun ilk karesi (kemik başına fark; kanalı olmayan kemikte fark yok)
	// root_motion: pozlarda koordinat çevirisini mutlak tutuyor (fark değil), atlanır; yoksa bütün tavuk döner
	static void First(Clip clip, int n, int skip, Vector3[] t, Quaternion[] r, Vector3[] s)
	{
		Array.Fill(t, Vector3.Zero);
		Array.Fill(r, Quaternion.Identity);
		Array.Fill(s, Vector3.One);
		foreach (var c in clip.Channels)
		{
			if ((uint)c.Node >= (uint)n || c.Node == skip) continue;
			var v = c.Values;
			switch (c.Path)
			{
				case 0 when v.Length >= 3: t[c.Node] = new Vector3(v[0], v[1], v[2]); break;
				case 1 when v.Length >= 4: r[c.Node] = Quaternion.Normalize(new Quaternion(v[0], v[1], v[2], v[3])); break;
				case 2 when v.Length >= 3: s[c.Node] = new Vector3(v[0], v[1], v[2]); break;
			}
		}
	}

	static int StableHash(string s)
	{
		unchecked
		{
			int h = (int)2166136261;
			foreach (char ch in s) h = (h ^ ch) * 16777619;
			return h;
		}
	}

	// seed + özellik sırasından 0..1 (splitmix)
	static float Hash01(uint seed, uint i)
	{
		ulong z = ((ulong)seed << 32 | i) + 0x9E3779B97F4A7C15UL;
		z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
		z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
		z ^= z >> 31;
		return (float)((z >> 40) / (double)(1UL << 24));
	}
}
