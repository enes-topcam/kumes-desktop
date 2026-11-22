// Görev çubuğundaki tavuk: pencere, 3D çizim, tepesindeki barlar/baloncuklar, fareyle etkileşim, bilgi balonu.
// Pencere tavuğu saran küçük bir kutu; tavuk yürüdükçe/kaldırıldıkça pencere taşınır (boyutu sabit).
using System.Numerics;
using Kumes.Native;
using Kumes.Render;
using Kumes.Steam;
using Kumes.Ui;
using static Kumes.Native.Win32;
using static Kumes.Lang;

namespace Kumes;

sealed unsafe class PetView : IDisposable
{
	public const float FullRationDays = 35; // bir torba yem ≈ 35 gün (ölçüm: 13 Kas -> 20 Ara 2026), bar ölçeği için
	static readonly Dictionary<string, string> StageNext = new() { ["Yumurta"] = "Civciv", ["Civciv"] = "Piliç", ["Piliç"] = "Tavuk" };

	readonly float s;
	readonly int W, H;
	readonly GlContext gl;
	PetRenderer renderer;
	PetAnimator animator;
	GlbModel model;
	CoatTextures coat;
	public readonly PetBehavior Behavior;
	readonly LayeredWindow win, shadowWin;
	readonly Surface2D overlay, shadowSurf;
	readonly TooltipWindow tip;
	readonly Func<PetInfo?> pet;
	readonly Func<string> statusText;
	public Action<int, int>? MenuRequested;
	public Action<bool>? HoldChanged;     // tutulurken diğer pencerelerin arkasında kalmasın
	public string ModelKey { get; private set; }
	public bool Visible { get; private set; }
	public bool Topmost { get; set; } = true;

	RECT wa;
	int shownLeft;
	int pendingLeft, pendingTop;
	bool hasPending;
	string overlayKey = "";
	string? tipTarget;
	int shadowKey = -1;
	double time;
	(int X, int Y)? candidate;
	bool dragging;
	float dragDx, dragDy;
	int lastCursorX;

	float Pad => PetRenderer.GroundPadCss * s;
	float FeetY => H - Pad;
	public float HeadH => 0.4f * PetRenderer.PxPerUnitCss * s; // ayaktan tepeliğe yaklaşık
	float Cx => W / 2f;
	float WorldW => wa.Right - wa.Left;
	bool BarsVisible => pet() is { Found: true };

	public PetView(GlContext gl, GlbModel model, CoatTextures coat, string modelKey, Func<PetInfo?> pet, Func<string> statusText)
	{
		this.gl = gl;
		this.pet = pet;
		this.statusText = statusText;
		ModelKey = modelKey;
		s = GetDpiForSystem() / 96f;
		int glSize = (int)MathF.Round(PetRenderer.CanvasCss * s);
		W = glSize;
		H = (int)MathF.Round(180 * s); // tavuğun üstünde barlar ve baloncuklar için yer

		renderer = new PetRenderer(gl.Gl, glSize, model, coat);
		this.model = model; // köşe verisi tutulur: aynı modelden Kümes penceresi için büyük resim de çizilir
		this.coat = coat;
		animator = new PetAnimator(model);
		wa = GetWorkArea();
		Behavior = new PetBehavior(animator, s, () => this.pet()?.FoodExpiresAt, WorldW * 0.75f);

		shadowWin = new LayeredWindow((int)MathF.Round(80 * s), (int)MathF.Round(16 * s), clickThrough: true);
		win = new LayeredWindow(W, H, clickThrough: false);
		overlay = new Surface2D(W, H);
		shadowSurf = new Surface2D(shadowWin.Width, shadowWin.Height);
		tip = new TooltipWindow(s);

		win.MouseMove = OnMouseMove;
		win.MouseLeave = OnMouseLeave;
		win.LeftDown = OnLeftDown;
		win.LeftUp = OnLeftUp;
		win.CaptureLost = OnCaptureLost;
		win.RightUp = (x, y) => MenuRequested?.Invoke(x, y);
	}

