// Saydam (katmanlı) pencere: tavuk, bilgi balonu ve havadayken yerdeki gölge için.
// Her karede içerik + konum + boyut TEK çağrıyla (UpdateLayeredWindow) güncellenir, böylece Electron'daki
// "eski kare sol üstte kalıyor" sorunu olmaz. Saydam piksellerde tıklama kendiliğinden alttaki pencereye geçer.
using static Kumes.Native.Win32;

namespace Kumes.Native;

sealed unsafe class LayeredWindow : WindowBase
{
	public int Width { get; }       // bitmap boyutu (pencere bundan küçük gösterilebilir)
	public int Height { get; }
	public int Stride => Width * 4;
	public byte* Bits { get; }      // BGRA, üstten alta satırlar, ön-çarpılmış alfa
	public bool Visible { get; private set; }

	public Action<int, int>? MouseMove;   // istemci koordinatı
	public Action? MouseLeave;
	public Action<int, int>? LeftDown;
	public Action<int, int>? LeftUp;
	public Action<int, int>? RightUp;     // ekran koordinatı
	public Action? CaptureLost;

	readonly nint screenDc, memDc, dib, oldBmp;
	bool tracking;

	public LayeredWindow(int width, int height, bool clickThrough, bool topmost = true)
	{
		Width = width;
		Height = height;
		EnsureClass("KumesLayer");
		uint ex = WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (topmost ? WS_EX_TOPMOST : 0) | (clickThrough ? WS_EX_TRANSPARENT : 0);
		Create(ex, "KumesLayer", "Kümes", WS_POPUP, 0, 0, width, height);

		screenDc = GetDC(0);
		memDc = CreateCompatibleDC(screenDc);
		var bmi = new BITMAPINFOHEADER
		{
			BiSize = (uint)sizeof(BITMAPINFOHEADER),
			BiWidth = width,
			BiHeight = -height, // negatif: üstten alta
			BiPlanes = 1,
			BiBitCount = 32,
		};
		void* bits;
		dib = CreateDIBSection(memDc, &bmi, 0, &bits, 0, 0);
		Bits = (byte*)bits;
		oldBmp = SelectObject(memDc, dib);
	}

	public void Clear() => new Span<byte>(Bits, Width * Height * 4).Clear();

	// Bitmap'in sol üst w×h kısmını (x, y) ekran konumunda göster
	public void Present(int x, int y, int w = 0, int h = 0)
	{
		var pos = new POINT { X = x, Y = y };
		var size = new SIZE { Cx = w > 0 ? w : Width, Cy = h > 0 ? h : Height };
		var src = new POINT();
		var blend = new BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
		UpdateLayeredWindow(Hwnd, screenDc, &pos, &size, memDc, &src, 0, &blend, ULW_ALPHA);
		if (!Visible)
		{
			ShowWindow(Hwnd, SW_SHOWNOACTIVATE);
			Visible = true;
		}
	}

	public void Hide()
	{
		if (!Visible) return;
		ShowWindow(Hwnd, SW_HIDE);
		Visible = false;
	}

	public void SetTopmost(bool on) =>
		SetWindowPos(Hwnd, on ? HWND_TOPMOST : -2 /* HWND_NOTOPMOST */, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

	// Bu pencereyi verilen pencerenin hemen altına koy (gölge tavuğun önüne geçmesin)
	public void PlaceBelow(nint other) => SetWindowPos(Hwnd, other, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

	protected override nint? OnMessage(uint msg, nuint wParam, nint lParam)
	{
		switch (msg)
		{
			case WM_MOUSEACTIVATE:
				return MA_NOACTIVATE; // tıklayınca odağı çalma
			case WM_MOUSEMOVE:
				if (!tracking)
				{
					var tme = new TRACKMOUSEEVENT { CbSize = (uint)sizeof(TRACKMOUSEEVENT), DwFlags = TME_LEAVE, HwndTrack = Hwnd };
					TrackMouseEvent(&tme);
					tracking = true;
				}
				MouseMove?.Invoke(LoWord(lParam), HiWord(lParam));
				return 0;
			case WM_MOUSELEAVE:
				tracking = false;
				MouseLeave?.Invoke();
				return 0;
			case WM_LBUTTONDOWN:
				LeftDown?.Invoke(LoWord(lParam), HiWord(lParam));
				return 0;
			case WM_LBUTTONUP:
				LeftUp?.Invoke(LoWord(lParam), HiWord(lParam));
				return 0;
			case WM_RBUTTONUP:
				GetCursorPos(out var p);
				RightUp?.Invoke(p.X, p.Y);
				return 0;
			case WM_CAPTURECHANGED:
				CaptureLost?.Invoke();
				return 0;
		}
		return null;
	}

	public override void Dispose()
	{
		SelectObject(memDc, oldBmp);
		DeleteObject(dib);
		DeleteDC(memDc);
		ReleaseDC(0, screenDc);
		base.Dispose();
	}
}
