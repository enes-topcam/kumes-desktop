// Kümes penceresi (guncelleme/desktop-app.md, onaylı görselin sağ üstteki penceresi): sade, koyu mor-gri, düz yüzeyler.
// Solda sadece Ana Sayfa · Koleksiyon · Ayarlar; altta Steam bağlantısı ve sürüm. Hakkında, Ayarlar'ın içinde.
// Ana Sayfa: seçili tavuğun kimliği, tek büyük yem sayacı + ince leylak çizgi, iki kısa metrik, güncelleme durumu.
// Koleksiyon: bağlı her Steam hesabının tavuğu (+ ile hesap eklenir), satıra tıklayınca tavuk detayı.
// Kenarlıksız pencere: başlık çubuğu düğmelerini kendimiz çiziyoruz. Her şey 1000x680'lik tasarım tuvaline çizilip
// pencereye ölçeklenir.
using System.Numerics;
using System.Runtime.InteropServices;
using Kumes.Native;
using Kumes.Steam;
using Vortice.Direct2D1;
using static Kumes.Native.Win32;

namespace Kumes.Ui;

// Bir Steam hesabı ve tavuğu (pencerede gösterilecek hali)
sealed record AccountModel(
	string Id,
	string Name,
	PetInfo? Pet,
	string State,          // idle, fetching, ok, error, need-login, cs2, waiting
	string Message,
	byte[]? Avatar, int AvatarW, int AvatarH,
	byte[]? Portrait, int PortraitSize,
	IReadOnlyList<FoodSample> History,
	string? Coat);

// Pencerede gösterilecek her şey (App her değişiklikte yeniden verir)
sealed record MainModel(
	IReadOnlyList<AccountModel> Accounts,
	string? SelectedId,
	bool Fetching,
	Settings Settings,
	bool Autostart,
	string? Extracting,
	string Version);

[StructLayout(LayoutKind.Sequential)]
struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

sealed unsafe class MainWindow : WindowBase
{
	// ---- tasarım tuvali ve renkler (guncelleme/design-system.md) ----
	const float DW = 1000, DH = 680, Side = 220, X0 = Side + 24, CW = DW - X0 - 24, Top = 40, CardTop = 52, CardH = DH - CardTop - 24;
	const uint Bg = 0x10111AFF, NavBg = 0x171722FF, Surface = 0x1D1E2AFF, Raised = 0x272635FF, Line = 0x323240FF,
		Text = 0xF5F3F7FF, Muted = 0xAAA9BCFF, Faint = 0x77768AFF, Lavender = 0xBAA0ECFF, OnLavender = 0x1A1426FF,
		Positive = 0x79CBA5FF, Error = 0xE27C86FF, Track = 0x34334AFF, Chicken = 0xE1AF74FF;
	const string Icons = "Segoe Fluent Icons", Display = "Segoe UI Variable Display", Body = "Segoe UI Variable Text";
	// çizgi simgeler (SvgIcons; mobille aynı set)
	const string GHome = SvgIcons.Home, GLayers = SvgIcons.Layers, GGear = SvgIcons.Gear, GRefresh = SvgIcons.Refresh, GAdd = SvgIcons.Plus,
		GChevron = SvgIcons.ChevronRight, GBack = SvgIcons.Back, GCalendar = SvgIcons.Calendar, GCheck = SvgIcons.CheckCircle,
		GAlert = SvgIcons.AlertCircle, GInfo = SvgIcons.Info, GSignOut = SvgIcons.Logout, GFolder = SvgIcons.Folder, GDoc = SvgIcons.File,
		GOpen = SvgIcons.External, GBox = SvgIcons.Box, GPower = SvgIcons.Power, GChicken = SvgIcons.Chicken;
	static readonly (string Id, string Label, string Glyph)[] Nav =
		[("home", "Ana Sayfa", GHome), ("collection", "Koleksiyon", GLayers), ("settings", "Ayarlar", GGear)];

	const uint Style = WS_POPUP | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
	readonly float dpi;
	int cw, ch;
	float k = 1, ox, oy; // tasarım tuvalinin pencereye ölçeği ve kayması
	Surface2D? surf;
	nint memDc, dib, oldBmp;
	byte* bits;
	MainModel model;
	public string Page { get; private set; } = "home"; // home, collection, detail:<hesap>, settings
	float scroll, maxScroll;

	// görseller (Direct2D bitmap; kaynak değişince yeniden oluşturulur)
	ID2D1Bitmap? logo, mark, steamBadge;
	readonly Dictionary<string, (byte[] Src, ID2D1Bitmap Bmp)> images = new();

	readonly List<(float X, float Y, float W, float H, string Id, bool Enabled)> items = new();
	string? hover, pressed, focus;
	bool focusVisible, tracking;
	float clipTop; // kaydırılan sayfada bu çizginin üstündeki öğeler tıklanmaz

	public Action? Closed;
	public Action<bool>? Moving;
	public Action<string>? Clicked; // App'e giden eylemler
	public bool WantsCollection => Page == "collection";

	public MainWindow(MainModel m)
	{
		model = m;
		dpi = GetDpiForSystem() / 96f;
		var wa = GetWorkArea();
		float fit = Math.Min(dpi, Math.Min((wa.Right - wa.Left) * 0.94f / DW, (wa.Bottom - wa.Top) * 0.94f / DH));
		int w = (int)(DW * fit), h = (int)(DH * fit);
		EnsureClass("KumesMain");
		Create(WS_EX_APPWINDOW, "KumesMain", "Kümes", Style,
			wa.Left + (wa.Right - wa.Left - w) / 2, wa.Top + (wa.Bottom - wa.Top - h) / 2, w, h);
		int corner = 2; // DWMWCP_ROUND: Windows 11 yuvarlak köşe
		DwmSetWindowAttribute(Hwnd, 33, &corner, sizeof(int));
		Resize(w, h);
	}