	// Irk/renk değişince (ya da yeni çıkarılan modeller gelince) modeli değiştir; tavuk yerinde kalır
	public void ReplaceModel(GlbModel model, CoatTextures coat, string modelKey)
	{
		renderer.Dispose();
		renderer = new PetRenderer(gl.Gl, W, model, coat);
		this.model = model;
		this.coat = coat;
		animator = new PetAnimator(model);
		Behavior.SetAnimator(animator);
		ModelKey = modelKey;
		hasPending = false;
	}

	// Kümes penceresi için büyük, sinematik ışıklı resim: px x px, düz alfa BGRA, üstten alta (kırpılmadan)
	public byte[] RenderHero(int px)
	{
		using var r = new PetRenderer(gl.Gl, px, model, coat);
		r.UseHeroLighting();
		var mixer = new AnimationMixer(model.Skeleton);
		if (model.Clips.TryGetValue("chick_idle01", out var clip))
		{
			var l = mixer.Play(clip, false);
			l.Weight = 1;
			l.Time = 0.5f;
		}
		mixer.Update(0, Matrix4x4.CreateRotationY(0.85f)); // 3/4 görünüm, sağa bakıyor
		var raw = new byte[px * px * 4];
		fixed (byte* p = raw) r.Render(mixer.JointMatrices, 0, px / PetRenderer.CanvasCss, p);
		return IconMaker.Unpremultiply(raw, px, flip: true);
	}

	// Tray simgesi için tavuğun küçük resmi (32x32, düz alfa BGRA)
	public byte[] RenderIcon(int iconSize)
	{
		var mixer = new AnimationMixer(model.Skeleton);
		if (model.Clips.TryGetValue("chick_idle01", out var clip))
		{
			var l = mixer.Play(clip, false);
			l.Weight = 1;
			l.Time = 0.5f;
		}
		mixer.Update(0, Matrix4x4.CreateRotationY(0.7f)); // kameraya biraz daha dönük
		var full = new byte[renderer.Size * renderer.Size * 4];
		fixed (byte* p = full) renderer.Render(mixer.JointMatrices, 0, s, p);
		return IconMaker.FromRender(full, renderer.Size, iconSize);
	}

	public void Show()
	{
		Visible = true;
		hasPending = false;
	}

	public void Hide()
	{
		Visible = false;
		if (dragging || candidate != null) { candidate = null; dragging = false; ReleaseCapture(); Behavior.EndHold(); }
		win.Hide();
		shadowWin.Hide();
		tip.Hide();
		tipTarget = null;
		Behavior.Hovering = false;
	}

	public void RefreshWorkArea() => wa = GetWorkArea();

	bool wasTopmost = true;

	// "Her zaman üstte" açıkken düzenli olarak en üste al (görev çubuğuna tıklanınca altta kalmasın).
	// Kapalıysa sadece geçişte bir kere normal pencereye çevir; her saniye öne fırlamasın.
	public void KeepOnTop()
	{
		if (!Visible) return;
		bool on = Topmost || dragging || Behavior.Falling; // sürüklerken diğer pencerelerin arkasında kalmasın
		if (on || wasTopmost)
		{
			win.SetTopmost(on);
			shadowWin.SetTopmost(on);
			shadowWin.PlaceBelow(win.Hwnd);
		}
		wasTopmost = on;
	}

	// ---------- kare ----------

	// ---------- pencere kenarları ----------

	List<Perch> ledges = new();
	double nextScan;
	bool wasFalling;

	// Tavuğun KENDİ seçtiği yerler (konma/atlama/tırmanma) bundan yüksek olmaz: tavuk ekranın üstünden taşmasın.
	// Ama konduğu pencereyi sen yukarı sürüklersen tavuk üstünde kalır (tepeye yapışma/tam ekran hariç).
	int MinLedgeTop => wa.Top + (int)(H - Pad);
	float MaxAutoAlt => wa.Bottom - MinLedgeTop;
	bool Reachable(Perch l) => l.Alt <= MaxAutoAlt;

