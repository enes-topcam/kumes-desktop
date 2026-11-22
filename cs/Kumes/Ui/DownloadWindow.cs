// Tavuk modeli hazırlanırken açılan pencere. CS2 kurulu değilse: önce izin sorar, sonra Valve'dan (anonim Steam girişiyle)
// indirir; hangi adımda olduğu, kaç MB indiği, ilerleme çubuğu. CS2 kuruluysa (local): dosyalar kurulumdan çıkarılır, bir şey
// inmez; adım ve çubuk gösterilir. Bitince "hazır" der ve App kapatır; hata olursa "Tekrar dene". Kapatılırsa arka planda sürer.
// Görünüm giriş penceresiyle aynı.
using System.Globalization;
using Kumes.Native;
using static Kumes.Native.Win32;
using static Kumes.Lang;

namespace Kumes.Ui;

sealed unsafe class DownloadWindow : WindowBase
{
	const uint Style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
	const uint Track = 0x2A2A3AFFu, StrawHot = 0xCDB9F4FFu, ButtonBg = 0x1B1C29FFu;
	// İlk indirmede gelen toplam (ölçüm, 22 Kasım 2026: bütün modeller + sadece demo ve civciv renkleri ~48,5 MB; eskiden
	// bütün renkler ~108 MB). Hesap eklenince o tavuğun rengi ayrıca iner (~5 MB). Çubuk buna göre dolar.
	public const long ExpectedBytes = 50L * 1024 * 1024;

	static readonly string[] Steps = ["Modeller", "Animasyonlar ve iskelet", "Malzemeler", "Renkler (dokular)"];

	readonly float s;
	readonly int cw, ch;
	readonly Surface2D surf;
	readonly nint memDc, dib, oldBmp;
	readonly byte* bits;
	readonly List<(string Id, float X, float Y, float W, float H)> hits = new();
	string? hover;

	int step;          // 1-4 indirme adımları, 5 hazırlanıyor
	long bytes;
	bool done;
	string? error;
	bool ask;          // indirmeden önce izin soruluyor (İndir / Şimdi değil)
	bool local;        // CS2 kurulumundan çıkarılıyor (indirme yok)
	string localText = "";
	int localStep, localTotal;

	public Action? Closed, Retry, Accept;
	public Action<bool>? Moving;

	public DownloadWindow()
	{
		s = GetDpiForSystem() / 96f;
		cw = (int)(420 * s);
		ch = (int)(262 * s);
		EnsureClass("KumesDownload");
		var r = new RECT { Right = cw, Bottom = ch };
		AdjustWindowRectEx(&r, Style, false, 0);
		var wa = GetWorkArea();
		int w = r.Right - r.Left, h = r.Bottom - r.Top;
		// en üstte: uygulamanın diğer pencerelerinin (ve başka uygulamaların) arkasında kalmasın
		Create(WS_EX_TOPMOST, "KumesDownload", "Kümes", Style, wa.Left + (wa.Right - wa.Left - w) / 2, wa.Top + (wa.Bottom - wa.Top - h) / 2, w, h);
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
		ShowWindow(Hwnd, SW_SHOW);
		BringWindowToTop(Hwnd);
		SetForegroundWindow(Hwnd);
	}

	public void SetAsk() { ask = true; error = null; Paint(); }
	public void SetLocal(string text, int n, int total) { local = true; ask = false; error = null; localText = text; localStep = n; localTotal = total; Paint(); }
	public void SetStep(int n) { step = n; error = null; ask = false; Paint(); }
	public void AddBytes(long n) { bytes += n; Paint(); }
	public void SetDone() { done = true; error = null; Paint(); }
	public void SetFailed(string message) { error = message; Paint(); }

	// Yeniden denemeye başlarken (Tekrar dene)
	public void Restart() { step = 0; bytes = 0; done = false; error = null; ask = false; local = false; localText = ""; localStep = 0; Paint(); }

	static string Mb(long b) => (b / 1048576.0).ToString("0.0", Lang.English ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo("tr-TR"));