	public void ShowAndFocus()
	{
		ShowWindow(Hwnd, IsIconic(Hwnd) ? SW_RESTORE : SW_SHOW);
		BringWindowToTop(Hwnd);
		SetForegroundWindow(Hwnd);
	}

	public void Update(MainModel m)
	{
		model = m;
		if (Page.StartsWith("detail:") && Acc(Page[7..]) == null) Page = "collection"; // hesap kaldırıldı
		Paint();
	}

	AccountModel? Acc(string? id) => model.Accounts.FirstOrDefault(a => a.Id == id);
	AccountModel? Selected => Acc(model.SelectedId) ?? model.Accounts.FirstOrDefault();

	void Resize(int w, int h)
	{
		if (w <= 0 || h <= 0) return;
		FreeSurface();
		cw = w; ch = h;
		k = Math.Min(cw / DW, ch / DH);
		ox = (cw - DW * k) / 2; oy = (ch - DH * k) / 2;
		var screen = GetDC(0);
		memDc = CreateCompatibleDC(screen);
		ReleaseDC(0, screen);
		var bmi = new BITMAPINFOHEADER { BiSize = (uint)sizeof(BITMAPINFOHEADER), BiWidth = cw, BiHeight = -ch, BiPlanes = 1, BiBitCount = 32 };
		void* p;
		dib = CreateDIBSection(memDc, &bmi, 0, &p, 0, 0);
		bits = (byte*)p;
		oldBmp = SelectObject(memDc, dib);
		surf = new Surface2D(cw, ch);
		if (Brand.Png("logo_symbol.png") is var (lb, lw, lh)) logo = surf.CreateImage(lb, lw, lh);
		// Kümes işareti (yumurta + tavuk): sol üst, portre yer tutucusu, metrik simgesi
		if (Brand.Png("logo_mark.png") is var (mb, mw, mh)) mark = surf.CreateImage(mb, mw, mh);
		if (Brand.Png("steam.png") is var (sb, sw, sh)) steamBadge = surf.CreateImage(sb, sw, sh);
		Paint();
	}

	void FreeSurface()
	{
		logo?.Dispose(); logo = null;
		mark?.Dispose(); mark = null;
		steamBadge?.Dispose(); steamBadge = null;
		foreach (var v in images.Values) v.Bmp.Dispose();
		images.Clear();
		surf?.Dispose(); surf = null;
		if (memDc != 0)
		{
			SelectObject(memDc, oldBmp);
			DeleteObject(dib);
			DeleteDC(memDc);
			memDc = 0;
		}
	}

	// BGRA baytlarından bitmap (kaynak değişmedikçe yeniden oluşturulmaz)
	ID2D1Bitmap? Image(string key, byte[]? data, int w, int h)
	{
		if (data == null || w <= 0) return null;
		if (images.TryGetValue(key, out var e))
		{
			if (ReferenceEquals(e.Src, data)) return e.Bmp;
			e.Bmp.Dispose();
		}
		var bmp = surf!.CreateImage(data, w, h);
		images[key] = (data, bmp);
		return bmp;
	}

	// ---------- çizim ----------

	Surface2D S => surf!;
	Gfx2D G => Gfx2D.Shared;
	Matrix3x2 Base => Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(ox, oy);

	public void Paint()
	{
		if (surf == null) return;
		items.Clear();
		S.Begin();
		S.FillRect(0, 0, cw, ch, Bg);
		S.Transform(Base);

		// sayfa içeriği: başlık şeridinin altında kırpılır, tekerlekle kaydırılır
		clipTop = Top;
		S.PushClip(Side, Top, DW - Side, DH - Top);
		float y = CardTop + 4 - scroll, end;
		if (Page == "home") end = PaintHome(y);
		else if (Page == "collection") end = PaintCollection(y);
		else if (Page == "settings") end = PaintSettings(y);
		else if (Page.StartsWith("detail:")) end = PaintDetail(y, Page[7..]);
		else end = y;
		S.PopClip();
		maxScroll = Math.Max(0, end + scroll - DH + 28);
		clipTop = 0;

		PaintSidebar();
		PaintChrome();

		if (focusVisible && focus != null && items.FirstOrDefault(i => i.Id == focus) is { Id: not null } fi)
			S.StrokeRoundRect(fi.X - 3, fi.Y - 3, fi.W + 6, fi.H + 6, 10, Lavender, 2);

		S.ResetTransform();
		S.End();
		S.CopyTo(bits, cw * 4);
		InvalidateRect(Hwnd, null, false);
	}

	void Item(float x, float y, float w, float h, string id, bool enabled = true)
	{
		if (clipTop > 0 && (y + h < clipTop || y > DH)) return; // kaydırılıp görünmeyen
		items.Add((x, y, w, h, id, enabled));
	}

	bool Hot(string id) => hover == id;
	bool Down(string id) => pressed == id;

	// çizgi simge, (cx, cy) merkezli; size: görünen boy
	void Glyph(string icon, float cx, float cy, float size, uint color) => S.Icon(icon, cx - size * 0.6f, cy - size * 0.6f, size * 1.2f, color);
	void ChromeGlyph(string glyph, float cx, float cy, float size, uint color) => S.TextCentered(glyph, cx, cy, G.Font(size, 400, Icons), color);
	Vortice.DirectWrite.IDWriteTextFormat F(float size, int weight = 400) => G.Font(size, weight, Body);
	Vortice.DirectWrite.IDWriteTextFormat D(float size, int weight = 600) => G.Font(size, weight, Display);
	float W(string text, Vortice.DirectWrite.IDWriteTextFormat f) => G.Measure(text, f).X;