	void ScanLedges()
	{
		ledges = Ledges.Find(wa, wa.Top + 3, (int)(60 * s));
		nextScan = time + 0.2;
		if (Behavior.OnLedge is not { } p) return;
		// konduğu kenar hâlâ görünüyor mu (önüne pencere gelmiş / pencere daralmış olabilir)?
		var same = ledges.FirstOrDefault(l => l.Hwnd == p.Hwnd && Behavior.X >= l.Left - 30 * s && Behavior.X <= l.Right + 30 * s);
		if (same == null)
		{
			// Pencere taşınıyorsa (ya da az önce taşındıysa) önüne pencere geldi sayma: sürüklerken düşmesin.
			// Değilse üst üste iki taramada (~0,4 sn) kapalı kalırsa düş; anlık geçişlerde düşmesin.
			if (time - perchMovedAt < 0.6) { lostScans = 0; return; }
			if (++lostScans >= 2) { lostScans = 0; Behavior.LoseLedge("önüne pencere geldi ya da kenar daraldı"); }
			return;
		}
		lostScans = 0;
		p.Left = same.Left;
		p.Right = same.Right;
	}

	int lostScans;

	// düşerken bu karede geçilen en yüksek kenar
	Perch? LandingBetween(float x, float fromAlt, float toAlt) =>
		ledges.Where(l => Reachable(l) && l.Hwnd != noLandHwnd && x >= l.Left && x <= l.Right && l.Alt <= fromAlt + 0.5f && l.Alt >= toAlt)
			.OrderByDescending(l => l.Alt).FirstOrDefault();

	readonly Random rng = new();

	// Tırmanmayı deneyeceği yer: yakında (~200 px içinde), 40-250 px daha yüksekte bir kenar; birden fazlaysa
	// rastgele. Zıplama sabit güçte (~130 px), daha yüksekteyse dener ama yetişemez.
	Perch? ClimbTarget()
	{
		ScanLedges(); // görev çubuğundayken liste eski olabilir
		float support = Behavior.SupportAlt, x = Behavior.X;
		var options = ledges.Where(l =>
			Reachable(l) && l.Alt > support + 40 * s && l.Alt <= support + 250 * s && l.Right - l.Left >= 80 * s
			&& (x < l.Left ? l.Left - x : x > l.Right ? x - l.Right : 0) <= 200 * s
			&& Behavior.OnLedge?.Hwnd != l.Hwnd).ToList();
		return options.Count > 0 ? options[rng.Next(options.Count)] : null;
	}

	// havadaki tavuğun altındaki ilk kenar (gölge oraya düşer)
	float GroundBelow(float x, float alt) =>
		ledges.Where(l => x >= l.Left && x <= l.Right && l.Alt <= alt + 0.5f).Select(l => l.Alt).DefaultIfEmpty(0).Max();

	// Konduğu pencereyi izle: taşınınca tavuk da taşınır; kapanır/küçülür/ekranın üstüne çıkarsa düşer
	void TrackPerch()
	{
		if (Behavior.OnLedge is not { } p) { badFrames = 0; return; }
		// kapandı / küçültüldü: hemen düş
		if (!IsWindow(p.Hwnd)) { badFrames = 0; Behavior.LoseLedge("pencere kapandı"); return; }
		if (IsIconic(p.Hwnd)) { badFrames = 0; Behavior.LoseLedge("pencere küçültüldü"); return; }
		// konum bir an okunamadı, ya da pencere ekranın en tepesine yapıştı (tam ekran / üste yaslama) veya
		// görev çubuğunun dibine indi: ~0,25 sn sürerse düş (sürüklerken anlık değmelerde düşmesin)
		bool ok = Ledges.TryGetBounds(p.Hwnd, out var r);
		if (!ok || r.Top <= wa.Top + 2 || r.Top > wa.Bottom - 30)
		{
			if (badFrames++ == 0) badSince = time;
			if (time - badSince >= 0.25)
			{
				badFrames = 0;
				Behavior.LoseLedge(!ok ? "pencerenin konumu okunamadı" : r.Top <= wa.Top + 2 ? "pencere ekranın tepesine yapıştı" : "pencere görev çubuğuna indi");
			}
			return;
		}
		badFrames = 0;
		int dx = r.Left - p.WinLeft;
		float alt = wa.Bottom - r.Top;
		// Aşağı çekme eşiği: pencerenin bu tutunmadaki en yüksek noktasından ~30 px'ten fazla aşağı inerse bırak.
		// Daha az kaymalarda (sağa sola sürüklerken el kayması) tutunmaya devam eder. Pencere yarım saniye
		// durunca eşik o konumdan yeniden başlar (küçük kaymalar birikip düşürmesin).
		if (p != trackedPerch) { trackedPerch = p; perchHighAlt = p.Alt; }
		if (time - perchMovedAt > 0.5 || alt > perchHighAlt) perchHighAlt = alt;
		if (alt < perchHighAlt - 30 * s)
		{
			// Pencere aşağı çekildi: tavuk tutunmaz, olduğu yerden süzülür. Pencere aşağı hareket ettiği sürece
			// (ve durduktan sonra kısa bir süre) ona konmaz; yoksa yavaş çekerken sürekli kon-kalk yapardı.
			noLandHwnd = p.Hwnd;
			noLandTop = r.Top;
			noLandUntil = time + 0.35;
			Behavior.DropFromLedge();
			return;
		}
		if (dx != 0 || alt != p.Alt) perchMovedAt = time; // pencere taşınıyor
		if (dx != 0)
		{
			p.Left += dx;
			p.Right += dx;
			p.WinLeft = r.Left;
			Behavior.X += dx;
		}
		p.Alt = alt;
		Behavior.Alt = alt;
	}

