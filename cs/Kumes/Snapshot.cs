// Geliştirme aracı: tek bir animasyon karesini pencere açmadan PNG olarak kaydeder.
// three.js sürümüyle (pet3d.js) görüntüyü karşılaştırmak için kullanılır.
//   Kumes.exe --snapshot <çıktı.png> <klip> <saniye> <yaw> [scale]
using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using Kumes.Native;
using Kumes.Render;
using Kumes.Steam;

namespace Kumes;

static unsafe class Snapshot
{
	// Arka plan (ön-çarpılmış alfa bunun üstüne bindirilir)
	const byte BgR = 0x1a, BgG = 0x1c, BgB = 0x20;

	public static void Run(string[] args)
	{
		string outPath = args[1], clipName = args[2];
		float time = float.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
		float yaw = float.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
		float scale = args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 1;
		int size = (int)MathF.Round(PetRenderer.CanvasCss * scale);

		var set = AssetManager.LoadBest() ?? throw new FileNotFoundException("CS2 modelleri yok");
		// 7. argüman "chick": kendi tavuğun yerine civciv modelini çiz (test için)
		var pet = args.Length > 6 && args[6] == "chick"
			? new PetInfo(true, Kind: "chick", StageName: "Civciv")
			: DesktopAccount.SelectedPet() ?? Store.ImportElectronPet();
		var (modelPath, coat) = AssetManager.Resolve(set, pet);
		using var gl = new GlContext();
		var model = GlbModel.Load(modelPath);
		using var renderer = new PetRenderer(gl.Gl, size, model, coat);
		var mixer = new AnimationMixer(model.Skeleton);
		if (!model.Clips.TryGetValue(clipName, out var clip)) throw new ArgumentException("klip yok: " + clipName);
		var layer = mixer.Play(clip, false);
		layer.Weight = 1;
		layer.Time = time;
		mixer.Update(0, Matrix4x4.CreateRotationY(yaw));

		var bits = new byte[size * size * 4];
		fixed (byte* p = bits) renderer.Render(mixer.JointMatrices, 0, scale, p); // gölge yok: three.js tarafı da çizmiyor

		// BGRA alttan üste, ön-çarpılmış -> RGB üstten alta, arka plana bindirilmiş
		var rgb = new byte[size * size * 3];
		for (int y = 0; y < size; y++)
		{
			int src = (size - 1 - y) * size * 4;
			for (int x = 0; x < size; x++)
			{
				int s = src + x * 4, d = (y * size + x) * 3;
				int a = bits[s + 3], inv = 255 - a;
				rgb[d] = (byte)Math.Min(255, bits[s + 2] + BgR * inv / 255);
				rgb[d + 1] = (byte)Math.Min(255, bits[s + 1] + BgG * inv / 255);
				rgb[d + 2] = (byte)Math.Min(255, bits[s] + BgB * inv / 255);
			}
		}
		File.WriteAllBytes(outPath, EncodePng(rgb, size, size));
	}