	// ---- sol menü ----
	void PaintSidebar()
	{
		S.FillRect(0, 0, Side, DH, NavBg);
		S.FillRect(Side - 1, 0, 1, DH, 0xFFFFFF0A);
		if (mark != null) S.DrawImage(mark, 22, 20, 32, 32);
		S.Text("Kümes", 62, 22, D(20, 600), Text);

		float y = 86;
		foreach (var (id, label, glyph) in Nav)
		{
			string nid = "nav:" + id;
			bool active = Page == id || (id == "collection" && Page.StartsWith("detail:"));
			if (active) S.FillRoundRect(14, y, Side - 28, 44, 12, Raised);
			else if (Hot(nid)) S.FillRoundRect(14, y, Side - 28, 44, 12, 0xFFFFFF0A);
			Glyph(glyph, 40, y + 22, 17, active ? Text : Muted);
			S.Text(label, 64, y + 12, F(14.5f, active ? 600 : 400), active ? Text : Muted);
			Item(14, y, Side - 28, 44, nid);
			y += 52;
		}

		// altta: Steam bağlantısı ve sürüm
		S.FillRect(20, DH - 108, Side - 40, 1, Line);
		int n = model.Accounts.Count;
		bool problem = model.Accounts.Any(a => a.State is "error" or "need-login");
		uint dot = n == 0 ? Faint : problem ? Error : Positive;
		string steam = n == 0 ? "Steam bağlı değil" : problem ? "Bağlantı sorunu" : n == 1 ? "Steam Bağlı" : $"{n} Steam hesabı";
		if (steamBadge != null) S.DrawImage(steamBadge, 22, DH - 90, 26, 26);
		S.FillEllipse(45, DH - 66, 4, 4, NavBg);
		S.FillEllipse(45, DH - 66, 3, 3, dot);
		S.Text(steam, 58, DH - 87, F(13.5f, 600), Text);
		S.Text($"v{model.Version}", 24, DH - 46, F(12.5f), Faint);
	}

	// ---- başlık çubuğu düğmeleri ----
	void PaintChrome()
	{
		bool max = IsZoomed(Hwnd);
		(string Id, string Glyph)[] btns = [("min", ""), ("max", max ? "" : ""), ("close", "")];
		float x = DW - 46 * 3;
		foreach (var (id, glyph) in btns)
		{
			if (Hot(id)) S.FillRect(x, 0, 46, 34, id == "close" ? 0xC42B1CFF : 0xFFFFFF14);
			ChromeGlyph(glyph, x + 23, 17, 10, Text);
			Item(x, 0, 46, 34, id);
			x += 46;
		}
	}

	// ---- ortak parçalar ----

	float PageTitle(float y, string title, string? sub)
	{
		S.Text(title, X0, y, D(28, 700), Text);
		if (sub != null) S.Text(sub, X0 + 1, y + 40, F(14), Muted);
		return y + (sub != null ? 76 : 50);
	}

	void Pill(float right, float y, string text, uint dot)
	{
		var f = F(12.5f, 500);
		float w = 30 + W(text, f);
		S.FillRoundRect(right - w, y, w, 26, 13, Raised);
		S.FillEllipse(right - w + 13, y + 13, 3.5f, 3.5f, dot);
		S.Text(text, right - w + 22, y + 5, f, Text);
	}

	void Bar(float x, float y, float w, float h, float frac, uint color)
	{
		S.FillRoundRect(x, y, w, h, h / 2, Track);
		if (frac > 0) S.FillRoundRect(x, y, Math.Max(h, w * Math.Clamp(frac, 0, 1)), h, h / 2, color);
	}

	// Tavuk portresi: 3D modelden çizilen resim, yoksa sade tavuk işareti
	void Portrait(AccountModel a, float x, float y, float size, float r)
	{
		S.FillRoundRect(x, y, size, size, r, Raised);
		if (Image("p:" + a.Id, a.Portrait, a.PortraitSize, a.PortraitSize) is { } bmp)
		{
			S.PushClip(x, y, size, size);
			S.DrawImage(bmp, x - size * 0.06f, y - size * 0.02f, size * 1.12f, size * 1.12f);
			S.PopClip();
		}
		else if (mark != null) S.DrawImage(mark, x + size * 0.2f, y + size * 0.2f, size * 0.6f, size * 0.6f, 0.35f);
	}

	void Avatar(AccountModel a, float x, float y, float size)
	{
		if (Image("a:" + a.Id, a.Avatar, a.AvatarW, a.AvatarH) is { } bmp)
		{
			S.DrawImage(bmp, x, y, size, size);
			S.StrokeRoundRect(x - 1.5f, y - 1.5f, size + 3, size + 3, size / 2 + 1.5f, Surface, 4); // köşeleri yuvarla
		}
		else
		{
			S.FillEllipse(x + size / 2, y + size / 2, size / 2, size / 2, Raised);
			S.TextCentered(a.Name[..1].ToUpperInvariant(), x + size / 2, y + size / 2, F(size * 0.42f, 600), Text);
		}
	}

	void Button(float x, float y, float w, float h, string glyph, string label, string id, bool primary = false, bool danger = false, bool enabled = true)
	{
		bool hot = Hot(id) && enabled, down = Down(id);
		uint bg = primary ? (down ? 0xA88CDCFFu : hot ? 0xC7B1F2FFu : Lavender) : (down ? 0x1A1A25FFu : hot ? Raised : Surface);
		S.FillRoundRect(x, y, w, h, 10, bg);
		if (!primary) S.StrokeRoundRect(x + 0.5f, y + 0.5f, w - 1, h - 1, 10, Line, 1);
		uint c = !enabled ? Faint : primary ? OnLavender : danger ? Error : Text;
		var f = F(13.5f, 600);
		float tw = W(label, f) + (glyph.Length > 0 ? 24 : 0), lx = x + (w - tw) / 2;
		if (glyph.Length > 0) Glyph(glyph, lx + 8, y + h / 2, 13, c);
		S.Text(label, lx + (glyph.Length > 0 ? 24 : 0), y + (h - 19) / 2, f, c);
		Item(x, y, w, h, id, enabled);
	}