	int badFrames;
	double badSince;
	double perchMovedAt = -10;
	Perch? trackedPerch;
	float perchHighAlt;

	// aşağı çekilen pencere: hareketi durana kadar ona konma
	nint noLandHwnd;
	int noLandTop;
	double noLandUntil;

	void UpdateNoLand()
	{
		if (noLandHwnd == 0) return;
		if (Ledges.TryGetBounds(noLandHwnd, out var r) && r.Top != noLandTop)
		{
			noLandTop = r.Top;
			noLandUntil = time + 0.35; // hâlâ hareket ediyor
		}
		if (time > noLandUntil) noLandHwnd = 0;
	}

	public void DemoHop() => Behavior.Hop(HeadH); // tanıtım çekimi: tıklanmış gibi numara + kalp

	public void Frame(float dt)
	{
		if (!Visible) return;
		time += dt;
		Behavior.FindLanding ??= LandingBetween;
		Behavior.FindClimbTarget ??= ClimbTarget;
		bool inAir = Behavior.InAir;
		UpdateNoLand();
		TrackPerch(); // önce pencereyi izle (tavuğu yeni yerine kaydır), sonra kenarları tara
		if ((Behavior.OnLedge != null || inAir) && (time >= nextScan || (inAir && !wasFalling))) ScanLedges();
		wasFalling = inAir;
		Behavior.Update(dt, WorldW);
		animator.Update(new AnimState(Behavior.DesiredAnim(), Behavior.Dir, Behavior.SpeedScale, dt));

		// Büyük zıplamalarda (trick02/05/07) pencere tavuğu takip eder: pencere zıplama kadar yukarı taşınır,
		// tavuk tuvalde o kadar aşağı çizilir. Ekranda tavuk gerçekten zıplar ama pencereden taşmaz.
		int jump = (int)MathF.Round(animator.JumpHeight() * PetRenderer.PxPerUnitCss * s);
		float ground = Behavior.OnLedge?.Alt ?? (inAir ? GroundBelow(Behavior.X, Behavior.Alt) : 0);
		float alt = Behavior.Alt + jump - ground; // gölgenin düştüğü yerden yükseklik

		byte* glDest = win.Bits + (H - renderer.Size) * win.Stride; // 3D tavuk pencerenin alt kısmında
		bool ready = renderer.RenderAsync(animator.Mixer.JointMatrices, alt <= 0.5f ? 0.25f : 0, s, glDest, win.Stride, jump);

		int left = wa.Left + (int)MathF.Round(Behavior.X - W / 2f);
		int feet = wa.Bottom - (int)MathF.Round(2 * s) - (int)MathF.Round(Behavior.Alt) - jump;
		int top = feet - (H - (int)MathF.Round(Pad));

		// Görüntü bir kare geriden geliyor; pencereyi de o karenin konumuna koy ki ikisi uyuşsun
		if (ready && hasPending)
		{
			new Span<byte>(win.Bits, (H - renderer.Size) * win.Stride).Clear();
			var key = OverlayKey();
			if (key != overlayKey)
			{
				PaintOverlay();
				overlayKey = key;
			}
			overlay.CompositeOver(win.Bits, win.Stride);
			win.Present(pendingLeft, pendingTop);
			shownLeft = pendingLeft;
			UpdateShadow(pendingLeft + W / 2, wa.Bottom - (int)MathF.Round(2 * s) - (int)MathF.Round(pendingGround), pendingAlt);
			if (tipTarget != null) ShowTip(pendingLeft, pendingTop);
		}
		pendingLeft = left;
		pendingTop = top;
		pendingAlt = alt;
		pendingGround = ground;
		hasPending = true;
	}

