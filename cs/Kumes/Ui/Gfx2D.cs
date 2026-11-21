// 2D çizim (barlar, yazılar, baloncuklar, renkli emojiler): Direct2D + DirectWrite, yazılımla bir WIC
// bitmap'ine çizer. Bitmap ön-çarpılmış BGRA olduğu için katmanlı pencereye doğrudan kopyalanabilir.
using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using D2DFactory = Vortice.Direct2D1.ID2D1Factory;
using DWFontWeight = Vortice.DirectWrite.FontWeight;

namespace Kumes.Ui;

sealed class Gfx2D : IDisposable
{
	public static Gfx2D Shared { get; } = new();

	public readonly D2DFactory D2D;
	public readonly IDWriteFactory DW;
	public readonly IWICImagingFactory Wic;
	readonly Dictionary<(string, float, int), IDWriteTextFormat> formats = new();
	readonly Dictionary<(string, float, int, bool, bool), IDWriteTextFormat> displayFormats = new();

	Gfx2D()
	{
		D2D = D2D1.D2D1CreateFactory<D2DFactory>(Vortice.Direct2D1.FactoryType.SingleThreaded);
		DW = DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
		Wic = new IWICImagingFactory();
	}

	public IDWriteTextFormat Font(float size, int weight = 400, string family = "Segoe UI")
	{
		var key = (family, size, weight);
		if (!formats.TryGetValue(key, out var f))
		{
			f = DW.CreateTextFormat(family, (DWFontWeight)weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
			f.WordWrapping = WordWrapping.NoWrap;
			formats[key] = f;
		}
		return f;
	}

	// Dar (condensed) ve/veya eğik yazı tipi: başlıklar ve büyük sayılar için
	public IDWriteTextFormat Display(float size, int weight, string family, bool condensed, bool italic)
	{
		var key = (family, size, weight, condensed, italic);
		if (!displayFormats.TryGetValue(key, out var f))
		{
			f = DW.CreateTextFormat(family, (DWFontWeight)weight, italic ? Vortice.DirectWrite.FontStyle.Oblique : Vortice.DirectWrite.FontStyle.Normal,
				condensed ? FontStretch.Condensed : FontStretch.Normal, size);
			f.WordWrapping = WordWrapping.NoWrap;
			displayFormats[key] = f;
		}
		return f;
	}

	public Vector2 Measure(string text, IDWriteTextFormat format)
	{
		using var layout = DW.CreateTextLayout(text, format, 4096, 4096);
		var m = layout.Metrics;
		return new Vector2(m.WidthIncludingTrailingWhitespace, m.Height);
	}

	public void Dispose()
	{
		foreach (var f in formats.Values) f.Dispose();
		Wic.Dispose();
		DW.Dispose();
		D2D.Dispose();
	}
}

// Belirli boyutta bir çizim yüzeyi
sealed unsafe class Surface2D : IDisposable
{
	public int Width { get; }
	public int Height { get; }
	readonly IWICBitmap bitmap;
	public readonly ID2D1RenderTarget RT;
	readonly Dictionary<uint, ID2D1SolidColorBrush> brushes = new();

	public Surface2D(int width, int height)
	{
		Width = width;
		Height = height;
		var g = Gfx2D.Shared;
		bitmap = g.Wic.CreateBitmap((uint)width, (uint)height, PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
		RT = g.D2D.CreateWicBitmapRenderTarget(bitmap, new RenderTargetProperties
		{
			Type = RenderTargetType.Software,
			PixelFormat = new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
		});
		RT.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale; // saydam zeminde ClearType olmaz
	}

	// 0xRRGGBBAA
	public ID2D1SolidColorBrush Brush(uint rgba)
	{
		if (!brushes.TryGetValue(rgba, out var b))
		{
			b = RT.CreateSolidColorBrush(new Color4(((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f));
			brushes[rgba] = b;
		}
		return b;
	}

	public void Begin()
	{
		RT.BeginDraw();
		RT.Clear(new Color4(0, 0, 0, 0));
	}

	public void End() => RT.EndDraw();

	public void FillRoundRect(float x, float y, float w, float h, float r, uint rgba) =>
		RT.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(rgba));

	public void StrokeRoundRect(float x, float y, float w, float h, float r, uint rgba, float width = 1) =>
		RT.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(rgba), width);

	public void FillRect(float x, float y, float w, float h, uint rgba) => RT.FillRectangle(new Rect(x, y, w, h), Brush(rgba));

	public void FillEllipse(float cx, float cy, float rx, float ry, uint rgba) =>
		RT.FillEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), Brush(rgba));

