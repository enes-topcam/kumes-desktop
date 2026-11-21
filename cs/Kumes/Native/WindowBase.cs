// Tüm pencerelerin ortak tabanı: tek bir pencere işlevi (WndProc), mesajları ilgili nesneye dağıtır.
using System.Runtime.InteropServices;
using static Kumes.Native.Win32;

namespace Kumes.Native;

abstract unsafe class WindowBase : IDisposable
{
	static readonly Dictionary<nint, WindowBase> windows = new();
	static readonly HashSet<string> classes = new();

	public nint Hwnd { get; private set; }

	protected static void EnsureClass(string name, nint cursor = 0)
	{
		if (!classes.Add(name)) return;
		var (iconSm, icon) = Ui.Brand.WindowIcons(); // sınıf simgesi: görev çubuğu bunu da kullanır
		var wc = new WNDCLASSEXW
		{
			CbSize = (uint)sizeof(WNDCLASSEXW),
			LpfnWndProc = (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&Proc,
			HInstance = GetModuleHandleW(null),
			HIcon = icon,
			HIconSm = iconSm,
			HCursor = cursor != 0 ? cursor : LoadCursorW(0, IDC_ARROW),
			LpszClassName = Marshal.StringToHGlobalUni(name),
		};
		RegisterClassExW(&wc);
	}

	protected void Create(uint exStyle, string cls, string title, uint style, int x, int y, int w, int h)
	{
		Hwnd = CreateWindowExW(exStyle, cls, title, style, x, y, w, h, 0, 0, GetModuleHandleW(null), 0);
		if (Hwnd == 0) throw new InvalidOperationException($"Pencere oluşturulamadı ({cls}): {Marshal.GetLastWin32Error()}");
		windows[Hwnd] = this;
	}

	// Başlık çubuğu ve görev çubuğu simgesi (küçük ve büyük)
	public void SetIcon(nint small, nint big)
	{
		if (Hwnd == 0) return;
		if (small != 0) SendMessageW(Hwnd, WM_SETICON, 0, small);
		if (big != 0) SendMessageW(Hwnd, WM_SETICON, 1, big);
	}

	// null dönerse varsayılan işleme bırakılır
	protected abstract nint? OnMessage(uint msg, nuint wParam, nint lParam);

	[UnmanagedCallersOnly]
	static nint Proc(nint hwnd, uint msg, nuint wParam, nint lParam)
	{
		try
		{
			if (windows.TryGetValue(hwnd, out var w))
			{
				var r = w.OnMessage(msg, wParam, lParam);
				if (r.HasValue) return r.Value;
			}
		}
		catch (Exception e)
		{
			Log.Write($"pencere hatası: {e}");
		}
		return DefWindowProcW(hwnd, msg, wParam, lParam);
	}

	public virtual void Dispose()
	{
		if (Hwnd == 0) return;
		windows.Remove(Hwnd);
		DestroyWindow(Hwnd);
		Hwnd = 0;
	}
}