	float pendingAlt, pendingGround;

	// Havadayken yerdeki gölge ayrı küçük pencerede (yükseldikçe küçülür ve solar)
	void UpdateShadow(int centerX, int groundY, float alt)
	{
		if (alt <= 0.5f) { shadowWin.Hide(); return; }
		float k = Math.Max(0.25f, 1 - alt / (500 * s));
		int q = (int)(k * 50);
		if (q != shadowKey)
		{
			shadowSurf.Begin();
			shadowSurf.FillEllipse(shadowSurf.Width / 2f, shadowSurf.Height / 2f, 30 * s * k, 5 * s * k, Rgba(0, 0, 0, 0.25f * k));
			shadowSurf.End();
			shadowSurf.CopyTo(shadowWin.Bits, shadowWin.Stride);
			shadowKey = q;
		}
		bool wasHidden = !shadowWin.Visible;
		shadowWin.Present(centerX - shadowWin.Width / 2, groundY - shadowWin.Height / 2);
		if (wasHidden) shadowWin.PlaceBelow(win.Hwnd);
	}

	// ---------- tavuğun tepesi: barlar, baloncuklar, kalpler ----------

	static uint Rgba(int r, int g, int b, float a) => (uint)(r << 24 | g << 16 | b << 8 | (int)Math.Round(Math.Clamp(a, 0, 1) * 255));

	(float X, float Y, float W, float H) FoodBar()
	{
		float top = FeetY - HeadH - 10 * s - (5 * 2 + 3) * s;
		float x1 = MathF.Round(Cx - (46 + 11) / 2f * s + 11 * s);
		return (x1, MathF.Round(top), 46 * s, 5 * s);
	}

	(float X, float Y, float W, float H) GrowthBar()
	{
		var f = FoodBar();
		return (f.X, f.Y + f.H + 3 * s, f.W, f.H);
	}

	bool InChicken(float x, float y) => Math.Abs(x - Cx) <= 38 * s && y >= FeetY - HeadH && y <= FeetY;

	string? BarAt(float x, float y)
	{
		if (!BarsVisible) return null;
		bool Hit((float X, float Y, float W, float H) b) => x >= b.X - 13 * s && x <= b.X + b.W + 3 * s && y >= b.Y - 2 * s && y <= b.Y + b.H + 2 * s;
		if (Hit(FoodBar())) return "food";
		if (Hit(GrowthBar())) return "growth";
		return null;
	}

	TimeSpan? FoodRemaining() => pet() is { Found: true, FoodExpiresAt: DateTimeOffset f } ? f - DateTimeOffset.Now : null;

	float FoodFraction() => FoodRemaining() is TimeSpan r ? (float)Math.Clamp(r.TotalDays / FullRationDays, 0, 1) : 0;

	uint FoodColor() => FoodRemaining() switch
	{
		null => 0x777777FF,
		TimeSpan r when r <= TimeSpan.FromDays(1) => 0xE8594AFF,
		TimeSpan r when r <= TimeSpan.FromDays(2) => 0xE8B04AFF,
		_ => 0x7CC96FFF,
	};

	// Büyüme: yumurtadan çıkıştan sonraki büyümeye kadar geçen sürenin oranı
	float GrowthFraction()
	{
		if (pet() is not { Found: true } p) return 0;
		if (p.NextUpgradeAt is not DateTimeOffset next) return 1;
		var start = p.HatchedAt ?? next - TimeSpan.FromDays(30);
		var span = next - start;
		return span > TimeSpan.Zero ? (float)Math.Clamp((DateTimeOffset.Now - start) / span, 0, 1) : 0;
	}