	static string Subtitle(AccountModel a)
	{
		var p = a.Pet!;
		var parts = new[] { p.Breed, p.StageName, a.Coat }.Where(v => !string.IsNullOrEmpty(v));
		return string.Join(" · ", parts);
	}

	static string ShortLeft(TimeSpan? t) => t switch
	{
		null => "–",
		{ } x when x <= TimeSpan.Zero => "Bitti",
		{ Days: > 0 } x => $"{x.Days} gün",
		{ Hours: > 0 } x => $"{x.Hours} saat",
		{ } x => $"{Math.Max(1, x.Minutes)} dk",
	};

	(string Text, string Glyph, uint Color) SyncText(AccountModel a)
	{
		var ago = a.Pet?.FetchedAt is { } t ? Fmt.Ago(t) : null;
		return a.State switch
		{
			"fetching" => ("Güncelleniyor…", GRefresh, Muted),
			"need-login" => ("Tekrar giriş gerekli", GAlert, Error),
			"cs2" => (ago != null ? $"CS2 açık · son güncelleme {ago}" : "CS2 açık, kapanınca güncellenecek", GInfo, Muted),
			"waiting" => (a.Message, GInfo, Muted),
			"error" => (ago != null ? $"Güncellenemedi · son güncelleme {ago}" : "Güncellenemedi", GAlert, Error),
			_ => (ago == null ? "Henüz güncellenmedi" : ago == "az önce" ? "Az önce güncellendi" : $"{ago} güncellendi", GCheck, Positive),
		};
	}

	// Tavuk kartı (Ana Sayfa ve detay; görseldeki düzen): üstte kimlik, ortada sayaç bloğu, iki metrik kartı,
	// altta güncelleme durumu. h: kartın yüksekliği (Ana Sayfa'da pencereyi doldurur). Yüksekliği döner.
	float PetCard(AccountModel a, float x, float y, float w, float h)
	{
		var p = a.Pet!;
		var s = PetSummary.From(p, DateTimeOffset.Now);
		S.FillRoundRect(x, y, w, h, 20, Surface);
		float pad = 22, cx = x + pad, cr = x + w - pad;

		// kimlik
		const float pt = 128;
		Portrait(a, cx, y + pad, pt, 18);
		S.Text(p.Name ?? "Tavuğum", cx + pt + 24, y + pad + 32, D(34, 700), Text);
		S.Text(Subtitle(a), cx + pt + 26, y + pad + 80, F(15.5f), Muted);
		if (a.State == "need-login") Pill(cr, y + pad + 4, "Giriş gerekli", Error);
		else Pill(cr, y + pad + 4, "Takipte", Positive);

		// alttan yukarı: güncelleme satırı, metrik kartları; kalan alan sayaç bloğu
		float syncH = 52, metricH = 104, gap = 16;
		float syncY = y + h - pad - syncH;
		float metricY = syncY - gap - metricH;
		float blockY = y + pad + pt + 18, blockH = metricY - gap - blockY;

		// sayaç bloğu
		S.FillRoundRect(cx, blockY, cr - cx, blockH, 16, Raised);
		float bx = cx + 26, br = cr - 26;
		float big = Math.Clamp(blockH * 0.30f, 40, 64);
		float ty = blockY + (blockH - (32 + big + 26 + 10 + 34)) / 2; // içerik dikeyde ortada
		S.Text("Yemin bitmesine", bx, ty, F(16), Muted);
		if (s.FoodLeft is not { } left)
			S.Text("Yem bilgisi yok", bx, ty + 30, D(big * 0.8f), Text);
		else if (left <= TimeSpan.Zero)
		{
			S.Text("Yem bitti", bx, ty + 28, D(big), Error);
			S.Text($"Bitti: {Fmt.DateLong(p.FoodExpiresAt!.Value)}", bx, ty + 32 + big + 40, F(14), Muted);
		}
		else
		{
			S.Text(Fmt.DurationLong(left), bx, ty + 26, D(big), s.FoodUrgent ? Error : Text);
			var pf = F(15, 600);
			string pct = $"%{Math.Round(s.FoodFraction * 100)}";
			float pw = W(pct, pf), barY = ty + 32 + big + 26;
			Bar(bx, barY, br - bx - pw - 18, 10, s.FoodFraction, s.FoodUrgent ? Error : Lavender);
			S.Text(pct, br - pw, barY - 7, pf, Text);
			var df = F(13.5f);
			S.Text("kalan yem", bx, barY + 22, df, Muted);
			var end = Fmt.DateLong(p.FoodExpiresAt!.Value);
			S.Text(end, br - W(end, df), barY + 22, df, Muted);
		}

		// iki metrik kartı
		string stageLabel, stageValue;
		if (s.NextStage == null) { stageLabel = "Gelişim"; stageValue = "Tamamlandı"; }
		else
		{
			stageLabel = $"{s.NextStage} olmasına";
			stageValue = s.NextStageLeft is { } n ? (n > TimeSpan.Zero ? Fmt.DurationLong(n) : "Bugün") : "Bilinmiyor";
		}
		float mw = (cr - cx - gap) / 2;
		Metric(cx, metricY, mw, metricH, GChicken, stageLabel, stageValue);
		Metric(cx + mw + gap, metricY, mw, metricH, GCalendar, "Mevcut yaşı", s.AgeDays is int d ? $"{d} gün" : "Bilinmiyor");

		// güncelleme durumu + Güncelle
		var (text, glyph, gc) = SyncText(a);
		float sy = syncY + syncH / 2;
		Glyph(glyph, cx + 12, sy, 18, gc);
		S.Text(text, cx + 34, sy - 10, F(14), a.State is "error" or "need-login" ? Error : Muted);
		if (a.State == "need-login") Button(cr - 150, sy - 21, 150, 42, GSignOut, "Giriş yap", "login");
		else Button(cr - 140, sy - 21, 140, 42, GRefresh, "Güncelle", "refresh:" + a.Id, enabled: !model.Fetching);
		return h;
	}

