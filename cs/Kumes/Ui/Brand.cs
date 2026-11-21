// Kümes logosu (leylak yumurta içinde tavuk): uygulama simgesi (exe, görev çubuğu, pencereler; koyu kare zeminde),
// işaret (Kümes penceresinin sol üstü) ve tray simgesi (gizli simgeler dahil, bildirimler: küçük alanlar için).
// Görseller Assets/ altında; .ico exe'nin simgesi, PNG'ler exe'ye gömülü kaynak.
using System.Runtime.InteropServices;
using Kumes.Native;
using StbImageSharp;
using static Kumes.Native.Win32;

namespace Kumes.Ui;

static class Brand
{
	[DllImport("user32.dll")] static extern nint LoadImageW(nint hInst, nint name, uint type, int cx, int cy, uint flags);
	[DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
	const int SM_CXICON = 11, SM_CXSMICON = 49;
	const nint AppIconId = 32512; // .NET'in exe'ye koyduğu ApplicationIcon kaynağı

	// Gömülü PNG -> düz alfa BGRA
	public static (byte[] Bgra, int W, int H)? Png(string name)
	{
		using var s = typeof(Brand).Assembly.GetManifestResourceStream($"Kumes.Assets.{name}");
		if (s == null) return null;
		var img = ImageResult.FromStream(s, ColorComponents.RedGreenBlueAlpha);
		var d = img.Data;
		for (int i = 0; i < d.Length; i += 4) (d[i], d[i + 2]) = (d[i + 2], d[i]);
		return (d, img.Width, img.Height);
	}

	// Pencere simgeleri: küçük (başlık çubuğu) ve büyük (Alt+Tab, görev çubuğu), ekran ölçeğine göre
	public static (nint Small, nint Big) WindowIcons()
	{
		var hInst = GetModuleHandleW(null);
		nint small = LoadImageW(hInst, AppIconId, 1 /* IMAGE_ICON */, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CXSMICON), 0);
		nint big = LoadImageW(hInst, AppIconId, 1, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CXICON), 0);
		if (small == 0 || big == 0) // yedek: gömülü uygulama simgesi PNG'sinden
		{
			if (Png("logo_symbol.png") is var (b, w, h))
			{
				if (small == 0) small = TrayIcon.CreateIcon(Ui.IconMaker.Resize(b, w, 32), 32);
				if (big == 0) big = TrayIcon.CreateIcon(Ui.IconMaker.Resize(b, w, 48), 48);
			}
		}
		return (small, big);
	}

	// Tray simgesi: tavuk işareti, ekran ölçeğine en yakın hazır boyut
	public static nint TrayMark()
	{
		int want = GetSystemMetrics(SM_CXSMICON);
		int[] sizes = [16, 20, 24, 32, 40, 48, 64];
		int pick = sizes.FirstOrDefault(x => x >= want, 64);
		if (Png($"mark_{pick}.png") is var (b, w, _) ) return TrayIcon.CreateIcon(w == pick ? b : Ui.IconMaker.Resize(b, w, pick), pick);
		return 0;
	}

	// Bildirim balonu için büyük simge (uygulama simgesi)
	public static nint BalloonIcon()
	{
		var hInst = GetModuleHandleW(null);
		int s = GetSystemMetrics(SM_CXICON);
		return LoadImageW(hInst, AppIconId, 1, s, s, 0);
	}
}