	string? Bubble()
	{
		if (Behavior.Hovering || Behavior.Held || Behavior.Falling) return null;
		if (Behavior.Mode == "sleep") return "z" + new string('z', (int)(time * 1.5) % 3);
		var h = Behavior.Hunger();
		if (h == "starving") return "😢 🌾";
		if (h == "hungry" && (int)(time / 6) % 2 == 0) return "🌾 ?";
		return null;
	}

	string OverlayKey()
	{
		var bubble = Bubble();
		var bars = BarsVisible ? $"{FoodFraction():F3}{FoodColor()}{GrowthFraction():F3}" : "-";
		var parts = Behavior.Particles.Count > 0 ? time.ToString("F3") : "";
		return $"{bars}|{tipTarget}|{bubble}|{Behavior.Dir}|{parts}";
	}

	void PaintOverlay()
	{
		var o = overlay;
		var g = Gfx2D.Shared;
		o.Begin();
		float headTop = FeetY - HeadH - (BarsVisible ? (10 + 5 * 2 + 3 + 4) * s : 0);

		var bubble = Bubble();
		if (bubble != null && bubble.StartsWith('z'))
		{
			// yatan tavuğun hemen üstünde, kafasının olduğu tarafta (uyurken boyu ~45 px; barlar çok yukarıda kalıyor)
			var f = g.Font(14 * s, 700);
			var size = g.Measure(bubble, f);
			float x = Cx + 22 * s * Behavior.Dir - size.X / 2, y = FeetY - 52 * s - size.Y;
			for (int dx = -1; dx <= 1; dx++)
				for (int dy = -1; dy <= 1; dy++)
					if (dx != 0 || dy != 0) o.Text(bubble, x + dx * 1.2f * s, y + dy * 1.2f * s, f, Rgba(0, 0, 0, 0.5f));
			o.Text(bubble, x, y, f, Rgba(255, 255, 255, 0.9f));
		}
		else if (bubble != null)
		{
			var f = g.Font(16 * s, 400, "Segoe UI Emoji");
			var size = g.Measure(bubble, f);
			float w = Math.Max(30 * s, size.X + 16 * s), h = 26 * s;
			float bx = Math.Clamp(Cx - w / 2, 4 * s, W - w - 4 * s), by = headTop - h;
			uint bg = Rgba(250, 248, 243, 0.95f);
			o.FillRoundRect(bx, by, w, h, 10 * s, bg);
			o.StrokeRoundRect(bx, by, w, h, 10 * s, Rgba(0, 0, 0, 0.25f));
			o.FillTriangle(new Vector2(Cx - 4 * s, by + h - 0.5f), new Vector2(Cx + 2 * s, by + h + 6 * s), new Vector2(Cx + 5 * s, by + h - 0.5f), bg);
			o.TextCentered(bubble, bx + w / 2, by + h / 2, f, 0x222222FF);
		}

		if (BarsVisible)
		{
			DrawBar(FoodBar(), FoodFraction(), FoodColor(), tipTarget == "food");
			DrawBar(GrowthBar(), GrowthFraction(), 0x6FA8E8FF, tipTarget == "growth");
			var icon = g.Font(9 * s, 400, "Segoe UI Emoji");
			var fb = FoodBar();
			var gb = GrowthBar();
			o.TextCentered("🌾", fb.X - 11 * s / 2 - s, fb.Y + fb.H / 2 + 0.5f, icon, 0xFFFFFFFF);
			o.TextCentered("🌱", gb.X - 11 * s / 2 - s, gb.Y + gb.H / 2 + 0.5f, icon, 0xFFFFFFFF);
		}

		var heart = g.Font(16 * s, 400, "Segoe UI Emoji");
		foreach (var p in Behavior.Particles)
			o.TextCentered(p.Text, Cx, FeetY - p.Dy, heart, 0xFFFFFFFF, Math.Min(1, p.Life));
		o.End();
	}