	void Metric(float x, float y, float w, float h, string icon, string label, string value)
	{
		S.FillRoundRect(x, y, w, h, 16, Raised);
		Glyph(icon, x + 38, y + h / 2, 24, Muted);
		S.Text(label, x + 70, y + h / 2 - 26, F(14), Muted);
		S.Text(value, x + 70, y + h / 2 - 2, F(21, 600), Text);
	}

	// Geçmiş: sadece Kümes'in gördüğü gerçek kayıtlar (yem yenilemeleri, evre değişimleri, yumurtadan çıkış)
	float History(AccountModel a, float y)
	{
		var p = a.Pet!;
		var events = new List<(DateTimeOffset At, string Text, string Icon)>();
		var hist = a.History.ToList();
		foreach (var f in PetStats.Feedings(hist)) events.Add((f.SeenAt, $"Yem yenilendi (+{Math.Round(f.Added.TotalDays)} gün)", SvgIcons.Bowl));
		for (int i = 1; i < hist.Count; i++)
			if (hist[i].Stage != null && hist[i - 1].Stage != null && hist[i].Stage != hist[i - 1].Stage)
				events.Add((hist[i].At, $"{hist[i].Stage} oldu", GChicken));
		var rows = events.OrderByDescending(e => e.At).Take(12).Select(e => (e.Text, When: Fmt.When(e.At), e.Icon)).ToList();
		if (p.HatchedAt is { } hatched) rows.Add(("Yumurtadan çıktı", Fmt.Day(hatched), SvgIcons.Egg));
		rows.Insert(0, ("Bilgiler güncellendi", Fmt.When(p.FetchedAt), GRefresh));
		if (rows.Count == 0) return y;

		S.Text("Geçmiş", X0 + 4, y, F(15, 600), Text);
		y += 34;
		const float rh = 58;
		float gh = rows.Count * rh;
		S.FillRoundRect(X0, y, CW, gh, 18, Surface);
		for (int i = 0; i < rows.Count; i++)
		{
			float ry = y + i * rh;
			if (i > 0) S.FillRect(X0 + 64, ry, CW - 86, 1, Line);
			S.FillRoundRect(X0 + 16, ry + 11, 36, 36, 10, Raised);
			Glyph(rows[i].Icon, X0 + 34, ry + 29, 16, Muted);
			S.Text(rows[i].Text, X0 + 66, ry + 18, F(14.5f), Text);
			var wf = F(13);
			S.Text(rows[i].When, X0 + CW - 22 - W(rows[i].When, wf), ry + 20, wf, Muted);
		}
		return y + gh + 16;
	}

	// Mesaj kartı (boş/hata durumları)
	float Message(float y, string title, string text, string? button = null, string? id = null)
	{
		var lines = Wrap(text, F(14), CW - 48);
		float h = 74 + lines.Count * 21 + (button != null ? 56 : 0);
		S.FillRoundRect(X0, y, CW, h, 18, Surface);
		S.Text(title, X0 + 24, y + 22, F(18, 600), Text);
		float ly = y + 54;
		foreach (var l in lines) { S.Text(l, X0 + 24, ly, F(14), Muted); ly += 21; }
		if (button != null) Button(X0 + 24, ly + 12, 200, 38, "", button, id!, primary: true);
		return h;
	}

	// ---- Ana Sayfa ----
	float PaintHome(float y)
	{
		y = CardTop - scroll;
		var a = Selected;
		if (a == null)
			return y + Message(y, "Steam hesabını bağla", "Kümes, CS2'deki tavuğunun yem süresini ve gelişimini Steam hesabınla CS2'nin sunucusundan okur. Telefonundaki Steam uygulamasıyla QR kodu okutman yeter.", "Steam ile giriş yap", "login");
		if (a.Pet == null)
		{
			if (a.State == "need-login") return y + Message(y, "Tekrar giriş gerekli", $"{a.Name} hesabının Steam oturumu sona erdi.", "Giriş yap", "login");
			if (a.State == "fetching" || a.State == "idle") return y + Message(y, "Tavuğun aranıyor…", "CS2'nin sunucusundan tavuk bilgisi alınıyor.");
			if (a.State is "cs2" or "waiting") return y + Message(y, "Birazdan güncellenecek", a.Message.Length > 0 ? a.Message : "CS2 açık; kapanınca tavuk bilgisi alınacak.", "Tekrar dene", "refresh:" + a.Id);
			return y + Message(y, "Tavuk bilgisi alınamadı", a.Message.Length > 0 ? a.Message : "Bağlantını kontrol edip yeniden dene.", "Tekrar dene", "refresh:" + a.Id);
		}
		if (!a.Pet.Found)
			return y + Message(y, "Tavuk bulunamadı", $"{a.Name} hesabının CS2 envanterinde tavuk ya da yumurta yok. Yeni bir tavuk alınca burada görünür.", "Tekrar kontrol et", "refresh:" + a.Id);
		y += PetCard(a, X0, y, CW, CardH) + 28;
		return History(a, y);
	}

