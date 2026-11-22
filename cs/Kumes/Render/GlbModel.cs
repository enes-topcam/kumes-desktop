// Source 2 Viewer'ın çıkardığı .glb dosyasını okur: tek iskeletli mesh, kemikler ve animasyonlar.
// Genel amaçlı değil; bu dosyalarda kullanılan biçimleri (float, ushort eklem, uint indeks) destekler.
using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace Kumes.Render;

sealed class Channel
{
	public int Node;
	public int Path;          // 0 konum, 1 dönüş, 2 ölçek
	public float[] Times = [];
	public float[] Values = []; // konum/ölçek: 3, dönüş: 4 değer/kare
}

sealed class Clip
{
	public string Name = "";
	public float Duration;
	public Channel[] Channels = [];
}

sealed class Skeleton
{
	public int Count;
	public string[] Names = [];
	public int[] Parent = [];
	public int[] Order = [];   // ebeveyn her zaman çocuktan önce
	public Vector3[] RestT = [];
	public Quaternion[] RestR = [];
	public Vector3[] RestS = [];
	public int[] Joints = [];
	public Matrix4x4[] InverseBind = [];
}

sealed class MeshData
{
	public int VertexCount;
	public float[] Positions = [], Normals = [], Tangents = [], UVs = [], Weights = [];
	public ushort[] Joints = [];
	public uint[] Indices = [];
	// Parçalar (glTF primitive): her biri ayrı malzemeyle çizilir. Yetişkin tavukta 1, civcivde 2 (gövde + tüy katmanı).
	public (int Start, int Count)[] Parts = [];
}

sealed class GlbModel
{
	public Skeleton Skeleton = new();
	public MeshData Mesh = new();
	public Dictionary<string, Clip> Clips = new();
	// Evre şekli pozları (chicken_head_min/max…): tek karelik, kemik başına fark (PoseShape bunları karıştırır)
	public Dictionary<string, Clip> Poses = new();