	void DrawBar((float X, float Y, float W, float H) b, float frac, uint color, bool highlight)
	{
		var o = overlay;
		o.FillRoundRect(b.X - s, b.Y - s, b.W + 2 * s, b.H + 2 * s, 3 * s, Rgba(15, 16, 20, 0.72f));
		if (frac > 0)
		{
			o.FillRoundRect(b.X, b.Y, Math.Max(2 * s, b.W * frac), b.H, 2 * s, color);
			o.FillRect(b.X + s, b.Y, Math.Max(0, b.W * frac - 2 * s), s, Rgba(255, 255, 255, 0.25f)); // üstte ince parlaklık
		}
		if (highlight) o.StrokeRoundRect(b.X - 1.5f * s, b.Y - 1.5f * s, b.W + 3 * s, b.H + 3 * s, 3 * s, Rgba(255, 255, 255, 0.85f), s);
	}

	// ---------- bilgi balonu ----------

	void SetTip(string? target)
	{
		if (target == tipTarget) return;
		tipTarget = target;
		if (target == null) tip.Hide();
	}

	void ShowTip(int winLeft, int winTop)
	{
		if (pet() is not { Found: true } p) { tip.Hide(); return; }
		string title, main, sub;
		if (tipTarget == "food")
		{
			title = Tf("🌾 {0} · Yem", p.Name);
			if (p.FoodExpiresAt is not DateTimeOffset f) { main = T("bilinmiyor"); sub = ""; }
			else if (f <= DateTimeOffset.Now) { main = T("Bitti! 😢"); sub = Tf("{0} itibarıyla aç", Fmt.Date(f)); }
			else { main = Tf("{0} kaldı", Fmt.Duration(f - DateTimeOffset.Now)); sub = Tf("Bitiş: {0}", Fmt.Date(f)); }
		}
		else
		{
			title = Tf("🌱 {0} · Büyüme", p.Name);
			StageNext.TryGetValue(p.StageName ?? "", out var next);
			if (p.NextUpgradeAt is not DateTimeOffset n) { main = T("Tam büyüdü"); sub = p.StageName is { } st ? T(st) : ""; }
			else
			{
				var left = n - DateTimeOffset.Now;
				main = left > TimeSpan.Zero ? Tf("{0} sonra", Fmt.Duration(left)) : T("yakında");
				sub = Fmt.Date(n) + (next != null ? $" · {T(p.StageName!)} → {T(next)}" : "");
			}
		}
		var bar = FoodBar();
		tip.Show(title, main, sub, statusText(), winLeft + W / 2, winTop + (int)bar.Y, wa);
	}

	// ---------- fare ----------

	void OnMouseMove(int x, int y)
	{
		if (candidate != null || dragging) { HandleDrag(); return; }
		if (Behavior.Falling) return;
		Behavior.Hovering = InChicken(x, y);
		Behavior.MouseX = shownLeft - wa.Left + x;
		SetTip(BarAt(x, y));
	}

	void OnMouseLeave()
	{
		if (dragging || candidate != null) return;
		Behavior.Hovering = false;
		SetTip(null);
	}

	void OnLeftDown(int x, int y)
	{
		if (!InChicken(x, y)) return;
		GetCursorPos(out var p);
		candidate = (p.X, p.Y);
		dragDx = Behavior.X - (p.X - wa.Left);
		dragDy = (wa.Bottom - p.Y) - Behavior.Alt;
		lastCursorX = p.X;
		SetCapture(win.Hwnd);
	}

	void OnLeftUp(int x, int y)
	{
		bool wasCandidate = candidate != null;
		bool wasDragging = dragging;
		candidate = null;
		dragging = false;
		ReleaseCapture();
		if (wasDragging) EndDrag();
		else if (wasCandidate) Behavior.Hop(HeadH); // sürüklemeden bıraktıysa tıklama sayılır
	}

	void OnCaptureLost()
	{
		candidate = null;
		if (!dragging) return;
		dragging = false;
		EndDrag();
	}

