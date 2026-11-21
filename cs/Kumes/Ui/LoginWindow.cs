// Steam giriş penceresi: QR kodu gösterir (src/login/login.html'in sade karşılığı).
// İçerik Direct2D ile bir bitmap'e çizilir, WM_PAINT'te pencereye kopyalanır.
using Kumes.Native;
using QRCoder;
using static Kumes.Native.Win32;

namespace Kumes.Ui;

sealed unsafe class LoginWindow : WindowBase
{
	const uint Style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
	readonly float s;
	readonly int cw, ch;
	readonly Surface2D surf;
	readonly nint memDc, dib, oldBmp;
	readonly byte* bits;
	bool[,]? qr;
	string message = "QR kod hazırlanıyor…";
	bool messageBad, done;

	public Action? Closed;
	public Action<bool>? Moving; // pencere sürüklenirken true (Windows kendi döngüsünü çalıştırır)

	public LoginWindow()
	{
		s = GetDpiForSystem() / 96f;
		cw = (int)(380 * s);
		ch = (int)(540 * s);
		EnsureClass("KumesLogin");
		var r = new RECT { Right = cw, Bottom = ch };
		AdjustWindowRectEx(&r, Style, false, 0);
		var wa = GetWorkArea();
		int w = r.Right - r.Left, h = r.Bottom - r.Top;
		Create(0, "KumesLogin", "Steam'e giriş", Style,
			wa.Left + (wa.Right - wa.Left - w) / 2, wa.Top + (wa.Bottom - wa.Top - h) / 2, w, h);
		Theme.ApplyTitleBar(Hwnd);

		var screen = GetDC(0);
		memDc = CreateCompatibleDC(screen);
		ReleaseDC(0, screen);
		var bmi = new BITMAPINFOHEADER { BiSize = (uint)sizeof(BITMAPINFOHEADER), BiWidth = cw, BiHeight = -ch, BiPlanes = 1, BiBitCount = 32 };
		void* p;
		dib = CreateDIBSection(memDc, &bmi, 0, &p, 0, 0);
		bits = (byte*)p;
		oldBmp = SelectObject(memDc, dib);
		surf = new Surface2D(cw, ch);
		Paint();
	}

	public void ShowAndFocus()
	{
		ShowWindow(Hwnd, SW_RESTORE);
		ShowWindow(Hwnd, SW_SHOW);
		BringWindowToTop(Hwnd);
		SetForegroundWindow(Hwnd);
	}