	// ---- Koleksiyon ----
	float PaintCollection(float y)
	{
		y = PageTitle(y, "Koleksiyon", "Takip ettiğin tavuklar");
		// + : Steam hesabı ekle
		float bx = X0 + CW - 40, by = y - 70;
		S.FillRoundRect(bx, by, 40, 40, 10, Hot("login") ? 0x33324AFFu : Raised);
		Glyph(GAdd, bx + 20, by + 20, 15, Text);
		Item(bx, by, 40, 40, "login");

		foreach (var a in model.Accounts)
		{
			string id = "nav:detail:" + a.Id;
			S.FillRoundRect(X0, y, CW, 84, 16, Hot(id) ? 0x23242FFFu : Surface);
			var p = a.Pet;
			if (p is { Found: true })
			{
				Portrait(a, X0 + 12, y + 12, 60, 12);
				S.Text(p.Name ?? "Tavuğum", X0 + 88, y + 16, F(16.5f, 600), Text);
				S.Text(a.State == "need-login" ? "Tekrar giriş gerekli" : Subtitle(a), X0 + 88, y + 40, F(13), a.State == "need-login" ? Error : Muted);
				var s = PetSummary.From(p, DateTimeOffset.Now);
				if (s.FoodLeft is { } l && l > TimeSpan.Zero) Bar(X0 + 88, y + 64, 170, 4, s.FoodFraction, s.FoodUrgent ? Error : Lavender);
				var sf = F(15, 600);
				var sl = ShortLeft(s.FoodLeft);
				S.Text(sl, X0 + CW - 44 - W(sl, sf), y + 31, sf, s.FoodUrgent ? Error : Text);
				if (a.Id == model.SelectedId && model.Accounts.Count > 1)
				{
					var tf = F(12, 500);
					S.Text("Ana Sayfada", X0 + CW - 44 - W("Ana Sayfada", tf), y + 54, tf, Lavender);
				}
			}
			else
			{
				Avatar(a, X0 + 12, y + 12, 60);
				S.Text(a.Name, X0 + 88, y + 20, F(16.5f, 600), Text);
				S.Text(a.State == "need-login" ? "Tekrar giriş gerekli" : p == null ? "Henüz bilgi yok" : "Envanterde tavuk yok", X0 + 88, y + 46, F(13), a.State == "need-login" ? Error : Muted);
			}
			Glyph(GChevron, X0 + CW - 22, y + 42, 12, Faint);
			Item(X0, y, CW, 84, id);
			y += 94;
		}
		if (model.Accounts.Count == 0)
			y += Message(y, "Henüz tavuk yok", "Steam hesabını bağlayınca tavuğun burada görünür.", "Steam ile giriş yap", "login") + 10;
		else if (model.Accounts.Count == 1)
		{
			S.Text("Başka bir Steam hesabındaki tavuğu da takip etmek için + ile hesap ekle.", X0 + 4, y + 4, F(13), Faint);
			y += 30;
		}
		return y;
	}

	// ---- Tavuk detayı (Koleksiyon'un içinde) ----
	float PaintDetail(float y, string id)
	{
		var a = Acc(id);
		bool hot = Hot("nav:collection");
		Glyph(GBack, X0 + 6, y + 10, 12, hot ? Text : Muted);
		S.Text("Koleksiyon", X0 + 22, y, F(14), hot ? Text : Muted);
		Item(X0 - 4, y - 6, 120, 32, "nav:collection");
		y += 36;
		if (a == null) return y;
		if (a.Pet is not { Found: true } p)
			return y + Message(y, a.Name, a.State == "need-login" ? "Bu hesabın Steam oturumu sona erdi." : a.Pet == null ? "Henüz bilgi alınmadı." : "Bu hesabın CS2 envanterinde tavuk ya da yumurta yok.",
				a.State == "need-login" ? "Giriş yap" : "Tekrar kontrol et", a.State == "need-login" ? "login" : "refresh:" + a.Id);

		y += PetCard(a, X0, y, CW, CardH) + 16;
		if (model.Accounts.Count > 1)
		{
			if (a.Id == model.SelectedId) { S.Text("Ana Sayfada ve masaüstünde bu tavuk gösteriliyor.", X0 + 4, y + 2, F(13), Muted); y += 32; }
			else { Button(X0, y, 240, 40, "", "Ana Sayfada göster", "select:" + a.Id, primary: true); y += 56; }
		}

		y = History(a, y);
		S.Text($"Steam hesabı: {a.Name}", X0 + 4, y, F(13), Faint);
		return y + 30;
	}