	public void FillTriangle(Vector2 a, Vector2 b, Vector2 c, uint rgba)
	{
		using var geo = Gfx2D.Shared.D2D.CreatePathGeometry();
		using (var sink = geo.Open())
		{
			sink.BeginFigure(a, FigureBegin.Filled);
			sink.AddLine(b);
			sink.AddLine(c);
			sink.EndFigure(FigureEnd.Closed);
			sink.Close();
		}
		RT.FillGeometry(geo, Brush(rgba));
	}

	// Sol üst köşeden yazı (renkli emoji destekli)
	public void Text(string text, float x, float y, IDWriteTextFormat format, uint rgba, float opacity = 1)
	{
		var brush = Brush(rgba);
		brush.Opacity = opacity;
		RT.DrawText(text, format, new Rect(x, y, 4096, 4096), brush, DrawTextOptions.EnableColorFont);
		brush.Opacity = 1;
	}

	// Ortalanmış yazı (cx, cy merkez)
	public void TextCentered(string text, float cx, float cy, IDWriteTextFormat format, uint rgba, float opacity = 1)
	{
		var size = Gfx2D.Shared.Measure(text, format);
		Text(text, cx - size.X / 2, cy - size.Y / 2, format, rgba, opacity);
	}

	// ---- referans tasarım için: renk geçişi, resim, kırpma, şekil, döndürme ----

