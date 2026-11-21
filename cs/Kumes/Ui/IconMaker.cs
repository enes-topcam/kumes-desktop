// Çizilmiş tavuk karesinden tray simgesi: tavuğu saran kareyi kırpıp küçültür.
namespace Kumes.Ui;

static class IconMaker
{
	// Kare düz alfa BGRA görüntüyü yeniden boyutlandır (alanı ortalayarak; alfa ağırlıklı)
	public static byte[] Resize(byte[] src, int size, int to)
	{
		var dst = new byte[to * to * 4];
		float step = size / (float)to;
		for (int y = 0; y < to; y++)
			for (int x = 0; x < to; x++)
			{
				float b = 0, g = 0, r = 0, a = 0; int n = 0;
				for (int sy = (int)(y * step); sy < (int)((y + 1) * step) || sy == (int)(y * step); sy++)
					for (int sx = (int)(x * step); sx < (int)((x + 1) * step) || sx == (int)(x * step); sx++)
					{
						if (sx >= size || sy >= size) continue;
						int i = (sy * size + sx) * 4; float al = src[i + 3];
						b += src[i] * al; g += src[i + 1] * al; r += src[i + 2] * al; a += al; n++;
					}
				if (a <= 0 || n == 0) continue;
				int o = (y * to + x) * 4;
				dst[o] = (byte)(b / a); dst[o + 1] = (byte)(g / a); dst[o + 2] = (byte)(r / a); dst[o + 3] = (byte)(a / n);
			}
		return dst;
	}

	// Ön-çarpılmış BGRA (OpenGL: alttan üste ise flip) -> düz alfa BGRA, üstten alta, aynı boyut
	public static byte[] Unpremultiply(byte[] src, int size, bool flip)
	{
		var dst = new byte[src.Length];
		int row = size * 4;
		for (int y = 0; y < size; y++)
		{
			int s = (flip ? size - 1 - y : y) * row, d = y * row;
			for (int x = 0; x < row; x += 4)
			{
				int a = src[s + x + 3];
				if (a == 0) continue;
				dst[d + x] = (byte)Math.Min(255, src[s + x] * 255 / a);
				dst[d + x + 1] = (byte)Math.Min(255, src[s + x + 1] * 255 / a);
				dst[d + x + 2] = (byte)Math.Min(255, src[s + x + 2] * 255 / a);
				dst[d + x + 3] = (byte)a;
			}
		}
		return dst;
	}

	// src: ön-çarpılmış BGRA, alttan üste (OpenGL). Dönüş: düz alfa BGRA, üstten alta, iconSize x iconSize.
	public static byte[] FromRender(byte[] src, int size, int iconSize)
	{
		int minX = size, minY = size, maxX = -1, maxY = -1;
		for (int y = 0; y < size; y++)
			for (int x = 0; x < size; x++)
				if (src[(y * size + x) * 4 + 3] > 8)
				{
					minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
					minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
				}
		var dst = new byte[iconSize * iconSize * 4];
		if (maxX < 0) return dst;

		// kare kırpma: tavuğun tamamı, ortalanmış, biraz kenar payı
		int side = Math.Max(maxX - minX, maxY - minY) + 4;
		float cx = (minX + maxX) / 2f, cy = (minY + maxY) / 2f;
		float x0 = cx - side / 2f, y0 = cy - side / 2f;
		float step = side / (float)iconSize;

		for (int iy = 0; iy < iconSize; iy++)
		{
			for (int ix = 0; ix < iconSize; ix++)
			{
				// kutu filtresi
				float b = 0, g = 0, r = 0, a = 0;
				int n = 0;
				float sy0 = y0 + (iconSize - 1 - iy) * step, sx0 = x0 + ix * step; // kaynak alttan üste
				for (float sy = sy0; sy < sy0 + step; sy += 1)
					for (float sx = sx0; sx < sx0 + step; sx += 1)
					{
						int px = (int)sx, py = (int)sy;
						n++;
						if (px < 0 || py < 0 || px >= size || py >= size) continue;
						int i = (py * size + px) * 4;
						b += src[i]; g += src[i + 1]; r += src[i + 2]; a += src[i + 3];
					}
				if (n == 0 || a <= 0) continue;
				int o = (iy * iconSize + ix) * 4;
				// ön-çarpılmış -> düz alfa
				dst[o] = (byte)Math.Min(255, b / a * 255);
				dst[o + 1] = (byte)Math.Min(255, g / a * 255);
				dst[o + 2] = (byte)Math.Min(255, r / a * 255);
				dst[o + 3] = (byte)Math.Min(255, a / n);
			}
		}
		return dst;
	}
}