	public static GlbModel Load(string path)
	{
		var bytes = File.ReadAllBytes(path);
		if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546C67) throw new InvalidDataException("glb değil");
		int jsonLen = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
		using var doc = JsonDocument.Parse(bytes.AsMemory(20, jsonLen));
		int binStart = 20 + jsonLen + 8;
		return new Reader(doc.RootElement, bytes.AsMemory(binStart)).Read();
	}

	sealed class Reader(JsonElement root, ReadOnlyMemory<byte> bin)
	{
		// Diziler bir kere listeye çevrilir: JsonElement'te nesne dizisine sıra numarasıyla erişmek baştan saymak demek
		// (binlerce animasyon kanalında karesel; 44 MB'lık modelde okuma ~3,3 sn sürüyordu)
		readonly JsonElement[] accessors = root.GetProperty("accessors").EnumerateArray().ToArray();
		readonly JsonElement[] views = root.GetProperty("bufferViews").EnumerateArray().ToArray();

		static int Comps(string type) => type switch
		{
			"SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
			_ => throw new InvalidDataException("tip: " + type),
		};

		(int count, int comps, int ctype, int offset, int stride) Info(int index)
		{
			var a = accessors[index];
			var v = views[a.GetProperty("bufferView").GetInt32()];
			int comps = Comps(a.GetProperty("type").GetString()!);
			int ctype = a.GetProperty("componentType").GetInt32();
			int csize = ctype switch { 5126 or 5125 => 4, 5123 or 5122 => 2, _ => 1 };
			int offset = (v.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
				+ (a.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
			int stride = v.TryGetProperty("byteStride", out var s) ? s.GetInt32() : comps * csize;
			return (a.GetProperty("count").GetInt32(), comps, ctype, offset, stride);
		}

		public float[] Floats(int index)
		{
			var (count, comps, ctype, offset, stride) = Info(index);
			var span = bin.Span;
			var r = new float[count * comps];
			for (int i = 0; i < count; i++)
			{
				int p = offset + i * stride;
				for (int c = 0; c < comps; c++)
				{
					r[i * comps + c] = ctype switch
					{
						5126 => BinaryPrimitives.ReadSingleLittleEndian(span[(p + c * 4)..]),
						5123 => BinaryPrimitives.ReadUInt16LittleEndian(span[(p + c * 2)..]) / 65535f,
						5121 => span[p + c] / 255f,
						_ => throw new InvalidDataException("float bileşeni: " + ctype),
					};
				}
			}
			return r;
		}

		public uint[] UInts(int index)
		{
			var (count, comps, ctype, offset, stride) = Info(index);
			var span = bin.Span;
			var r = new uint[count * comps];
			for (int i = 0; i < count; i++)
			{
				int p = offset + i * stride;
				for (int c = 0; c < comps; c++)
				{
					r[i * comps + c] = ctype switch
					{
						5125 => BinaryPrimitives.ReadUInt32LittleEndian(span[(p + c * 4)..]),
						5123 => BinaryPrimitives.ReadUInt16LittleEndian(span[(p + c * 2)..]),
						5121 => span[p + c],
						_ => throw new InvalidDataException("tamsayı bileşeni: " + ctype),
					};
				}
			}
			return r;
		}

		public GlbModel Read()
		{
			var m = new GlbModel();
			ReadNodes(m.Skeleton);
			ReadMesh(m);
			ReadAnimations(m);
			return m;
		}

		void ReadNodes(Skeleton sk)
		{
			var nodes = root.GetProperty("nodes").EnumerateArray().ToArray();
			int n = nodes.Length;
			sk.Count = n;
			sk.Names = new string[n];
			sk.Parent = Enumerable.Repeat(-1, n).ToArray();
			sk.RestT = new Vector3[n];
			sk.RestR = new Quaternion[n];
			sk.RestS = new Vector3[n];
			for (int i = 0; i < n; i++)
			{
				var node = nodes[i];
				sk.Names[i] = node.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
				sk.RestT[i] = Vector3.Zero;
				sk.RestR[i] = Quaternion.Identity;
				sk.RestS[i] = Vector3.One;
				if (node.TryGetProperty("matrix", out var mat))
				{
					var f = mat.EnumerateArray().Select(x => x.GetSingle()).ToArray();
					// glTF sütun-öncelikli dizi = System.Numerics'in satır-vektör matrisi
					var mm = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
					Matrix4x4.Decompose(mm, out var s, out var r, out var t);
					sk.RestT[i] = t; sk.RestR[i] = r; sk.RestS[i] = s;
				}
				if (node.TryGetProperty("translation", out var tr)) sk.RestT[i] = Vec3(tr);
				if (node.TryGetProperty("rotation", out var rot)) { var q = Vec4(rot); sk.RestR[i] = new Quaternion(q.X, q.Y, q.Z, q.W); }
				if (node.TryGetProperty("scale", out var sc)) sk.RestS[i] = Vec3(sc);
				if (node.TryGetProperty("children", out var ch))
					foreach (var c in ch.EnumerateArray()) sk.Parent[c.GetInt32()] = i;
			}
			var order = new List<int>(n);
			var visited = new bool[n];
			void Visit(int i)
			{
				if (visited[i]) return;
				if (sk.Parent[i] >= 0) Visit(sk.Parent[i]);
				visited[i] = true;
				order.Add(i);
			}
			for (int i = 0; i < n; i++) Visit(i);
			sk.Order = order.ToArray();

			var skin = root.GetProperty("skins")[0];
			sk.Joints = skin.GetProperty("joints").EnumerateArray().Select(x => x.GetInt32()).ToArray();
			var ibm = Floats(skin.GetProperty("inverseBindMatrices").GetInt32());
			sk.InverseBind = new Matrix4x4[sk.Joints.Length];
			for (int j = 0; j < sk.Joints.Length; j++)
			{
				var f = ibm.AsSpan(j * 16, 16);
				sk.InverseBind[j] = new Matrix4x4(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15]);
			}
		}

		void ReadMesh(GlbModel m)
		{
			// Tüm parçaları tek bir köşe/indeks tamponunda birleştir; her parçanın indeks aralığı Parts'ta
			var pos = new List<float>(); var nrm = new List<float>(); var tan = new List<float>(); var uv = new List<float>();
			var wgt = new List<float>(); var jnt = new List<ushort>(); var idx = new List<uint>();
			var parts = new List<(int, int)>();
			foreach (var prim in root.GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray())
			{
				var attr = prim.GetProperty("attributes");
				int Acc(string name) => attr.GetProperty(name).GetInt32();
				uint baseVertex = (uint)(pos.Count / 3);
				var p = Floats(Acc("POSITION"));
				pos.AddRange(p);
				nrm.AddRange(Floats(Acc("NORMAL")));
				tan.AddRange(attr.TryGetProperty("TANGENT", out _) ? Floats(Acc("TANGENT")) : new float[p.Length / 3 * 4]);
				uv.AddRange(Floats(Acc("TEXCOORD_0")));
				wgt.AddRange(Floats(Acc("WEIGHTS_0")));
				jnt.AddRange(UInts(Acc("JOINTS_0")).Select(x => (ushort)x));
				var ind = UInts(prim.GetProperty("indices").GetInt32());
				parts.Add((idx.Count, ind.Length));
				idx.AddRange(ind.Select(i => i + baseVertex));
			}
			var md = m.Mesh;
			md.Positions = pos.ToArray(); md.Normals = nrm.ToArray(); md.Tangents = tan.ToArray(); md.UVs = uv.ToArray();
			md.Weights = wgt.ToArray(); md.Joints = jnt.ToArray(); md.Indices = idx.ToArray();
			md.Parts = parts.ToArray();
			md.VertexCount = md.Positions.Length / 3;
		}

		void ReadAnimations(GlbModel m)
		{
			if (!root.TryGetProperty("animations", out var anims)) return;
			int rootMotion = Array.IndexOf(m.Skeleton.Names, "root_motion");
			int rootBone = Array.IndexOf(m.Skeleton.Names, "root");
			foreach (var anim in anims.EnumerateArray())
			{
				var full = anim.GetProperty("name").GetString() ?? "";
				bool pose = PoseShape.IsPose(full);
				// sadece dünya/arayüz animasyonları (birinci şahıs kopyalarını atla) ve evre şekli pozları
				if (!pose && !full.Contains("/world/") && !full.Contains("/ui/")) continue;
				if (pose && m.Poses.ContainsKey(full)) continue;
				var name = full[(full.LastIndexOf('/') + 1)..];
				if (m.Clips.ContainsKey(name)) continue;

				var samplers = anim.GetProperty("samplers").EnumerateArray().ToArray();
				var list = new List<Channel>();
				float duration = 0;
				foreach (var ch in anim.GetProperty("channels").EnumerateArray())
				{
					var target = ch.GetProperty("target");
					int path = target.GetProperty("path").GetString() switch { "translation" => 0, "rotation" => 1, "scale" => 2, _ => -1 };
					if (path < 0 || !target.TryGetProperty("node", out var nodeEl)) continue;
					var s = samplers[ch.GetProperty("sampler").GetInt32()];
					var c = new Channel
					{
						Node = nodeEl.GetInt32(),
						Path = path,
						Times = Floats(s.GetProperty("input").GetInt32()),
						Values = Floats(s.GetProperty("output").GetInt32()),
					};
					if (c.Times.Length > 0) duration = Math.Max(duration, c.Times[^1]);
					if (path == 0 && !pose) Sanitize(c, rootMotion, rootBone);
					CollapseConstant(c);
					list.Add(c);
				}
				var clip = new Clip { Name = pose ? full : name, Duration = duration, Channels = list.ToArray() };
				if (pose) m.Poses[full] = clip;
				else m.Clips[name] = clip;
			}
		}

		// Animasyonların modeli yerinden oynatmasını engelle (pet3d.js sanitizeClip ile aynı):
		// root_motion konumunu sıfırla; root yatayda belirgin kayıyorsa x/y'yi ilk karede sabitle.
		static void Sanitize(Channel c, int rootMotion, int rootBone)
		{
			var v = c.Values;
			if (c.Node == rootMotion)
			{
				Array.Clear(v);
			}
			else if (c.Node == rootBone)
			{
				float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
				for (int i = 0; i < v.Length; i += 3)
				{
					minX = Math.Min(minX, v[i]); maxX = Math.Max(maxX, v[i]);
					minY = Math.Min(minY, v[i + 1]); maxY = Math.Max(maxY, v[i + 1]);
				}
				if (maxX - minX > 0.08f || maxY - minY > 0.08f)
					for (int i = 0; i < v.Length; i += 3) { v[i] = v[0]; v[i + 1] = v[1]; }
			}
		}

		// Klip boyunca hiç değişmeyen kanalı (çoğu kemiğin ölçeği/konumu) tek kareye indir: aynı görüntü,
		// animasyon verisi ~5 kat küçülür (ölçüm: 32,6 MB -> 6,4 MB)
		static void CollapseConstant(Channel c)
		{
			int n = c.Path == 1 ? 4 : 3;
			var v = c.Values;
			if (v.Length <= n) return;
			for (int i = n; i < v.Length; i++)
				if (Math.Abs(v[i] - v[i % n]) > 1e-6f) return;
			c.Values = v[..n];
			c.Times = [c.Times[0]];
		}

		static Vector3 Vec3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
		static Vector4 Vec4(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());
	}
}
