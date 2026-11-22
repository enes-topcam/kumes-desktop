// Tavuğun konabileceği yerler: açık pencerelerin üst kenarları (başka pencerenin altında kalan kısımlar hariç).
using System.Runtime.InteropServices;
using static Kumes.Native.Win32;

namespace Kumes.Native;

// Konulan kenar. Koordinatlar "dünya": x çalışma alanının solundan, Alt görev çubuğunun üstünden yukarı (px).
sealed class Perch
{
	public nint Hwnd;
	public float Left, Right;  // kenarın görünen parçası
	public float Alt;          // kenarın yüksekliği
	public int WinLeft;        // pencerenin ekrandaki sol kenarı (pencere taşınınca tavuğu kaydırmak için)
}

static unsafe class Ledges
{
	static readonly List<nint> found = new();
	// KumesLayer: tavuğun kendi penceresi. Kümes'in diğer pencereleri (ana pencere, giriş) sıradan pencere sayılır:
	// tavuk üstlerine konabilir ve arkalarında kalan kenarları kapatırlar.
	static readonly HashSet<string> ignoredClasses = ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "KumesLayer"];

	[UnmanagedCallersOnly]
	static int Collect(nint hwnd, nint _)
	{
		found.Add(hwnd);
		return 1;
	}

	// Görünür bir pencerenin gerçek sınırları (Windows 10/11'deki görünmez yeniden boyutlandırma kenarı hariç)
	public static bool TryGetBounds(nint hwnd, out RECT r)
	{
		r = default;
		if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
		int cloaked = 0;
		if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0) return false; // gizli UWP / başka masaüstü
		RECT b;
		if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, &b, sizeof(RECT)) != 0) return false;
		r = b;
		return r.Right > r.Left && r.Bottom > r.Top;
	}

	static string ClassName(nint hwnd)
	{
		char* buf = stackalloc char[64];
		int n = GetClassNameW(hwnd, buf, 64);
		return new string(buf, 0, Math.Max(0, n));
	}

	// Tüm kenarları bul (en öndeki pencereden arkaya). minTop: bundan yukarıdaki kenarlar sayılmaz
	// (tavuk ekranın üstünden taşmasın); minWidth: bundan kısa parçalar sayılmaz.
	public static List<Perch> Find(RECT wa, int minTop, int minWidth)
	{
		found.Clear();
		EnumWindows(&Collect, 0); // Z sırasıyla: en öndeki önce
		var occluders = new List<RECT>();
		var result = new List<Perch>();
		foreach (var h in found)
		{
			if (!TryGetBounds(h, out var r)) continue;
			if (ignoredClasses.Contains(ClassName(h))) continue;

			long ex = GetWindowLongPtrW(h, GWL_EXSTYLE);
			if ((ex & WS_EX_TRANSPARENT) != 0) continue; // tıklamayı geçiren saydam katmanlar (NVIDIA/Discord overlay vb.)
			bool tool = (ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0;
			bool candidate = !tool && r.Right - r.Left >= 120 && r.Bottom - r.Top >= 60
				&& r.Top >= minTop && r.Top <= wa.Bottom - 30;

			if (candidate)
			{
				// kenarın önündeki pencerelerin kapattığı kısımları çıkar
				var segs = new List<(int L, int R)> { (Math.Max(r.Left, wa.Left), Math.Min(r.Right, wa.Right)) };
				foreach (var o in occluders)
				{
					if (o.Top > r.Top || o.Bottom <= r.Top) continue; // bu satırı kapatmıyor
					var next = new List<(int L, int R)>();
					foreach (var (l, rr) in segs)
					{
						if (o.Right <= l || o.Left >= rr) { next.Add((l, rr)); continue; }
						if (o.Left > l) next.Add((l, o.Left));
						if (o.Right < rr) next.Add((o.Right, rr));
					}
					segs = next;
				}
				foreach (var (l, rr) in segs)
					if (rr - l >= minWidth)
						result.Add(new Perch { Hwnd = h, Left = l - wa.Left, Right = rr - wa.Left, Alt = wa.Bottom - r.Top, WinLeft = r.Left });
			}
			// Sadece gerçek pencereler kenarları kapatır; menüler, ipucu kutuları, açılır listeler gibi küçük/araç
			// pencereleri sayılmaz (yoksa tarayıcıda bir menü açılınca tavuk durduk yere düşerdi)
			if (!tool && r.Right - r.Left >= 120 && r.Bottom - r.Top >= 60) occluders.Add(r);
		}
		return result;
	}
}