	// ---- Ayarlar ----
	float PaintSettings(float y)
	{
		y = PageTitle(y, "Ayarlar", null);

		// masaüstü tavuğu
		y = Section(y, "Masaüstü tavuğu");
		(string L, string Dsc, bool On, string Id)[] toggles =
		[
			("Tavuğu göster", "Görev çubuğunun ve pencerelerin üstündeki tavuk", model.Settings.Visible, "visible"),
			("Her zaman üstte", "Tavuk diğer pencerelerin önünde kalır", model.Settings.AlwaysOnTop, "topmost"),
			("CS2 açıkken gizle", "Oyun açıkken tavuk görünmez", model.Settings.HideWhileCs2, "hidecs2"),
			("Windows açılınca başlat", "Bilgisayar açılınca Kümes sessizce başlar (pencere açılmaz)", model.Autostart, "autostart"),
		];
		y = Group(y, toggles.Length, 60, (i, ry) =>
		{
			var t = toggles[i];
			S.Text(t.L, X0 + 20, ry + 11, F(14.5f, 600), Text);
			S.Text(t.Dsc, X0 + 20, ry + 32, F(12.5f), Muted);
			Toggle(X0 + CW - 64, ry + 19, t.On);
			Item(X0, ry, CW, 60, t.Id);
		});

		// Steam hesapları
		y = Section(y, "Steam hesapları");
		var accs = model.Accounts;
		y = Group(y, accs.Count + 1, 64, (i, ry) =>
		{
			if (i == accs.Count)
			{
				bool hot = Hot("login");
				S.FillRoundRect(X0 + 16, ry + 13, 38, 38, 10, Raised);
				Glyph(GAdd, X0 + 35, ry + 32, 13, Text);
				S.Text("Hesap ekle", X0 + 68, ry + 13, F(14.5f, 600), hot ? Lavender : Text);
				S.Text("Başka bir hesaptaki tavuğu da takip et", X0 + 68, ry + 34, F(12.5f), Muted);
				Item(X0, ry, CW, 64, "login");
				return;
			}
			var a = accs[i];
			Avatar(a, X0 + 16, ry + 13, 38);
			S.Text(a.Name, X0 + 68, ry + 13, F(14.5f, 600), Text);
			string st = a.State == "need-login" ? "Tekrar giriş gerekli" : a.Pet is { Found: true } p ? $"Bağlı · {p.Name}" : "Bağlı";
			S.Text(st, X0 + 68, ry + 34, F(12.5f), a.State == "need-login" ? Error : Muted);
			if (a.State == "need-login") Button(X0 + CW - 248, ry + 15, 110, 34, "", "Giriş yap", "login");
			else Button(X0 + CW - 248, ry + 15, 110, 34, GOpen, "Profil", "profile:" + a.Id);
			Button(X0 + CW - 128, ry + 15, 112, 34, GSignOut, "Çıkış yap", "logout:" + a.Id, danger: true);
		});

		// veri
		y = Section(y, "Veri");
		var last = model.Accounts.Select(a => a.Pet?.FetchedAt).Where(t => t != null).DefaultIfEmpty(null).Max();
		(string L, string Dsc, string G, string Id)[] data =
		[
			("Şimdi güncelle", model.Fetching ? "Güncelleniyor…" : last is { } t ? $"Son senkronizasyon: {Fmt.When(t)}" : "Henüz güncellenmedi", GRefresh, "refresh"),
			("Veri klasörünü aç", "%APPDATA%\\KumesCS", GFolder, "openfolder"),
			("Kayıt dosyasını aç", "kumes.log", GDoc, "openlog"),
			("CS2 modellerini yeniden çıkar", model.Extracting ?? "Modeller ve renkler senin CS2 kurulumundan çıkarılır", GBox, "reextract"),
		];
		y = Group(y, data.Length, 60, (i, ry) =>
		{
			var r = data[i];
			bool enabled = !(r.Id == "refresh" && model.Fetching) && !(r.Id == "reextract" && model.Extracting != null);
			Glyph(r.G, X0 + 34, ry + 30, 15, Muted);
			S.Text(r.L, X0 + 60, ry + 11, F(14.5f, 600), enabled && Hot(r.Id) ? Lavender : Text);
			S.Text(r.Dsc, X0 + 60, ry + 32, F(12.5f), Muted);
			Glyph(GChevron, X0 + CW - 24, ry + 30, 11, Faint);
			Item(X0, ry, CW, 60, r.Id, enabled);
		});

		// hakkında
		y = Section(y, "Hakkında");
		(string Head, string Body)[] blocks =
		[
			("Ne yapar", "CS2'deki evcil tavuğunu Windows'ta, oyundaki gerçek modeli ve animasyonlarıyla yaşatır; yemin ne zaman biteceğini takip eder ve bitmeden haber verir."),
			("Veri nereden geliyor", $"Kümes, Steam hesabınla ayrı bir cihaz olarak (\"{DesktopAccount.DeviceName}\") bağlanır, birkaç saniye \"CS2 oynuyor\" görünür ve CS2'nin oyun sunucusundan envanterindeki tavuğu okur. Takas, satın alma ya da mesaj gibi hiçbir şey yapmaz."),
			("Hesabın", "Şifren hiç saklanmaz. Oturum anahtarı sadece bu bilgisayarda, Windows'un kendi şifrelemesiyle durur. Tamamen kaldırmak için: Steam → Ayarlar → Güvenlik → Yetkili cihazlar."),
			("Bağımsız bir uygulama", "Tavuk modelleri ve renkleri senin CS2 kurulumundan çıkarılır; uygulamayla dağıtılmaz. Kümes resmi bir Valve ürünü değildir ve Valve ile bir bağlantısı yoktur."),
		];
		float by = y + 18;
		var bf = F(13.5f);
		var wrapped = blocks.Select(b => (b.Head, Lines: Wrap(b.Body, bf, CW - 40))).ToList();
		float bh = 18 + wrapped.Sum(b => 26 + b.Lines.Count * 20 + 14);
		S.FillRoundRect(X0, y, CW, bh, 16, Surface);
		foreach (var (head, lines) in wrapped)
		{
			S.Text(head, X0 + 20, by, F(14, 600), Text);
			by += 26;
			foreach (var l in lines) { S.Text(l, X0 + 20, by, bf, Muted); by += 20; }
			by += 14;
		}
		y += bh + 16;
		Button(X0, y, 230, 40, GPower, "Kümes'i tamamen kapat", "quit", danger: true);
		return y + 60;
	}

	float Section(float y, string title)
	{
		S.Text(title, X0 + 4, y + 8, F(13, 600), Muted);
		return y + 34;
	}

	// ince ayraçlı satır grubu
	float Group(float y, int count, float rowH, Action<int, float> row)
	{
		S.FillRoundRect(X0, y, CW, count * rowH, 16, Surface);
		for (int i = 0; i < count; i++)
		{
			float ry = y + i * rowH;
			if (i > 0) S.FillRect(X0 + 18, ry, CW - 36, 1, Line);
			row(i, ry);
		}
		return y + count * rowH + 10;
	}

	// leylak anahtar: açıkken dolu + topuz sağda
	void Toggle(float x, float y, bool on)
	{
		float w = 44, h = 24;
		S.FillRoundRect(x, y, w, h, h / 2, on ? Lavender : Track);
		float r = h / 2 - 3.5f;
		S.FillEllipse(on ? x + w - 3.5f - r : x + 3.5f + r, y + h / 2, r, r, on ? OnLavender : Muted);
	}

	List<string> Wrap(string text, Vortice.DirectWrite.IDWriteTextFormat f, float width)
	{
		var lines = new List<string>();
		var cur = "";
		foreach (var word in text.Split(' '))
		{
			var tryLine = cur.Length == 0 ? word : cur + " " + word;
			if (G.Measure(tryLine, f).X > width && cur.Length > 0) { lines.Add(cur); cur = word; }
			else cur = tryLine;
		}
		if (cur.Length > 0) lines.Add(cur);
		return lines;
	}

	// ---------- etkileşim ----------

	Vector2 ToDesign(int x, int y) => new((x - ox) / k, (y - oy) / k);

