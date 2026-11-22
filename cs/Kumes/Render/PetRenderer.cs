// Tavuğu OpenGL ile ekran dışı bir tampona çizer ve pikselleri katmanlı pencerenin bitmap'ine kopyalar.
// Işıklar, malzeme ve ton eşleme three.js sürümündeki (pet3d.js) ayarlarla aynı.
using System.Numerics;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Kumes.Render;

// FurColor/FurAlpha: civcivin tüy katmanı (ikinci mesh parçası; saydamlık maskeli, çift yüzlü)
record CoatTextures(string Color, string? Normal, string? Ao, string? FurColor = null, string? FurAlpha = null);

sealed unsafe class PetRenderer : IDisposable
{
	public const float CanvasCss = 160;  // pencere boyu (CSS px, ölçeklenmeden önce)
	public const float ViewH = 0.62f;    // görünen dünya yüksekliği (model ~0.37 birim boyunda)
	public const float GroundPadCss = 6; // ayakların altında bırakılan px
	public static float PxPerUnitCss => CanvasCss / ViewH;

	readonly GL gl;
	readonly int size;
	public int Size => size;
	readonly uint vao, vbo, ebo, program, shadowProgram, shadowVao;
	readonly uint texColor, texNormal, texAo;
	readonly uint texFurColor, texFurAlpha, texFlatNormal, texWhite;
	readonly (int Start, int Count)[] parts;
	readonly int uAlphaTest;
	readonly uint msaaFbo, msaaColor, msaaDepth, resolveFbo, resolveColor;
	readonly uint[] pbos = new uint[2]; // iki tamponlu okuma: GPU'yu beklemeden bir önceki kareyi al
	int pboIndex;
	bool pboPending;
	readonly int indexCount, jointCount;
	readonly int uBones, uViewProj, uShadow;

	readonly IReadOnlyDictionary<string, ImageResult>? decoded;

	// decoded: arka planda çözülmüş dokular (Decode); yoksa dosyadan burada çözülür
	public PetRenderer(GL gl, int size, GlbModel model, CoatTextures coat, IReadOnlyDictionary<string, ImageResult>? decoded = null)
	{
		this.gl = gl;
		this.decoded = decoded;
		this.size = size;
		var mesh = model.Mesh;
		jointCount = model.Skeleton.Joints.Length;
		indexCount = mesh.Indices.Length;
		parts = mesh.Parts.Length > 0 ? mesh.Parts : [(0, indexCount)];

		// ---- köşe verisi: konum3 normal3 tanjant4 uv2 ağırlık4 (float) + eklem4 (ushort) ----
		const int floats = 16;
		int stride = floats * 4 + 8;
		var data = new byte[mesh.VertexCount * stride];
		fixed (byte* dst = data)
		{
			for (int i = 0; i < mesh.VertexCount; i++)
			{
				float* f = (float*)(dst + i * stride);
				for (int k = 0; k < 3; k++) f[k] = mesh.Positions[i * 3 + k];
				for (int k = 0; k < 3; k++) f[3 + k] = mesh.Normals[i * 3 + k];
				for (int k = 0; k < 4; k++) f[6 + k] = mesh.Tangents[i * 4 + k];
				for (int k = 0; k < 2; k++) f[10 + k] = mesh.UVs[i * 2 + k];
				for (int k = 0; k < 4; k++) f[12 + k] = mesh.Weights[i * 4 + k];
				ushort* j = (ushort*)(f + floats);
				for (int k = 0; k < 4; k++) j[k] = mesh.Joints[i * 4 + k];
			}
		}

		vao = gl.GenVertexArray();
		gl.BindVertexArray(vao);
		vbo = gl.GenBuffer();
		gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
		gl.BufferData<byte>(BufferTargetARB.ArrayBuffer, data, BufferUsageARB.StaticDraw);
		ebo = gl.GenBuffer();
		gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
		gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, mesh.Indices, BufferUsageARB.StaticDraw);
		void Attr(uint idx, int n, int offsetFloats)
		{
			gl.VertexAttribPointer(idx, n, VertexAttribPointerType.Float, false, (uint)stride, (void*)(offsetFloats * 4));
			gl.EnableVertexAttribArray(idx);
		}
		Attr(0, 3, 0);
		Attr(1, 3, 3);
		Attr(2, 4, 6);
		Attr(3, 2, 10);
		Attr(5, 4, 12);
		gl.VertexAttribIPointer(4, 4, VertexAttribIType.UnsignedShort, (uint)stride, (void*)(floats * 4));
		gl.EnableVertexAttribArray(4);
		gl.BindVertexArray(0);

