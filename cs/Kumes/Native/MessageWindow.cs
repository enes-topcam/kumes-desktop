// Görünmez pencere: tray simgesinin sahibi, menülerin sahibi (menünün dışarı tıklayınca kapanması için
// odak alabilen bir pencere gerekiyor), arka plan işlerinin ana iş parçacığını uyandırması ve
// ikinci kez açılan Kümes'in "kendini göster" mesajı için.
using System.Collections.Concurrent;
using static Kumes.Native.Win32;

namespace Kumes.Native;

sealed unsafe class MessageWindow : WindowBase
{
	public const string ClassName = "KumesMsg";
	public const uint WM_WAKE = WM_APP + 1;
	public const uint WM_TRAY = WM_APP + 2;
	public const uint WM_SHOW_REQUEST = WM_APP + 3;
	public const uint WM_DEMO = WM_APP + 10; // tanıtım videosu çekimi: davranış komutu (sadece --cekim ile)
	public Action<int, int>? Demo;

	static readonly uint taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
	readonly ConcurrentQueue<Action> queue = new();

	public Action<uint>? TrayEvent;      // fare mesajı (WM_LBUTTONUP, WM_RBUTTONUP, NIN_BALLOONUSERCLICK...)
	public Action? TaskbarRecreated;     // explorer yeniden başladı: tray simgesini tekrar ekle
	public Action? ShowRequested;
	public Action? Tick;                 // menü açıkken / pencere sürüklenirken kareleri çizmek için

	// Menü veya pencere taşıma gibi Windows'un kendi döngüsünü çalıştırdığı anlarda ana döngümüz durur.
	// Bu sürede Windows zamanlayıcı mesajlarını yine dağıtır; kareleri oradan çizeriz ki tavuk donmasın.
	int modal;
	public void BeginModal() { if (modal++ == 0) SetTimer(Hwnd, 1, 25, 0); }
	public void EndModal() { if (modal > 0 && --modal == 0) KillTimer(Hwnd, 1); }
	public bool InModal => modal > 0;

	public MessageWindow()
	{
		EnsureClass(ClassName);
		Create(WS_EX_TOOLWINDOW, ClassName, "Kümes", WS_POPUP, 0, 0, 0, 0);
	}

	// Başka bir iş parçacığından ana iş parçacığında çalıştır
	public void Post(Action a)
	{
		queue.Enqueue(a);
		PostMessageW(Hwnd, WM_WAKE, 0, 0);
	}

	public void RunQueued()
	{
		while (queue.TryDequeue(out var a))
		{
			try { a(); }
			catch (Exception e) { Log.Write($"HATA (kuyruk): {e}"); }
		}
	}

	protected override nint? OnMessage(uint msg, nuint wParam, nint lParam)
	{
		if (msg == WM_WAKE) { RunQueued(); return 0; }
		if (msg == WM_TRAY) { TrayEvent?.Invoke((uint)(lParam & 0xFFFF)); return 0; }
		if (msg == WM_SHOW_REQUEST) { ShowRequested?.Invoke(); return 0; }
		if (msg == WM_DEMO) { Demo?.Invoke((int)wParam, (int)lParam); return 0; }
		if (msg == WM_TIMER && wParam == 1) { Tick?.Invoke(); return 0; }
		if (msg == taskbarCreated && taskbarCreated != 0) { TaskbarRecreated?.Invoke(); return 0; }
		return null;
	}
}

// Menü: her açılışta güncel durumla yeniden kurulur (eski tikler kalmasın)
sealed class PopupMenu
{
	const uint MF_POPUP = 0x10;
	readonly List<(string? Text, bool Enabled, bool Checked, Action? Click, PopupMenu? Sub)> items = new();

	public PopupMenu Item(string text, Action? click, bool enabled = true, bool isChecked = false)
	{
		items.Add((text, enabled, isChecked, click, null));
		return this;
	}

	public PopupMenu Submenu(string text, PopupMenu sub, bool enabled = true)
	{
		items.Add((text, enabled, false, null, sub));
		return this;
	}

	public PopupMenu Separator()
	{
		items.Add((null, false, false, null, null));
		return this;
	}