	void Paint()
	{
		if (Hwnd == 0) return;
		var g = Gfx2D.Shared;
		hits.Clear();
		surf.Begin();
		surf.FillRect(0, 0, cw, ch, Theme.Bg);
		float pad = 24 * s, y = 20 * s;
		if (ask) { PaintAsk(g, pad, y); return; }
		string title = done ? T("Tavuğun hazır")
			: local ? (error != null ? T("Tavuk modeli hazırlanamadı") : T("Tavuk modeli hazırlanıyor"))
			: error != null ? T("İndirme tamamlanamadı") : T("Tavuk dosyaları indiriliyor");
		surf.Text(title, pad, y, g.Font(20 * s, 700, Theme.Serif), Theme.Ink);
		surf.FillRect(pad + 1 * s, y + 34 * s, 28 * s, 3 * s, done ? Theme.Leaf : error != null ? Theme.Danger : Theme.Straw);

		var lead = g.Font(13 * s, 400, Theme.Sans);
		y += 46 * s;
		if (local)
		{
			surf.Text(T("Tavuğun modeli ve renkleri bilgisayarındaki CS2'den"), pad, y, lead, Theme.Muted);
			surf.Text(T("çıkarılıyor. İnternetten bir şey inmez."), pad, y + 18 * s, lead, Theme.Muted);
		}
		else
		{
			surf.Text(T("CS2 kurulu olmadığı için tavuğun modeli ve renkleri"), pad, y, lead, Theme.Muted);
			surf.Text(T("Valve'ın sunucusundan indiriliyor. Sadece bir kere iner."), pad, y + 18 * s, lead, Theme.Muted);
		}

		// adım
		y += 54 * s;
		var stepFont = g.Font(13.5f * s, 600, Theme.Sans);
		string stepText = done ? T("Tamamlandı")
			: local ? (localText.Length > 0 ? localText : T("CS2 dosyaları açılıyor…"))
			: step is >= 1 and <= 4 ? Tf("Adım {0}/4 · {1}", step, T(Steps[step - 1]))
			: step == 5 ? T("Dosyalar hazırlanıyor…")
			: T("Steam'e bağlanılıyor…");
		surf.Text(stepText, pad, y, stepFont, done ? Theme.Leaf : Theme.Ink);

		// çubuk + yüzde
		y += 28 * s;
		float frac = done ? 1 : local ? (localTotal > 0 ? Math.Min(0.98f, (localStep - 0.5f) / localTotal) : 0) : Math.Min(0.98f, (float)bytes / ExpectedBytes);
		var pctFont = g.Font(13 * s, 600, Theme.Sans);
		string pct = Tf("%{0}", (int)Math.Round(frac * 100));
		float pw = g.Measure(pct, pctFont).X;
		float bw = cw - 2 * pad - pw - 12 * s, bh = 8 * s;
		surf.FillRoundRect(pad, y, bw, bh, bh / 2, Track);
		if (frac > 0) surf.FillRoundRect(pad, y, Math.Max(bh, bw * frac), bh, bh / 2, done ? Theme.Leaf : error != null ? Theme.Danger : Theme.Straw);
		surf.Text(pct, cw - pad - pw, y - 6 * s, pctFont, Theme.Ink);

		// MB
		y += 18 * s;
		var small = g.Font(12.5f * s, 400, Theme.Sans);
		surf.Text(local ? T("Sadece ilk açılışta, yaklaşık 20 saniye sürer.") : Tf("{0} MB / yaklaşık {1} MB", Mb(bytes), ExpectedBytes / 1048576), pad, y, small, Theme.Muted);

		// alt: hata ve düğmeler ya da bilgi
		float by = ch - 52 * s;
		if (error != null)
		{
			surf.Text(error.Length > 58 ? error[..58] + "…" : error, pad, by - 26 * s, small, Theme.Danger);
			Button("retry", T("Tekrar dene"), pad, by, 150 * s, 36 * s, true);
			Button("close", T("Kapat"), pad + 162 * s, by, 110 * s, 36 * s, false);
		}
		else if (!done)
		{
			surf.Text(local ? T("Bu pencereyi kapatabilirsin, arka planda sürer.") : T("Bu pencereyi kapatabilirsin, indirme arka planda sürer."), pad, by + 10 * s, small, Theme.Muted);
		}
		surf.End();
		surf.CopyTo(bits, cw * 4);
		InvalidateRect(Hwnd, null, false);
	}