		program = Link(Shaders.PetVertex.Replace("JOINT_COUNT", jointCount.ToString()), Shaders.PetFragment);
		shadowProgram = Link(Shaders.FullscreenVertex, Shaders.ShadowFragment);
		shadowVao = gl.GenVertexArray();

		uBones = gl.GetUniformLocation(program, "uBones");
		uViewProj = gl.GetUniformLocation(program, "uViewProj");
		uShadow = gl.GetUniformLocation(shadowProgram, "uShadow");

		texColor = LoadTexture(coat.Color, srgb: true);
		texNormal = coat.Normal != null ? LoadTexture(coat.Normal, false) : Solid(128, 128, 255);
		texAo = coat.Ao != null ? LoadTexture(coat.Ao, false) : Solid(255, 255, 255);
		bool fur = parts.Length > 1 && coat.FurColor != null && coat.FurAlpha != null;
		texFurColor = fur ? LoadTexture(coat.FurColor!, srgb: true) : 0;
		texFurAlpha = fur ? LoadTexture(coat.FurAlpha!, false) : 0;
		texFlatNormal = Solid(128, 128, 255);
		texWhite = Solid(255, 255, 255);
		uAlphaTest = gl.GetUniformLocation(program, "uAlphaTest");

		gl.UseProgram(program);
		gl.Uniform1(gl.GetUniformLocation(program, "uColorTex"), 0);
		gl.Uniform1(gl.GetUniformLocation(program, "uAlphaTex"), 3);
		gl.Uniform1(gl.GetUniformLocation(program, "uNormalTex"), 1);
		gl.Uniform1(gl.GetUniformLocation(program, "uAoTex"), 2);
		gl.Uniform3(gl.GetUniformLocation(program, "uTint"), 1.0f, 0.97f, 0.92f); // oyundaki gibi hafif sıcak ton
		gl.Uniform1(gl.GetUniformLocation(program, "uExposure"), 1.05f);
		// HemisphereLight(0xfff6ea, 0x4a5462, 1.7)
		SetColor("uSky", 0xfff6ea, 1.7f);
		SetColor("uGround", 0x4a5462, 1.7f);
		// DirectionalLight(0xffffff, 2.1) @ (0.8, 1.4, 1.2) ve kenar ışığı (0xdfe8ff, 0.8) @ (-1, 0.6, -0.8)
		SetDir("uSunDir", new Vector3(0.8f, 1.4f, 1.2f));
		SetColor("uSunColor", 0xffffff, 2.1f);
		SetDir("uRimDir", new Vector3(-1f, 0.6f, -0.8f));
		SetColor("uRimColor", 0xdfe8ff, 0.8f);

