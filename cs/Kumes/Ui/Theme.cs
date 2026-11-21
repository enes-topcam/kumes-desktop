// Giriş penceresinin renkleri: Kümes penceresiyle (MainWindow) aynı sade koyu mor-gri dil, tek vurgu soluk leylak.
namespace Kumes.Ui;

static class Theme
{
	public const uint Bg = 0x10111AFF;        // neredeyse siyah-mor zemin
	public const uint Ink = 0xF5F3F7FF;       // ana yazı
	public const uint Muted = 0xAAA9BCFF;     // ikincil yazı
	public const uint Straw = 0xBAA0ECFF;     // vurgu: soluk leylak
	public const uint Leaf = 0x79CBA5FF;      // başarı (giriş tamam)
	public const uint Danger = 0xE27C86FF;    // hata
	public const uint Outline = 0x323240FF;   // çerçeve
	public const uint White = 0xFFFFFFFF;     // QR kutusu (telefon okusun diye beyaz)

	public const string Serif = "Segoe UI Variable Display"; // başlık yazısı (Kümes penceresiyle aynı)
	public const string Sans = "Segoe UI";

	// Pencere başlık çubuğunu koyu temaya uydur (Windows 11)
	public static unsafe void ApplyTitleBar(nint hwnd)
	{
		int dark = 1;
		Native.Win32.DwmSetWindowAttribute(hwnd, Native.Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, &dark, sizeof(int));
		int caption = ColorRef(Bg), text = ColorRef(Ink);
		Native.Win32.DwmSetWindowAttribute(hwnd, Native.Win32.DWMWA_CAPTION_COLOR, &caption, sizeof(int));
		Native.Win32.DwmSetWindowAttribute(hwnd, Native.Win32.DWMWA_TEXT_COLOR, &text, sizeof(int));
	}

	static int ColorRef(uint rgba) => (int)(((rgba >> 24) & 255) | (((rgba >> 16) & 255) << 8) | (((rgba >> 8) & 255) << 16));
}