	void HandleDrag()
	{
		GetCursorPos(out var p);
		if (candidate is (int cx, int cy) && !dragging)
		{
			if (Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)) <= 5) return;
			dragging = true;
			Behavior.StartHold();
			SetTip(null);
			HoldChanged?.Invoke(true);
		}
		if (!dragging) return;
		// yükseklik sınırı yok: imleç ekranın tepesine kadar gidebildiği için tavuk da gider (bırakınca süzülür)
		Behavior.MoveHold(p.X - wa.Left + dragDx, (wa.Bottom - p.Y) - dragDy, float.MaxValue, WorldW, p.X - lastCursorX);
		lastCursorX = p.X;
	}

	void EndDrag()
	{
		Behavior.EndHold();
		HoldChanged?.Invoke(false);
	}

	public void Dispose()
	{
		tip.Dispose();
		overlay.Dispose();
		shadowSurf.Dispose();
		win.Dispose();
		shadowWin.Dispose();
		renderer.Dispose();
	}
}

// Bar üstüne gelince çıkan küçük bilgi balonu (pet.css .tip ile aynı görünüm)
sealed unsafe class TooltipWindow : IDisposable
{
	readonly float s;
	readonly LayeredWindow win;
	readonly Surface2D surf;
	string key = "";
	int w, h;

	public TooltipWindow(float scale)
	{
		s = scale;
		win = new LayeredWindow((int)MathF.Round(300 * s), (int)MathF.Round(120 * s), clickThrough: true);
		surf = new Surface2D(win.Width, win.Height);
	}

	public void Show(string title, string main, string sub, string foot, int centerX, int barTopY, RECT wa)
	{
		var k = $"{title}|{main}|{sub}|{foot}";
		if (k != key)
		{
			Paint(title, main, sub, foot);
			key = k;
		}
		int left = Math.Clamp(centerX - w / 2, wa.Left + (int)(6 * s), wa.Right - w - (int)(6 * s));
		int top = Math.Max(wa.Top + (int)(4 * s), barTopY - (int)(8 * s) - h);
		win.Present(left, top, w, h);
	}

	void Paint(string title, string main, string sub, string foot)
	{
		var g = Gfx2D.Shared;
		var fTitle = g.Font(10.5f * s);
		var fMain = g.Font(14 * s, 700);
		var fSub = g.Font(10.5f * s);
		var fFoot = g.Font(9.5f * s);
		var lines = new List<(string Text, Vortice.DirectWrite.IDWriteTextFormat F, uint Color, float Size)>
		{
			(title, fTitle, 0x9A9890FF, 10.5f), (main, fMain, 0xECEAE6FF, 14),
		};
		if (sub.Length > 0) lines.Add((sub, fSub, 0x9A9890FF, 10.5f));
		float contentW = lines.Max(l => g.Measure(l.Text, l.F).X);
		bool hasFoot = foot.Length > 0;
		if (hasFoot) contentW = Math.Max(contentW, g.Measure(foot, fFoot).X);
		float padX = 10 * s, padTop = 7 * s, padBottom = 6 * s;
		float height = padTop + lines.Sum(l => l.Size * 1.3f * s) + (hasFoot ? (4 + 4 + 1 + 9.5f * 1.3f) * s : 0) + padBottom;
		w = Math.Min(win.Width, (int)MathF.Ceiling(contentW + padX * 2 + 2 * s));
		h = Math.Min(win.Height, (int)MathF.Ceiling(height));

		surf.Begin();
		surf.FillRoundRect(0.5f, 0.5f, w - 1, h - 1, 8 * s, 0x16181EF0);
		surf.StrokeRoundRect(0.5f, 0.5f, w - 1, h - 1, 8 * s, 0xFFFFFF14);
		float y = padTop;
		foreach (var l in lines)
		{
			surf.Text(l.Text, padX, y, l.F, l.Color);
			y += l.Size * 1.3f * s;
		}
		if (hasFoot)
		{
			y += 4 * s;
			surf.FillRect(padX, y, w - padX * 2, Math.Max(1, s), 0xFFFFFF14);
			y += 4 * s + 1;
			surf.Text(foot, padX, y, fFoot, 0x9A9890FF);
		}
		surf.End();
		new Span<byte>(win.Bits, win.Stride * win.Height).Clear();
		surf.CopyTo(win.Bits, win.Stride);
	}

	public void Hide()
	{
		win.Hide();
		key = "";
	}

	public void Dispose()
	{
		surf.Dispose();
		win.Dispose();
	}
}
