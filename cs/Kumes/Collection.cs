// Tavuk portreleri (Koleksiyon listesi): takip edilen her tavuğun ırk/rengi (modellerdeki "style" grupları) bir kere
// gerçek 3D modelden çizilip %APPDATA%\KumesCS\collection altına PNG olarak kaydedilir; sonraki açılışlarda hazırdır.
// Sadece istenen (Want) renkler çizilir. Çizim ana iş parçacığında (OpenGL) adım adım; model dosyalarını okumak arka planda.
using System.Numerics;
using Kumes.Native;
using Kumes.Render;
using Kumes.Steam;
using StbImageSharp;

namespace Kumes;

sealed record CollectionEntry(string Breed, string Coat, int Style, string Label, string Path);

sealed unsafe class Collection
{
	public const int ThumbSize = 128;
	static readonly string[] Breeds = ["silkie", "catalana", "polish"];
	public static readonly Dictionary<string, string> BreedNames = new() { ["chick"] = "Civciv", ["silkie"] = "Silkie", ["catalana"] = "Catalana", ["polish"] = "Polish" };

	readonly GlContext gl;
	readonly AssetSet set;
	public readonly List<CollectionEntry> Entries = new();
	readonly Dictionary<string, GlbModel> models = new();
	Task<GlbModel>? loading;
	string? loadingBreed;
	readonly Dictionary<string, byte[]> thumbs = new(); // yüklenmiş küçük resimler (düz alfa BGRA)

	public string Dir => Store.File("collection");
	readonly HashSet<string> wanted = new(); // çizilmesi istenen renkler (takip edilen tavuklar)
	public bool Complete => !Entries.Any(e => wanted.Contains(e.Path) && !File.Exists(e.Path));

	// Bu tavukların portreleri çizilsin
	public void Want(IEnumerable<PetInfo?> pets)
	{
		foreach (var p in pets)
			if (EntryFor(p) is { } e) wanted.Add(e.Path);
	}

	public CollectionEntry? EntryFor(PetInfo? pet) => pet is { Found: true } ? Entries.FirstOrDefault(e => Owns(e, pet)) : null;
	public byte[]? ThumbFor(PetInfo? pet) => EntryFor(pet) is { } e ? Thumb(e) : null;

	public Collection(GlContext gl, AssetSet set)
	{
		this.gl = gl;
		this.set = set;
		Directory.CreateDirectory(Dir);
		var m = set.Manifest.RootElement;
		var stamp = m.TryGetProperty("extractedAt", out var ea) ? ea.ToString() : "0"; // modeller yeniden çıkarılınca resimler de yenilensin
		// civciv: renk grubu yok, oyunda tek renk (sarı); her tavuk önce civcivdir
		if (m.GetProperty("coats").TryGetProperty("chick", out var cc))
			foreach (var c in cc.EnumerateObject())
				Entries.Add(new CollectionEntry("chick", c.Name, 0, Pretty(c.Name), Path.Combine(Dir, $"chick_{c.Name}_{stamp}.png")));
		if (!m.TryGetProperty("styles", out var styles)) return;
		foreach (var breed in Breeds)
		{
			if (!styles.TryGetProperty(breed, out var bs)) continue;
			foreach (var st in bs.EnumerateObject().OrderBy(o => int.TryParse(o.Name, out var n) ? n : 99))
			{
				var coat = st.Value.GetString()!;
				if (!int.TryParse(st.Name, out var style)) continue;
				Entries.Add(new CollectionEntry(breed, coat, style, Pretty(coat), Path.Combine(Dir, $"{breed}_{coat}_{stamp}.png")));
			}
		}
	}

	public static string Pretty(string key)
	{
		var w = key.Replace('_', ' ');
		return w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w;
	}

	// Bir adım iş yap (bir renk çiz ya da model yüklemesini başlat/bitir). Daha iş varsa true.
	public bool Step()
	{
		var next = Entries.FirstOrDefault(e => wanted.Contains(e.Path) && !File.Exists(e.Path));
		if (next == null) { models.Clear(); return false; } // bitti: modelleri bellekten bırak
		if (!models.TryGetValue(next.Breed, out var model))
		{
			if (loading == null)
			{
				var path = Path.Combine(set.Dir, set.Manifest.RootElement.GetProperty("models").GetProperty(next.Breed).GetString()!);
				loadingBreed = next.Breed;
				loading = Task.Run(() => GlbModel.Load(path));
				return true;
			}
			if (!loading.IsCompleted) return true;
			if (loading.IsCompletedSuccessfully) models[loadingBreed!] = loading.Result;
			else { Log.Write($"koleksiyon modeli yüklenemedi: {loading.Exception?.InnerException?.Message}"); Entries.RemoveAll(e => e.Breed == loadingBreed); }
			loading = null;
			return true;
		}
		try
		{
			var coat = set.Manifest.RootElement.GetProperty("coats").GetProperty(next.Breed).GetProperty(next.Coat);
			string? P(string k) => coat.TryGetProperty(k, out var v) && v.GetString() is string s ? Path.Combine(set.Dir, s) : null;
			using var r = new PetRenderer(gl.Gl, ThumbSize, model, new CoatTextures(P("color")!, P("normal"), P("ao"), P("furColor"), P("furAlpha")));
			r.UseHeroLighting();
			var mixer = new AnimationMixer(model.Skeleton);
			if (model.Clips.TryGetValue("chick_idle01", out var clip)) { var l = mixer.Play(clip, false); l.Weight = 1; l.Time = 0.5f; }
			mixer.Update(0, Matrix4x4.CreateRotationY(0.85f));
			var raw = new byte[ThumbSize * ThumbSize * 4];
			fixed (byte* p = raw) r.Render(mixer.JointMatrices, 0, ThumbSize / PetRenderer.CanvasCss, p);
			var img = Ui.IconMaker.Unpremultiply(raw, ThumbSize, flip: true);
			File.WriteAllBytes(next.Path, Snapshot.EncodePngBgra(img, ThumbSize, ThumbSize));
			thumbs[next.Path] = img;
		}
		catch (Exception e)
		{
			Log.Write($"koleksiyon resmi çizilemedi ({next.Breed}/{next.Coat}): {e.Message}");
			Entries.Remove(next);
		}
		return true;
	}

	// Hazırsa küçük resim (düz alfa BGRA, ThumbSize x ThumbSize)
	public byte[]? Thumb(CollectionEntry e)
	{
		if (thumbs.TryGetValue(e.Path, out var t)) return t;
		if (!File.Exists(e.Path)) return null;
		try
		{
			var img = ImageResult.FromMemory(File.ReadAllBytes(e.Path), ColorComponents.RedGreenBlueAlpha);
			var bgra = img.Data;
			for (int i = 0; i < bgra.Length; i += 4) (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
			thumbs[e.Path] = bgra;
			return bgra;
		}
		catch { return null; }
	}

	public void ReleaseThumbs() => thumbs.Clear();

	// Oyundaki tavuğun hangi renk olduğu
	public static bool Owns(CollectionEntry e, PetInfo? pet) =>
		e.Breed == "chick" ? pet is { Found: true, Kind: "chick" }
		: pet is { Found: true, Style: uint st, Breed: string b } && b.Equals(e.Breed, StringComparison.OrdinalIgnoreCase) && st == e.Style;
}