	(float X, float Y, float W, float H, string Id, bool Enabled)? HitTest(int cx, int cy)
	{
		var p = ToDesign(cx, cy);
		for (int i = items.Count - 1; i >= 0; i--) // üstte çizilen önce
		{
			var it = items[i];
			if (p.X >= it.X && p.X <= it.X + it.W && p.Y >= it.Y && p.Y <= it.Y + it.H) return it;
		}
		return null;
	}

	void Activate(string id)
	{
		switch (id)
		{
			case "min": ShowWindow(Hwnd, 6 /* SW_MINIMIZE */); return;
			case "max": ShowWindow(Hwnd, IsZoomed(Hwnd) ? SW_RESTORE : 3 /* SW_MAXIMIZE */); return;
			case "close": PostMessageW(Hwnd, WM_CLOSE, 0, 0); return;
		}
		if (id.StartsWith("nav:"))
		{
			Page = id[4..];
			scroll = 0;
			hover = null;
			Paint();
			return;
		}
		Clicked?.Invoke(id);
	}

	void MoveFocus(int step)
	{
		var enabled = items.Where(i => i.Enabled).Select(i => i.Id).ToList();
		if (enabled.Count == 0) return;
		int idx = focus != null ? enabled.IndexOf(focus) : -1;
		idx = idx < 0 ? (step > 0 ? 0 : enabled.Count - 1) : (idx + step + enabled.Count) % enabled.Count;
		focus = enabled[idx];
		focusVisible = true;
		Paint();
	}

	protected override nint? OnMessage(uint msg, nuint wParam, nint lParam)
	{
		switch (msg)
		{
			case WM_PAINT:
				PAINTSTRUCT ps;
				var dc = BeginPaint(Hwnd, &ps);
				if (memDc != 0) BitBlt(dc, 0, 0, cw, ch, memDc, 0, 0, SRCCOPY);
				EndPaint(Hwnd, &ps);
				return 0;
			case WM_ERASEBKGND:
				return 1;
			case 0x0005: // WM_SIZE
			{
				int w = LoWord(lParam) & 0xFFFF, h = HiWord(lParam) & 0xFFFF;
				if (wParam != 1 /* SIZE_MINIMIZED */ && (w != cw || h != ch)) Resize(w, h);
				return 0;
			}
			case 0x0024: // WM_GETMINMAXINFO: büyütünce görev çubuğunu kapatma; en küçük boyut
			{
				var wa = GetWorkArea();
				var mmi = (MINMAXINFO*)lParam;
				mmi->MaxPosition = new POINT { X = wa.Left, Y = wa.Top };
				mmi->MaxSize = new POINT { X = wa.Right - wa.Left, Y = wa.Bottom - wa.Top };
				mmi->MinTrackSize = new POINT { X = (int)(DW * 0.6f * dpi), Y = (int)(DH * 0.6f * dpi) };
				return 0;
			}
			case 0x0084: // WM_NCHITTEST: üst şerit sürükleme alanı (düğmeler hariç)
			{
				var r = DefWindowProcW(Hwnd, msg, wParam, lParam);
				if (r != 1 /* HTCLIENT */) return r;
				POINT pt = new() { X = LoWord(lParam), Y = HiWord(lParam) };
				ScreenToClient(Hwnd, ref pt);
				var d = ToDesign(pt.X, pt.Y);
				if (d.Y < Top && HitTest(pt.X, pt.Y) == null) return 2; // HTCAPTION
				return 1;
			}
			case WM_MOUSEMOVE:
			{
				if (!tracking)
				{
					var tme = new TRACKMOUSEEVENT { CbSize = (uint)sizeof(TRACKMOUSEEVENT), DwFlags = TME_LEAVE, HwndTrack = Hwnd };
					TrackMouseEvent(&tme);
					tracking = true;
				}
				var id = HitTest(LoWord(lParam), HiWord(lParam))?.Id;
				if (id != hover) { hover = id; Paint(); }
				return 0;
			}
			case WM_MOUSELEAVE:
				tracking = false;
				if (hover != null || pressed != null) { hover = null; pressed = null; Paint(); }
				return 0;
			case 0x020A: // WM_MOUSEWHEEL
			{
				short delta = (short)((long)wParam >> 16);
				var s = Math.Clamp(scroll - delta / 120f * 60, 0, maxScroll);
				if (s != scroll) { scroll = s; Paint(); }
				return 0;
			}
			case WM_SETCURSOR:
				if ((lParam & 0xFFFF) == 1 && items.FirstOrDefault(i => i.Id == hover) is { Id: not null } hv)
				{
					SetCursor(LoadCursorW(0, hv.Enabled ? IDC_HAND : IDC_NO));
					return 1;
				}
				return null;
			case WM_LBUTTONDOWN:
			{
				var it = HitTest(LoWord(lParam), HiWord(lParam));
				pressed = it is { Enabled: true } ? it.Value.Id : null;
				if (pressed != null) focus = pressed;
				focusVisible = false;
				Paint();
				return 0;
			}
			case WM_LBUTTONUP:
			{
				var it = HitTest(LoWord(lParam), HiWord(lParam));
				var was = pressed;
				pressed = null;
				Paint();
				if (it is { Enabled: true } && it.Value.Id == was) Activate(was!);
				return 0;
			}
			case WM_KEYDOWN:
				switch ((int)wParam)
				{
					case VK_TAB: MoveFocus(GetKeyState(VK_SHIFT) < 0 ? -1 : 1); return 0;
					case VK_DOWN: MoveFocus(1); return 0;
					case VK_UP: MoveFocus(-1); return 0;
					case VK_RETURN or VK_SPACE:
						if (focus != null && items.Any(i => i.Id == focus && i.Enabled)) { focusVisible = true; Activate(focus); }
						return 0;
					case VK_ESCAPE: PostMessageW(Hwnd, WM_CLOSE, 0, 0); return 0;
				}
				return null;
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
		FreeSurface();
		base.Dispose();
	}
}