	// Doğrusal renk geçişi (yatay ya da dikey) ile dikdörtgen / yuvarlak dikdörtgen
	public void FillGradient(float x, float y, float w, float h, uint from, uint to, bool vertical, float radius = 0)
	{
		using var stops = RT.CreateGradientStopCollection(
		[
			new GradientStop(0, ToColor(from)),
			new GradientStop(1, ToColor(to)),
		]);
		var props = new LinearGradientBrushProperties(new Vector2(x, y), vertical ? new Vector2(x, y + h) : new Vector2(x + w, y));
		using var brush = RT.CreateLinearGradientBrush(props, stops);
		if (radius > 0) RT.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), radius, radius), brush);
		else RT.FillRectangle(new Rect(x, y, w, h), brush);
	}

	static Color4 ToColor(uint rgba) =>
		new(((rgba >> 24) & 255) / 255f, ((rgba >> 16) & 255) / 255f, ((rgba >> 8) & 255) / 255f, (rgba & 255) / 255f);

	// Düz alfa BGRA'dan Direct2D bitmap'i (bir kere oluşturup tekrar çizmek için)
	public ID2D1Bitmap CreateImage(byte[] bgra, int w, int h)
	{
		var pre = new byte[bgra.Length];
		for (int i = 0; i < bgra.Length; i += 4)
		{
			int a = bgra[i + 3];
			pre[i] = (byte)(bgra[i] * a / 255);
			pre[i + 1] = (byte)(bgra[i + 1] * a / 255);
			pre[i + 2] = (byte)(bgra[i + 2] * a / 255);
			pre[i + 3] = (byte)a;
		}
		fixed (byte* p = pre)
			return RT.CreateBitmap(new Vortice.Mathematics.SizeI(w, h), (nint)p, (uint)(w * 4),
				new BitmapProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
	}

	public void DrawImage(ID2D1Bitmap bmp, float x, float y, float w, float h, float opacity = 1, Rect? src = null)
	{
		var size = bmp.PixelSize;
		RT.DrawBitmap(bmp, new Rect(x, y, w, h), opacity, Vortice.Direct2D1.BitmapInterpolationMode.Linear, src ?? new Rect(0, 0, size.Width, size.Height));
	}

	public void PushClip(float x, float y, float w, float h) => RT.PushAxisAlignedClip(new Vortice.RawRectF(x, y, x + w, y + h), AntialiasMode.PerPrimitive);
	public void PopClip() => RT.PopAxisAlignedClip();

	// Döndürme/taşıma (Reset ile geri al)
	public void Transform(Matrix3x2 m) => RT.Transform = m;
	public void ResetTransform() => RT.Transform = Matrix3x2.Identity;

	// Serbest şekil: noktalardan çizgi (açık ya da kapalı) ve dolgu
	public void Path(ReadOnlySpan<Vector2> pts, uint? fill, uint? stroke, float width = 1, bool closed = true)
	{
		using var geo = Gfx2D.Shared.D2D.CreatePathGeometry();
		using (var sink = geo.Open())
		{
			sink.BeginFigure(pts[0], fill != null ? FigureBegin.Filled : FigureBegin.Hollow);
			for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
			sink.EndFigure(closed ? FigureEnd.Closed : FigureEnd.Open);
			sink.Close();
		}
		if (fill is uint f) RT.FillGeometry(geo, Brush(f));
		if (stroke is uint s)
		{
			using var style = Gfx2D.Shared.D2D.CreateStrokeStyle(new StrokeStyleProperties { LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round });
			RT.DrawGeometry(geo, Brush(s), width, style);
		}
	}

	// Çizgi simge (SvgIcons, 24'lük ızgara) x,y'de size boyunda
	public void Icon(string d, float x, float y, float size, uint rgba, float width = 1.75f)
	{
		var old = RT.Transform;
		RT.Transform = Matrix3x2.CreateScale(size / 24f) * Matrix3x2.CreateTranslation(x, y) * old;
		RT.DrawGeometry(SvgIcons.Geometry(d), Brush(rgba), width, SvgIcons.Round);
		RT.Transform = old;
	}

	public void StrokeEllipse(float cx, float cy, float rx, float ry, uint rgba, float width) =>
		RT.DrawEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), Brush(rgba), width);

	// Yumuşak parlama: renk, verilen kutunun etrafına artan boşlukla birkaç kat düşük opaklıkta yayılır
	public void Glow(float x, float y, float w, float h, float radius, uint rgb, float spread, float strength = 0.35f)
	{
		int layers = 6;
		for (int i = layers; i >= 1; i--)
		{
			float g = spread * i / layers;
			float a = strength * (1 - (float)(i - 1) / layers) / layers * 2;
			uint c = (rgb & 0xFFFFFF00) | (uint)Math.Clamp(a * 255, 0, 255);
			FillRoundRect(x - g, y - g, w + 2 * g, h + 2 * g, radius + g, c);
		}
	}

	// Düz alfa BGRA (üstten alta) görüntüyü çiz
	public void Image(byte[] bgra, int w, int h, float x, float y, float size)
	{
		var pre = new byte[bgra.Length];
		for (int i = 0; i < bgra.Length; i += 4)
		{
			int a = bgra[i + 3];
			pre[i] = (byte)(bgra[i] * a / 255);
			pre[i + 1] = (byte)(bgra[i + 1] * a / 255);
			pre[i + 2] = (byte)(bgra[i + 2] * a / 255);
			pre[i + 3] = (byte)a;
		}
		fixed (byte* p = pre)
		{
			using var bmp = RT.CreateBitmap(new Vortice.Mathematics.SizeI(w, h), (nint)p, (uint)(w * 4),
				new BitmapProperties(new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied)));
			RT.DrawBitmap(bmp, new Rect(x, y, size, size), 1, Vortice.Direct2D1.BitmapInterpolationMode.Linear, new Rect(0, 0, w, h));
		}
	}

	// Pikselleri hedef bitmap'e kopyala (üstten alta, ön-çarpılmış BGRA)
	public void CopyTo(byte* dst, int dstStride, int dstX = 0, int dstY = 0)
	{
		using var l = bitmap.Lock(BitmapLockFlags.Read);
		var data = l.Data;
		for (int y = 0; y < Height; y++)
			System.Buffer.MemoryCopy((byte*)data.DataPointer + y * data.Pitch, dst + (dstY + y) * dstStride + dstX * 4, Width * 4, Width * 4);
	}

	// Hedefin üstüne bindir (ön-çarpılmış "over"): dst = src + dst * (1 - src.a)
	public void CompositeOver(byte* dst, int dstStride)
	{
		using var l = bitmap.Lock(BitmapLockFlags.Read);
		var data = l.Data;
		for (int y = 0; y < Height; y++)
		{
			byte* s = (byte*)data.DataPointer + y * data.Pitch;
			byte* d = dst + y * dstStride;
			for (int x = 0; x < Width; x++, s += 4, d += 4)
			{
				int a = s[3];
				if (a == 0) continue;
				if (a == 255) { *(uint*)d = *(uint*)s; continue; }
				int inv = 255 - a;
				d[0] = (byte)(s[0] + d[0] * inv / 255);
				d[1] = (byte)(s[1] + d[1] * inv / 255);
				d[2] = (byte)(s[2] + d[2] * inv / 255);
				d[3] = (byte)(a + d[3] * inv / 255);
			}
		}
	}

	public void Dispose()
	{
		foreach (var b in brushes.Values) b.Dispose();
		RT.Dispose();
		bitmap.Dispose();
	}
}