		// ---- ekran dışı tampon: 4x MSAA, sonra tek örnekli tampona çözülür ----
		msaaFbo = gl.GenFramebuffer();
		gl.BindFramebuffer(FramebufferTarget.Framebuffer, msaaFbo);
		msaaColor = gl.GenRenderbuffer();
		gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaColor);
		gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.Rgba8, (uint)size, (uint)size);
		gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, msaaColor);
		msaaDepth = gl.GenRenderbuffer();
		gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, msaaDepth);
		gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, 4, InternalFormat.DepthComponent24, (uint)size, (uint)size);
		gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, msaaDepth);
		CheckFbo("msaa");

		resolveFbo = gl.GenFramebuffer();
		gl.BindFramebuffer(FramebufferTarget.Framebuffer, resolveFbo);
		resolveColor = gl.GenRenderbuffer();
		gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, resolveColor);
		gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, (uint)size, (uint)size);
		gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, resolveColor);
		CheckFbo("resolve");
		gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

		for (int i = 0; i < 2; i++)
		{
			pbos[i] = gl.GenBuffer();
			gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbos[i]);
			gl.BufferData(BufferTargetARB.PixelPackBuffer, (nuint)(size * size * 4), null, BufferUsageARB.StreamRead);
		}
		gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
	}

	void CheckFbo(string name)
	{
		var st = gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
		if (st != GLEnum.FramebufferComplete) throw new InvalidOperationException($"FBO ({name}) hazır değil: {st}");
	}

	// Kümes penceresindeki büyük resim için sinematik ışık: önden loş, arkadan güçlü turuncu kenar ışığı
	public void UseHeroLighting()
	{
		gl.UseProgram(program);
		SetColor("uSky", 0xffd2a6, 1.4f);
		SetColor("uGround", 0x2a2420, 1.1f);
		SetDir("uSunDir", new Vector3(-0.5f, 0.9f, 1.1f));
		SetColor("uSunColor", 0xfff1e0, 2.3f);
		SetDir("uRimDir", new Vector3(1.0f, 0.5f, -0.7f));
		SetColor("uRimColor", 0xff9a4a, 5.0f);
	}

	void SetColor(string name, uint hex, float intensity)
	{
		static float Lin(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
		float r = Lin(((hex >> 16) & 255) / 255f), g = Lin(((hex >> 8) & 255) / 255f), b = Lin((hex & 255) / 255f);
		gl.Uniform3(gl.GetUniformLocation(program, name), r * intensity, g * intensity, b * intensity);
	}

	void SetDir(string name, Vector3 d)
	{
		d = Vector3.Normalize(d);
		gl.Uniform3(gl.GetUniformLocation(program, name), d.X, d.Y, d.Z);
	}

	uint Link(string vs, string fs)
	{
		uint Compile(ShaderType type, string src)
		{
			uint s = gl.CreateShader(type);
			gl.ShaderSource(s, src);
			gl.CompileShader(s);
			gl.GetShader(s, ShaderParameterName.CompileStatus, out int ok);
			if (ok == 0) throw new InvalidOperationException($"{type} derlenemedi: {gl.GetShaderInfoLog(s)}");
			return s;
		}
		uint v = Compile(ShaderType.VertexShader, vs), f = Compile(ShaderType.FragmentShader, fs);
		uint p = gl.CreateProgram();
		gl.AttachShader(p, v);
		gl.AttachShader(p, f);
		gl.LinkProgram(p);
		gl.GetProgram(p, ProgramPropertyARB.LinkStatus, out int linked);
		if (linked == 0) throw new InvalidOperationException("Gölgelendirici bağlanamadı: " + gl.GetProgramInfoLog(p));
		gl.DeleteShader(v);
		gl.DeleteShader(f);
		return p;
	}

	// Arka planda önceden çözülmüş dokular (PNG çözmek yavaş: model değişirken ana iş parçacığı donmasın).
	// Preload arka planda doldurur, LoadTexture önce buraya bakar; model yüklenince ClearPreloaded ile boşaltılır.
	static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ImageResult> preloaded = new(StringComparer.OrdinalIgnoreCase);

	public static void Preload(CoatTextures coat)
	{
		foreach (var (p, img) in Decode(coat)) preloaded[p] = img;
	}

	// Bir rengin dokularını çöz (her iş parçacığında çağrılabilir; GL kullanmaz)
	public static Dictionary<string, ImageResult> Decode(CoatTextures coat)
	{
		var d = new Dictionary<string, ImageResult>(StringComparer.OrdinalIgnoreCase);
		foreach (var p in new[] { coat.Color, coat.Normal, coat.Ao, coat.FurColor, coat.FurAlpha })
			if (p != null && !d.ContainsKey(p))
				d[p] = ImageResult.FromMemory(File.ReadAllBytes(p), ColorComponents.RedGreenBlueAlpha);
		return d;
	}

	public static void ClearPreloaded() => preloaded.Clear();

	uint LoadTexture(string path, bool srgb)
	{
		var img = decoded != null && decoded.TryGetValue(path, out var dec) ? dec
			: preloaded.TryGetValue(path, out var pre) ? pre
			: ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
		uint t = gl.GenTexture();
		gl.BindTexture(TextureTarget.Texture2D, t);
		fixed (byte* p = img.Data)
			gl.TexImage2D(TextureTarget.Texture2D, 0, srgb ? InternalFormat.Srgb8Alpha8 : InternalFormat.Rgba8,
				(uint)img.Width, (uint)img.Height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, p);
		gl.GenerateMipmap(TextureTarget.Texture2D);
		gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
		gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
		gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
		gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
		gl.TexParameter(TextureTarget.Texture2D, (TextureParameterName)0x84FE, 4f); // anizotropi 4x
		return t;
	}

	uint Solid(byte r, byte g, byte b)
	{
		uint t = gl.GenTexture();
		gl.BindTexture(TextureTarget.Texture2D, t);
		byte* px = stackalloc byte[] { r, g, b, 255 };
		gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, px);
		gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
		gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
		return t;
	}

	// Kareyi çiz ve okumayı başlat; bir ÖNCEKİ karenin pikselleri hazırsa dest'e kopyala (true döner).
	// Okuma GPU'yu beklemediği için CPU boşa dönmez; karşılığında görüntü bir kare (25 ms) geriden gelir.
	// dest: üstten alta satırlı hedefte tavuk alanının ilk satırı; destStride: hedefin satır uzunluğu (bayt)
	// dropPx: tavuğu tuvalde bu kadar aşağı çiz (büyük zıplamalarda pencere yukarı taşınır, tavuk tuvalde kalır)
	public bool RenderAsync(Matrix4x4[] joints, float shadowAlpha, float scale, byte* dest, int destStride, float dropPx = 0)
	{
		Draw(joints, shadowAlpha, scale, dropPx);
		gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, resolveFbo);
		gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbos[pboIndex]);
		gl.ReadPixels(0, 0, (uint)size, (uint)size, PixelFormat.Bgra, PixelType.UnsignedByte, null);

		bool copied = false;
		if (pboPending)
		{
			gl.BindBuffer(BufferTargetARB.PixelPackBuffer, pbos[pboIndex ^ 1]);
			var src = gl.MapBuffer(BufferTargetARB.PixelPackBuffer, BufferAccessARB.ReadOnly);
			if (src != null)
			{
				int row = size * 4;
				for (int y = 0; y < size; y++) // OpenGL alttan üste -> hedef üstten alta
					System.Buffer.MemoryCopy((byte*)src + (size - 1 - y) * row, dest + y * destStride, row, row);
				gl.UnmapBuffer(BufferTargetARB.PixelPackBuffer);
				copied = true;
			}
		}
		gl.BindBuffer(BufferTargetARB.PixelPackBuffer, 0);
		gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
		pboPending = true;
		pboIndex ^= 1;
		return copied;
	}

	// Çiz ve hemen oku (snapshot aracı için)
	public void Render(Matrix4x4[] joints, float shadowAlpha, float scale, byte* dest, float dropPx = 0)
	{
		Draw(joints, shadowAlpha, scale, dropPx);
		gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, resolveFbo);
		gl.ReadPixels(0, 0, (uint)size, (uint)size, PixelFormat.Bgra, PixelType.UnsignedByte, dest);
		gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
	}

	// shadowAlpha: yerdeki gölgenin koyuluğu (0 = yok); scale: ekran ölçeği (DPI)
	void Draw(Matrix4x4[] joints, float shadowAlpha, float scale, float dropPx = 0)
	{
		gl.BindFramebuffer(FramebufferTarget.Framebuffer, msaaFbo);
		gl.Viewport(0, 0, (uint)size, (uint)size);
		gl.ClearColor(0, 0, 0, 0);
		gl.ClearDepth(1);
		gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
		gl.Enable(EnableCap.Blend);
		gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha); // ön-çarpılmış alfa

		float pad = GroundPadCss * scale;
		if (shadowAlpha > 0)
		{
			// yerdeki gölge: ayakların 2 px altında 30x5 px elips (pet.js ile aynı)
			gl.Disable(EnableCap.DepthTest);
			gl.Disable(EnableCap.CullFace);
			gl.UseProgram(shadowProgram);
			gl.Uniform4(uShadow, size / 2f, pad - 2 * scale, 30 * scale, 5 * scale);
			gl.Uniform1(gl.GetUniformLocation(shadowProgram, "uAlpha"), shadowAlpha);
			gl.BindVertexArray(shadowVao);
			gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
		}

		gl.Enable(EnableCap.DepthTest);
		gl.DepthFunc(DepthFunction.Less);
		gl.Enable(EnableCap.CullFace);
		gl.CullFace(TriangleFace.Back);
		gl.FrontFace(FrontFaceDirection.Ccw);
		gl.UseProgram(program);

		float padUnits = pad / (PxPerUnitCss * scale);
		var proj = Matrix4x4.CreateOrthographicOffCenter(-ViewH / 2, ViewH / 2, -padUnits, ViewH - padUnits, 0.01f, 10f);
		var viewProj = Matrix4x4.CreateTranslation(0, -dropPx / (PxPerUnitCss * scale), -3) * proj;
		gl.UniformMatrix4(uViewProj, 1, false, (float*)&viewProj);
		fixed (Matrix4x4* jm = joints)
			gl.UniformMatrix4(uBones, (uint)jointCount, false, (float*)jm);

		gl.BindVertexArray(vao);
		// gövde (ilk parça; tüy dokusu yoksa hepsi)
		bool fur = texFurColor != 0 && parts.Length > 1;
		gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, texColor);
		gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, texNormal);
		gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, texAo);
		gl.Uniform1(uAlphaTest, 0);
		int bodyCount = fur ? parts[0].Count : indexCount;
		gl.DrawElements(PrimitiveType.Triangles, (uint)bodyCount, DrawElementsType.UnsignedInt, (void*)(parts[0].Start * 4));
		if (fur)
		{
			// tüy katmanı: çift yüzlü, saydamlık maskesi kenar yumuşatmayla (alpha-to-coverage) uygulanır
			gl.Disable(EnableCap.CullFace);
			gl.Disable(EnableCap.Blend);
			gl.Enable(EnableCap.SampleAlphaToCoverage);
			gl.Enable(EnableCap.SampleAlphaToOne);
			gl.ActiveTexture(TextureUnit.Texture0); gl.BindTexture(TextureTarget.Texture2D, texFurColor);
			gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, texFlatNormal);
			gl.ActiveTexture(TextureUnit.Texture2); gl.BindTexture(TextureTarget.Texture2D, texWhite);
			gl.ActiveTexture(TextureUnit.Texture3); gl.BindTexture(TextureTarget.Texture2D, texFurAlpha);
			gl.Uniform1(uAlphaTest, 1);
			for (int i = 1; i < parts.Length; i++)
				gl.DrawElements(PrimitiveType.Triangles, (uint)parts[i].Count, DrawElementsType.UnsignedInt, (void*)(parts[i].Start * 4));
			gl.Disable(EnableCap.SampleAlphaToCoverage);
			gl.Disable(EnableCap.SampleAlphaToOne);
			gl.Enable(EnableCap.Blend);
			gl.ActiveTexture(TextureUnit.Texture0);
		}
		gl.BindVertexArray(0);

		// MSAA'yı tek örnekli tampona çöz (okuma oradan: BGRA, alttan üste)
		gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, msaaFbo);
		gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, resolveFbo);
		gl.BlitFramebuffer(0, 0, size, size, 0, 0, size, size, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
	}

	public void Dispose()
	{
		gl.DeleteBuffer(vbo); gl.DeleteBuffer(ebo);
		gl.DeleteBuffer(pbos[0]); gl.DeleteBuffer(pbos[1]);
		gl.DeleteVertexArray(vao); gl.DeleteVertexArray(shadowVao);
		gl.DeleteProgram(program); gl.DeleteProgram(shadowProgram);
		gl.DeleteTexture(texColor); gl.DeleteTexture(texNormal); gl.DeleteTexture(texAo);
		if (texFurColor != 0) gl.DeleteTexture(texFurColor);
		if (texFurAlpha != 0) gl.DeleteTexture(texFurAlpha);
		gl.DeleteTexture(texFlatNormal); gl.DeleteTexture(texWhite);
		gl.DeleteFramebuffer(msaaFbo); gl.DeleteFramebuffer(resolveFbo);
		gl.DeleteRenderbuffer(msaaColor); gl.DeleteRenderbuffer(msaaDepth); gl.DeleteRenderbuffer(resolveColor);
	}
}