	// Alt menüler dahil tüm öğelere sırayla numara verir; seçilen numaranın eylemi actions'ta
	nint Build(List<Action?> actions)
	{
		var menu = CreatePopupMenu();
		foreach (var (text, enabled, isChecked, click, sub) in items)
		{
			if (text == null) { AppendMenuW(menu, MF_SEPARATOR, 0, null); continue; }
			uint state = (enabled ? 0 : MF_GRAYED) | (isChecked ? MF_CHECKED : 0);
			if (sub != null)
			{
				AppendMenuW(menu, MF_POPUP | state, (nuint)sub.Build(actions), text);
				continue;
			}
			actions.Add(click);
			AppendMenuW(menu, MF_STRING | state, (nuint)actions.Count, text);
		}
		return menu;
	}

	public void Show(nint owner, int x, int y)
	{
		var actions = new List<Action?>();
		var menu = Build(actions);
		SetForegroundWindow(owner); // yoksa dışarı tıklayınca menü kapanmaz
		int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, x, y, owner, 0);
		PostMessageW(owner, WM_NULL, 0, 0);
		DestroyMenu(menu); // alt menüleri de siler
		if (cmd > 0 && actions[cmd - 1] is Action a) a();
	}
}

// Bildirim alanı (tray) simgesi + Windows bildirimleri (balon -> Windows 10/11'de bildirim olarak çıkar)
sealed unsafe class TrayIcon : IDisposable
{
	readonly nint owner;
	nint icon;
	public nint BalloonIcon; // bildirimlerdeki büyük simge (yoksa tray simgesi)
	string tip = "Kümes";
	bool added;

	public TrayIcon(nint owner) { this.owner = owner; }

	NOTIFYICONDATAW Base(uint flags)
	{
		var d = new NOTIFYICONDATAW
		{
			CbSize = (uint)sizeof(NOTIFYICONDATAW),
			HWnd = owner,
			UID = 1,
			UFlags = flags,
			UCallbackMessage = MessageWindow.WM_TRAY,
			HIcon = icon,
		};
		Copy(tip, d.SzTip, 128);
		return d;
	}

	static void Copy(string s, char* dst, int max)
	{
		int n = Math.Min(s.Length, max - 1);
		for (int i = 0; i < n; i++) dst[i] = s[i];
		dst[n] = '\0';
	}

	public void Add()
	{
		var d = Base(NIF_MESSAGE | NIF_ICON | NIF_TIP);
		added = Shell_NotifyIconW(NIM_ADD, &d);
	}

	public void SetIcon(nint hIcon)
	{
		if (icon != 0 && icon != hIcon) DestroyIcon(icon);
		icon = hIcon;
		if (!added) return;
		var d = Base(NIF_ICON);
		Shell_NotifyIconW(NIM_MODIFY, &d);
	}

	public void SetTip(string text)
	{
		if (text == tip) return;
		tip = text;
		if (!added) return;
		var d = Base(NIF_TIP);
		Shell_NotifyIconW(NIM_MODIFY, &d);
	}

	public void Notify(string title, string body)
	{
		var d = Base(NIF_INFO);
		Copy(body, d.SzInfo, 256);
		Copy(title, d.SzInfoTitle, 64);
		var big = BalloonIcon != 0 ? BalloonIcon : icon;
		d.DwInfoFlags = big != 0 ? NIIF_USER | NIIF_LARGE_ICON : NIIF_INFO;
		d.HBalloonIcon = big;
		Shell_NotifyIconW(NIM_MODIFY, &d);
	}

	public void Dispose()
	{
		if (added)
		{
			var d = Base(0);
			Shell_NotifyIconW(NIM_DELETE, &d);
			added = false;
		}
		if (icon != 0) DestroyIcon(icon);
		icon = 0;
	}

	// 32 bit BGRA (düz alfa, üstten alta) görüntüden simge oluştur
	public static nint CreateIcon(byte[] bgra, int size)
	{
		var bmi = new BITMAPINFOHEADER { BiSize = (uint)sizeof(BITMAPINFOHEADER), BiWidth = size, BiHeight = -size, BiPlanes = 1, BiBitCount = 32 };
		var dc = GetDC(0);
		void* bits;
		var color = CreateDIBSection(dc, &bmi, 0, &bits, 0, 0);
		ReleaseDC(0, dc);
		fixed (byte* src = bgra) System.Buffer.MemoryCopy(src, bits, size * size * 4, size * size * 4);
		var mask = CreateBitmap(size, size, 1, 1, null);
		var info = new ICONINFO { FIcon = 1, HbmColor = color, HbmMask = mask };
		var h = CreateIconIndirect(&info);
		DeleteObject(color);
		DeleteObject(mask);
		return h;
	}
}