	// Her klibin tavuğu ne kadar yükselttiğini / kaydırdığını ölçer (px, 160 px'lik pencere ölçeğinde).
	//   Kumes.exe --clip-report <model.glb> <çıktı.txt>
	public static void ClipReport(string glb, string outPath)
	{
		var model = GlbModel.Load(glb);
		var sk = model.Skeleton;
		var joints = sk.Joints;
		float px = PetRenderer.PxPerUnitCss;
		var sb = new System.Text.StringBuilder();
		sb.AppendLine("klip                        süre   en_yüksek  en_alçak  yatay_kayma  (px; pencere tavan ~154)");
		foreach (var (name, clip) in model.Clips.OrderBy(c => c.Key))
		{
			var mixer = new AnimationMixer(sk);
			var layer = mixer.Play(clip, false);
			layer.Weight = 1;
			float maxY = float.MinValue, minY = float.MaxValue, minX = float.MaxValue, maxX = float.MinValue;
			for (int i = 0; i <= 60; i++)
			{
				layer.Time = clip.Duration * i / 60f;
				mixer.Update(0, Matrix4x4.CreateRotationY(MathF.PI / 2)); // yandan: x = ileri/geri
				foreach (int j in joints)
				{
					var p = mixer.World[j].Translation;
					maxY = Math.Max(maxY, p.Y); minY = Math.Min(minY, p.Y);
					minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
				}
			}
			sb.AppendLine($"{name,-27} {clip.Duration,5:F2}  {maxY * px,9:F0}  {minY * px,8:F0}  {(maxX - minX) * px,11:F0}");
		}

		// Büyük zıplamalar: kök kemiğinin (root) yolu, 3/4 görünümde (yaw 1.12) ekran px'i olarak
		int root = Array.IndexOf(sk.Names, "root");
		sb.AppendLine();
		sb.AppendLine("kök kemiği yolu (x sağa, y yukarı; px)");
		foreach (var name in new[] { "chick_trick02", "chick_trick05", "chick_trick07", "chick_idle01" })
		{
			if (!model.Clips.TryGetValue(name, out var clip)) continue;
			var mixer = new AnimationMixer(sk);
			var layer = mixer.Play(clip, false);
			layer.Weight = 1;
			var line = new System.Text.StringBuilder($"{name,-15}");
			for (int i = 0; i <= 10; i++)
			{
				layer.Time = clip.Duration * i / 10f;
				mixer.Update(0, Matrix4x4.CreateRotationY(MathF.PI / 2 - 0.45f));
				var p = mixer.World[root].Translation * px;
				line.Append($" ({p.X:F0},{p.Y:F0})");
			}
			sb.AppendLine(line.ToString());
		}
		// trick02 gövde yüksekliği ince örnekleme (son üçte birlik)
		if (model.Clips.TryGetValue("chick_trick02", out var t2))
		{
			var mixer = new AnimationMixer(sk);
			var layer = mixer.Play(t2, false);
			layer.Weight = 1;
			var line = new System.Text.StringBuilder("trick02 gövde y (t=sn:px):");
			for (float t = t2.Duration * 0.6f; t <= t2.Duration + 1e-3f; t += t2.Duration / 60f)
			{
				layer.Time = t;
				mixer.Update(0, Matrix4x4.Identity);
				line.Append($" {t:F2}:{mixer.World[root].Translation.Y * px:F0}");
			}
			sb.AppendLine();
			sb.AppendLine(line.ToString());
		}

		// Klibin son karesi, normal duruştan (idle01 başı) ne kadar farklı? (kemik dönüş farkı, derece)
		sb.AppendLine();
		sb.AppendLine("son kare ile idle01 arasındaki fark (kemik dönüşü, derece: ortalama / en büyük [kemik]) + gövde yönü farkı");
		if (model.Clips.TryGetValue("chick_idle01", out var idle))
		{
			var basePose = Pose(sk, idle, 0);
			foreach (var (name, clip) in model.Clips.OrderBy(c => c.Key))
			{
				if (!name.Contains("trick") && !name.Contains("react")) continue;
				var end = Pose(sk, clip, clip.Duration);
				float sum = 0, max = 0; int maxJ = 0;
				foreach (int j in joints)
				{
					float a = AngleDeg(basePose[j], end[j]);
					sum += a;
					if (a > max) { max = a; maxJ = j; }
				}
				int rootIdx = Array.IndexOf(sk.Names, "root");
				sb.AppendLine($"{name,-15} ort {sum / joints.Length,5:F1}°  en büyük {max,5:F1}° [{sk.Names[maxJ]}]  gövde {AngleDeg(basePose[rootIdx], end[rootIdx]),5:F1}°");
			}
		}
		File.WriteAllText(outPath, sb.ToString());
	}