	public void SetQr(string url)
	{
		using var gen = new QRCodeGenerator();
		using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.L);
		var m = data.ModuleMatrix;
		qr = new bool[m.Count, m.Count];
		for (int y = 0; y < m.Count; y++)
			for (int x = 0; x < m.Count; x++)
				qr[y, x] = m[y][x];
		message = "";
		messageBad = false;
		Paint();
	}

	public void SetMessage(string text, bool bad = false)
	{
		message = text;
		messageBad = bad;
		Paint();
	}

	public void SetDone(string text)
	{
		done = true;
		message = text;
		messageBad = false;
		Paint();
	}

	void Paint()
	{
		var g = Gfx2D.Shared;
		surf.Begin();
		surf.FillRect(0, 0, cw, ch, Theme.Bg);
		float pad = 24 * s;
		surf.Text("Steam'e bağlan", pad, 20 * s, g.Font(24 * s, 700, Theme.Serif), Theme.Ink);
		surf.FillRect(pad + 1 * s, 56 * s, 28 * s, 3 * s, Theme.Straw); // Kümes penceresiyle aynı vurgu çizgisi
		var lead = g.Font(13 * s, 400, Theme.Sans);
		surf.Text("Tavuğunun yem ve büyüme bilgisi CS2'nin", pad, 66 * s, lead, Theme.Muted);
		surf.Text("sunucusundan okunuyor. Bir kere giriş yeter.", pad, 84 * s, lead, Theme.Muted);

		// QR kutusu (QR beyaz zeminde olmalı ki telefon okusun)
		float box = 260 * s, bx = (cw - box) / 2, by = 116 * s;
		surf.FillRoundRect(bx, by, box, box, 6 * s, Theme.White);
		surf.StrokeRoundRect(bx + 0.5f, by + 0.5f, box - 1, box - 1, 6 * s, Theme.Outline, Math.Max(1, s));
		if (done)
		{
			surf.TextCentered("✓", cw / 2f, by + box / 2 - 14 * s, g.Font(48 * s, 700, Theme.Sans), Theme.Leaf);
			surf.TextCentered("Giriş tamam", cw / 2f, by + box / 2 + 34 * s, g.Font(15 * s, 700, Theme.Serif), Theme.Ink);
		}
		else if (qr != null)
		{
			int n = qr.GetLength(0);
			float cell = MathF.Floor((box - 16 * s) / n); // tam piksel: keskin kenarlar
			float qx = MathF.Round(bx + (box - cell * n) / 2), qy = MathF.Round(by + (box - cell * n) / 2);
			for (int y = 0; y < n; y++)
				for (int x = 0; x < n; x++)
					if (qr[y, x]) surf.FillRect(qx + x * cell, qy + y * cell, cell, cell, 0x111111FF);
		}
		else
		{
			surf.TextCentered("QR kod hazırlanıyor…", cw / 2f, by + box / 2, g.Font(13 * s, 400, Theme.Sans), Theme.Muted);
		}

		// adımlar: Steam uygulamasındaki gerçek menü adlarıyla
		var step = g.Font(13 * s, 400, Theme.Sans);
		var num = g.Font(13 * s, 700, Theme.Serif);
		float sy = by + box + 22 * s;
		string[] steps =
		[
			"Telefonunda Steam uygulamasını aç.",
			"Alttaki kalkan simgesine (Steam Guard) dokun.",
			"\"QR kodu tara\"ya dokun, bu kodu okut, onayla.",
		];
		for (int i = 0; i < steps.Length; i++)
		{
			surf.Text($"{i + 1}", pad, sy, num, Theme.Straw);
			surf.Text(steps[i], pad + 20 * s, sy, step, Theme.Ink);
			sy += 24 * s;
		}
		if (message.Length > 0)
			surf.TextCentered(message, cw / 2f, ch - 30 * s, g.Font(12.5f * s, 400, Theme.Sans), messageBad ? Theme.Danger : Theme.Muted);
		surf.End();
		surf.CopyTo(bits, cw * 4);
		InvalidateRect(Hwnd, null, false);
	}

	protected override nint? OnMessage(uint msg, nuint wParam, nint lParam)
	{
		switch (msg)
		{
			case WM_PAINT:
				PAINTSTRUCT ps;
				var dc = BeginPaint(Hwnd, &ps);
				BitBlt(dc, 0, 0, cw, ch, memDc, 0, 0, SRCCOPY);
				EndPaint(Hwnd, &ps);
				return 0;
			case WM_ERASEBKGND:
				return 1;
			case WM_KEYDOWN when (int)wParam == VK_ESCAPE: // Esc ile kapat
				PostMessageW(Hwnd, WM_CLOSE, 0, 0);
				return 0;
			case WM_ENTERSIZEMOVE:
				Moving?.Invoke(true);
				return null;
			case WM_EXITSIZEMOVE:
				Moving?.Invoke(false);
				return null;
			case WM_CLOSE:
				Moving?.Invoke(false);
				Closed?.Invoke();
				Dispose();
				return 0;
		}
		return null;
	}

	public override void Dispose()
	{
		if (Hwnd == 0) return;
		SelectObject(memDc, oldBmp);
		DeleteObject(dib);
		DeleteDC(memDc);
		surf.Dispose();
		base.Dispose();
	}
}