	// İzin: ne inecek, ne kadar, nereden; İndir'e basılınca aynı pencerede indirme başlar
	void PaintAsk(Gfx2D g, float pad, float y)
	{
		surf.Text(T("Tavuk modeli indirilsin mi?"), pad, y, g.Font(20 * s, 700, Theme.Serif), Theme.Ink);
		surf.FillRect(pad + 1 * s, y + 34 * s, 28 * s, 3 * s, Theme.Straw);
		var lead = g.Font(13 * s, 400, Theme.Sans);
		y += 46 * s;
		surf.Text(T("CS2 kurulu olmadığı için tavuğun modeli ve renkleri"), pad, y, lead, Theme.Muted);
		surf.Text(T("Valve'ın sunucusundan indirilmeli."), pad, y + 18 * s, lead, Theme.Muted);
		y += 54 * s;
		var info = g.Font(13.5f * s, 600, Theme.Sans);
		surf.Text(Tf("Yaklaşık {0} MB, sadece bir kere iner.", ExpectedBytes / 1048576), pad, y, info, Theme.Ink);
		surf.Text(T("Steam hesabı gerekmez."), pad, y + 22 * s, g.Font(12.5f * s, 400, Theme.Sans), Theme.Muted);
		float by = ch - 52 * s;
		Button("accept", T("İndir"), pad, by, 150 * s, 36 * s, true);
		Button("close", T("Şimdi değil"), pad + 162 * s, by, 130 * s, 36 * s, false);
		surf.End();
		surf.CopyTo(bits, cw * 4);
		InvalidateRect(Hwnd, null, false);
	}

	void Button(string id, string text, float x, float y, float w, float h, bool primary)
	{
		var f = Gfx2D.Shared.Font(14 * s, 600, Theme.Sans);
		bool hot = hover == id;
		if (primary)
		{
			surf.FillRoundRect(x, y, w, h, 9 * s, hot ? StrawHot : Theme.Straw);
			surf.TextCentered(text, x + w / 2, y + h / 2, f, Theme.Bg);
		}
		else
		{
			if (hot) surf.FillRoundRect(x, y, w, h, 9 * s, ButtonBg);
			surf.StrokeRoundRect(x + 0.5f, y + 0.5f, w - 1, h - 1, 9 * s, Theme.Outline, Math.Max(1, s));
			surf.TextCentered(text, x + w / 2, y + h / 2, f, hot ? Theme.Ink : Theme.Muted);
		}
		hits.Add((id, x, y, w, h));
	}

	string? HitAt(nint lParam)
	{
		float x = LoWord(lParam), y = HiWord(lParam);
		foreach (var h in hits)
			if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) return h.Id;
		return null;
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
			case WM_MOUSEMOVE:
			{
				var tme = new TRACKMOUSEEVENT { CbSize = (uint)sizeof(TRACKMOUSEEVENT), DwFlags = TME_LEAVE, HwndTrack = Hwnd };
				TrackMouseEvent(&tme);
				var id = HitAt(lParam);
				if (id != hover) { hover = id; Paint(); }
				return 0;
			}
			case WM_MOUSELEAVE:
				if (hover != null) { hover = null; Paint(); }
				return 0;
			case WM_SETCURSOR when (nint)wParam == Hwnd && (lParam & 0xFFFF) == 1:
				if (hover == null) return null;
				SetCursor(LoadCursorW(0, IDC_HAND));
				return 1;
			case WM_LBUTTONDOWN:
				switch (HitAt(lParam))
				{
					case "retry": Retry?.Invoke(); break;
					case "accept": Accept?.Invoke(); break;
					case "close": PostMessageW(Hwnd, WM_CLOSE, 0, 0); break;
				}
				return 0;
			case WM_KEYDOWN when (int)wParam == VK_ESCAPE:
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