	// Uygulamadaki akışın aynısı: PetAnimator ile klibi oynatır, bitince normal duruşa geçer; 40 fps'de
	// [from, to] saniye aralığındaki kareleri tek bir şerit PNG'ye dizer (zıplamada pencere takibi dahil).
	//   Kumes.exe --sequence <çıktı.png> <klip> <from> <to> [her_kaçıncı_kare]
	public static void Sequence(string[] args)
	{
		string outPath = args[1], clipName = args[2];
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		float from = float.Parse(args[3], ci), to = float.Parse(args[4], ci);
		int every = args.Length > 5 ? int.Parse(args[5]) : 2;
		int size = (int)PetRenderer.CanvasCss;
		var set = AssetManager.LoadBest() ?? throw new FileNotFoundException("CS2 modelleri yok");
		var (modelPath, coat) = AssetManager.Resolve(set, DesktopAccount.SelectedPet());
		using var gl = new GlContext();
		var model = GlbModel.Load(modelPath);
		using var renderer = new PetRenderer(gl.Gl, size, model, coat);
		var anim = new PetAnimator(model);
		const float dt = 1 / 40f;
		for (int i = 0; i < 40; i++) anim.Update(new AnimState("idle1", 1, 1, dt)); // önce normal duruşa otursun
		anim.PlayTest(clipName);

		int tall = size + 320; // zıplama payı
		var frames = new List<byte[]>();
		var bits = new byte[size * size * 4];
		for (float t = 0; t <= to; t += dt)
		{
			anim.Update(new AnimState("idle1", 1, 1, dt));
			int frame = (int)MathF.Round(t / dt);
			if (t < from || frame % every != 0) continue;
			int jump = (int)MathF.Round(anim.JumpHeight() * PetRenderer.PxPerUnitCss);
			fixed (byte* p = bits) renderer.Render(anim.Mixer.JointMatrices, 0, 1, p, jump);
			// uzun bir sütuna, zıplama kadar yukarıya yerleştir (ekranda göründüğü gibi)
			var col = new byte[size * tall * 3];
			for (int i = 0; i < col.Length; i += 3) { col[i] = BgR; col[i + 1] = BgG; col[i + 2] = BgB; }
			for (int y = 0; y < size; y++)
			{
				int dy = tall - size + y - jump;
				if (dy < 0 || dy >= tall) continue;
				for (int x = 0; x < size; x++)
				{
					int s = ((size - 1 - y) * size + x) * 4, d = (dy * size + x) * 3;
					int a = bits[s + 3], inv = 255 - a;
					col[d] = (byte)Math.Min(255, bits[s + 2] + BgR * inv / 255);
					col[d + 1] = (byte)Math.Min(255, bits[s + 1] + BgG * inv / 255);
					col[d + 2] = (byte)Math.Min(255, bits[s] + BgB * inv / 255);
				}
			}
			frames.Add(col);
		}
		// kareleri yan yana diz (her karenin altına ince bir zaman çizgisi yok; sırayla soldan sağa)
		int w = size * frames.Count;
		var sheet = new byte[w * tall * 3];
		for (int f = 0; f < frames.Count; f++)
			for (int y = 0; y < tall; y++)
				Array.Copy(frames[f], y * size * 3, sheet, (y * w + f * size) * 3, size * 3);
		File.WriteAllBytes(outPath, EncodePng(sheet, w, tall));
	}

	static Quaternion[] Pose(Skeleton sk, Clip clip, float t)
	{
		var mixer = new AnimationMixer(sk);
		var l = mixer.Play(clip, true);
		l.Weight = 1;
		l.Time = t;
		mixer.Update(0, Matrix4x4.Identity);
		return mixer.World.Select(m => Quaternion.CreateFromRotationMatrix(m)).ToArray();
	}

	static float AngleDeg(Quaternion a, Quaternion b)
	{
		float d = Math.Clamp(Math.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b))), 0, 1);
		return 2 * MathF.Acos(d) * 180 / MathF.PI;
	}

	// Düz alfa BGRA (üstten alta) -> RGBA PNG
	public static byte[] EncodePngBgra(byte[] bgra, int w, int h)
	{
		using var ms = new MemoryStream();
		ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
		var ihdr = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
		BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
		ihdr[8] = 8; ihdr[9] = 6; // 8 bit, RGBA
		Chunk(ms, "IHDR", ihdr);
		using (var raw = new MemoryStream())
		{
			using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
			{
				var row = new byte[w * 4];
				for (int y = 0; y < h; y++)
				{
					z.WriteByte(0);
					for (int x = 0; x < w; x++)
					{
						int i = (y * w + x) * 4;
						row[x * 4] = bgra[i + 2]; row[x * 4 + 1] = bgra[i + 1]; row[x * 4 + 2] = bgra[i]; row[x * 4 + 3] = bgra[i + 3];
					}
					z.Write(row);
				}
			}
			Chunk(ms, "IDAT", raw.ToArray());
		}
		Chunk(ms, "IEND", []);
		return ms.ToArray();
	}

	static byte[] EncodePng(byte[] rgb, int w, int h)
	{
		using var ms = new MemoryStream();
		ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
		var ihdr = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
		BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
		ihdr[8] = 8; ihdr[9] = 2; // 8 bit, RGB
		Chunk(ms, "IHDR", ihdr);
		using (var raw = new MemoryStream())
		{
			using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
				for (int y = 0; y < h; y++)
				{
					z.WriteByte(0);
					z.Write(rgb, y * w * 3, w * 3);
				}
			Chunk(ms, "IDAT", raw.ToArray());
		}
		Chunk(ms, "IEND", []);
		return ms.ToArray();
	}

	static void Chunk(Stream s, string type, byte[] data)
	{
		Span<byte> len = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
		s.Write(len);
		var td = new byte[4 + data.Length];
		System.Text.Encoding.ASCII.GetBytes(type, td);
		data.CopyTo(td, 4);
		s.Write(td);
		BinaryPrimitives.WriteUInt32BigEndian(len, Crc32(td));
		s.Write(len);
	}

	static uint Crc32(byte[] data)
	{
		uint c = 0xFFFFFFFF;
		foreach (var b in data)
		{
			c ^= b;
			for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
		}
		return ~c;
	}
}
