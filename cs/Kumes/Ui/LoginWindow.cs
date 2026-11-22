// Steam giriş penceresi, iki yol (sekme):
//  - QR kod: telefondaki Steam uygulamasıyla okutulur.
//  - Kullanıcı adı: kullanıcı adı + şifre, sonra Steam Guard (uygulamadan onay ya da e-posta/uygulama kodu).
//    Mobildeki girişle aynı akış (Kumes.Core PasswordLogin). Şifre doğrudan Steam'e gider, saklanmaz.
// Görünüm Direct2D ile bir bitmap'e çizilir, WM_PAINT'te pencereye kopyalanır. Yazı kutuları Windows'un kendi
// EDIT denetimleri (şifre gizleme, yapıştırma, Türkçe klavye kendiliğinden çalışsın); çizilen kutunun içine oturur.
using Kumes.Native;
using Kumes.Steam;
using QRCoder;
using static Kumes.Native.Win32;
using static Kumes.Lang;

namespace Kumes.Ui;

sealed unsafe class LoginWindow : WindowBase
{
	const uint Style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN;
	const uint FieldBg = 0x1B1C29FFu, TabBg = 0x181925FFu, TabOn = 0x2C2A3DFFu, StrawHot = 0xCDB9F4FFu, ButtonOff = 0x3A3850FFu;
	const string LockIcon = "M5 11h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z M7 11V7a5 5 0 0 1 10 0v4";
	const int IDC_IBEAM = 32513;

	readonly float s;
	readonly int cw, ch;
	readonly Surface2D surf;
	readonly nint memDc, dib, oldBmp;
	readonly byte* bits;
	bool[,]? qr;
	string message = T("QR kod hazırlanıyor…"); // QR sekmesinin alt yazısı
	bool messageBad, done;

	// kullanıcı adı sekmesi
	enum Tab { Qr, Password }
	enum Step { Form, Busy, GuardWait, GuardCode, CodeCheck }
	Tab tab = Tab.Qr;
	Step step = Step.Form;
	readonly nint userEdit, passEdit, codeEdit, font, codeFont, fieldBrush;
	string? pwError;
	string guardText = "";
	bool canUseCode;
	TaskCompletionSource<string?>? codeWait;
	TaskCompletionSource? codeChosen; // onay beklerken "Kod ile giriş yap"a basıldı

	// tıklanabilir yerler (her çizimde yeniden)
	readonly List<(string Id, float X, float Y, float W, float H, bool Enabled)> hits = new();
	string? hover;

	public Action? Closed;
	public Action<bool>? Moving; // pencere sürüklenirken true (Windows kendi döngüsünü çalıştırır)
	public Action<string, string>? PasswordLogin; // kullanıcı adı, şifre
	public Action? PasswordCancel;
	public Action? DemoRequested; // "Giriş yapmadan dene": örnek tavukla dene
	readonly bool canDemo;

	public LoginWindow(bool canDemo = false)
	{
		this.canDemo = canDemo;
		s = GetDpiForSystem() / 96f;
		cw = (int)(380 * s);
		ch = (int)((canDemo ? 584 : 540) * s);
		EnsureClass("KumesLogin");
		var r = new RECT { Right = cw, Bottom = ch };
		AdjustWindowRectEx(&r, Style, false, 0);
		var wa = GetWorkArea();
		int w = r.Right - r.Left, h = r.Bottom - r.Top;
		Create(0, "KumesLogin", T("Steam'e giriş"), Style,
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

		font = CreateFontW(-(int)MathF.Round(14 * s), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, Theme.Sans);
		codeFont = CreateFontW(-(int)MathF.Round(22 * s), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, Theme.Sans);
		fieldBrush = CreateSolidBrush(ColorRef(FieldBg));
		userEdit = MakeEdit(1, 0, font, 64);
		passEdit = MakeEdit(2, ES_PASSWORD, font, 128);
		SendMessageW(passEdit, EM_SETPASSWORDCHAR, 0x25CF, 0); // ● (eski EDIT varsayılanı *)
		codeEdit = MakeEdit(3, ES_CENTER | ES_UPPERCASE, codeFont, 8);
		Paint();
	}

	nint MakeEdit(int id, uint style, nint f, int limit)
	{
		var e = CreateWindowExW(0, "EDIT", "", WS_CHILD | ES_AUTOHSCROLL | style, 0, 0, 10, 10, Hwnd, id, GetModuleHandleW(null), 0);
		SendMessageW(e, WM_SETFONT, (nuint)f, 1);
		SendMessageW(e, EM_LIMITTEXT, (nuint)limit, 0);
		return e;
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

	// QR girişinin hata/durum yazısı
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
		pwError = null;
		Paint();
	}

	// ---------- kullanıcı adı + şifre ----------

	void SetTab(Tab t)
	{
		if (tab == t || step != Step.Form) return;
		tab = t;
		Paint();
		if (t == Tab.Password) SetFocus(GetText(userEdit).Length == 0 ? userEdit : passEdit);
		else SetFocus(Hwnd);
	}

	void Start()
	{
		if (step != Step.Form) return;
		var name = GetText(userEdit).Trim();
		var pw = GetText(passEdit);
		if (name.Length == 0) { pwError = T("Steam kullanıcı adını yaz."); Paint(); SetFocus(userEdit); return; }
		if (pw.Length == 0) { pwError = T("Şifreni yaz."); Paint(); SetFocus(passEdit); return; }
		pwError = null;
		step = Step.Busy;
		Paint();
		SetFocus(Hwnd);
		PasswordLogin?.Invoke(name, pw);
	}

	// Giriş bitmedi (hata ya da vazgeçildi): forma dön, şifre silinir
	public void PasswordFailed(string? error)
	{
		codeWait?.TrySetResult(null);
		step = Step.Form;
		pwError = error;
		SetWindowTextW(passEdit, "");
		Paint();
		SetFocus(GetText(userEdit).Length == 0 ? userEdit : passEdit);
	}

	void SendCode()
	{
		if (step != Step.GuardCode) return;
		var c = GetText(codeEdit).Trim();
		if (c.Length < 5) { guardText = T("Kod 5 karakter olmalı."); Paint(); return; }
		step = Step.CodeCheck;
		guardText = T("Kod kontrol ediliyor…");
		Paint();
		SetFocus(Hwnd);
		codeWait?.TrySetResult(c);
	}

	void CancelGuard()
	{
		codeWait?.TrySetResult(null);
		PasswordCancel?.Invoke();
	}

	void ShowGuard(string text, bool askCode, bool useCode)
	{
		if (Hwnd == 0) return;
		step = askCode ? Step.GuardCode : Step.GuardWait;
		guardText = text;
		canUseCode = useCode && !askCode;
		SetWindowTextW(codeEdit, "");
		Paint();
		if (askCode) SetFocus(codeEdit);
	}

	// Steam Guard adımları arka plan iş parçacığından gelir; post ile pencerenin iş parçacığına aktarılır
	public ISteamGuardPrompt CreateGuard(Action<Action> post) => new Guard(this, post);

	sealed class Guard(LoginWindow w, Action<Action> post) : ISteamGuardPrompt
	{
		public void WaitingForAppApproval(bool canUseCode, bool viaEmail)
		{
			w.codeChosen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			post(() => w.ShowGuard(viaEmail ? T("E-postana gelen bağlantıyla girişi onayla.") : T("Telefonundaki Steam uygulamasından girişi onayla."),
				false, canUseCode));
		}

		public Task WhenCodeChosen() => w.codeChosen!.Task;

		public Task<string?> AskCodeAsync(GuardCodeKind kind, string? email, bool previousWasWrong)
		{
			var text = previousWasWrong ? T("Kod yanlış, tekrar dene.")
				: kind == GuardCodeKind.Email ? T("E-postana gelen kodu yaz.") : T("Steam uygulamasındaki kodu yaz.");
			var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
			w.codeWait = tcs;
			post(() => { if (w.Hwnd == 0) tcs.TrySetResult(null); else w.ShowGuard(text, true, false); });
			return tcs.Task;
		}
	}

	// Uygulamanın mesaj döngüsü, yazı kutularına giden tuşları önce buraya sorar (Enter, Tab, Esc, Ctrl+A)
	public bool PreTranslate(in MSG m)
	{
		if (Hwnd == 0 || m.Message != WM_KEYDOWN) return false;
		if (m.Hwnd != userEdit && m.Hwnd != passEdit && m.Hwnd != codeEdit) return false;
		int key = (int)m.WParam;
		if (key == VK_RETURN)
		{
			if (m.Hwnd == userEdit) SetFocus(passEdit);
			else if (m.Hwnd == passEdit) Start();
			else SendCode();
			return true;
		}
		if (key == VK_TAB)
		{
			if (m.Hwnd != codeEdit) SetFocus(m.Hwnd == userEdit ? passEdit : userEdit);
			return true;
		}
		if (key == VK_ESCAPE) { PostMessageW(Hwnd, WM_CLOSE, 0, 0); return true; }
		if (key == 'A' && GetKeyState(VK_CONTROL) < 0) { SendMessageW(m.Hwnd, EM_SETSEL, 0, -1); return true; }
		return false;
	}

	// ---------- çizim ----------

	// Kutu yerleri (piksel): kullanıcı adı, şifre, kod
	(float X, float Y, float W, float H) UserField => (24 * s, 186 * s, cw - 48 * s, 42 * s);
	(float X, float Y, float W, float H) PassField => (24 * s, 262 * s, cw - 48 * s, 42 * s);
	(float X, float Y, float W, float H) CodeField => (24 * s, 236 * s, cw - 48 * s, 52 * s);

	void Place(nint edit, (float X, float Y, float W, float H) f, float textH, bool visible)
	{
		if (visible)
		{
			int eh = (int)MathF.Round(textH * s);
			MoveWindow(edit, (int)(f.X + 12 * s), (int)(f.Y + (f.H - eh) / 2), (int)(f.W - 24 * s), eh, false);
		}
		ShowWindow(edit, visible ? SW_SHOW : SW_HIDE);
	}

	// alttaki durum/hata yazısının dikey ortası (altında "Giriş yapmadan dene" varsa onun üstünde)
	float MsgY => ch - (canDemo ? 72 : 30) * s;

	// Dil değişti: başlık ve içerik yeniden
	public void Repaint()
	{
		if (Hwnd == 0) return;
		SetWindowTextW(Hwnd, T("Steam'e giriş"));
		Paint();
	}

	void Paint()
	{
		if (Hwnd == 0) return;
		var g = Gfx2D.Shared;
		hits.Clear();
		surf.Begin();
		surf.FillRect(0, 0, cw, ch, Theme.Bg);
		float pad = 24 * s;
		surf.Text(T("Steam'e bağlan"), pad, 20 * s, g.Font(24 * s, 700, Theme.Serif), Theme.Ink);
		surf.FillRect(pad + 1 * s, 56 * s, 28 * s, 3 * s, Theme.Straw); // Kümes penceresiyle aynı vurgu çizgisi
		var lead = g.Font(13 * s, 400, Theme.Sans);
		surf.Text(T("Tavuğunun yem ve büyüme bilgisi için"), pad, 66 * s, lead, Theme.Muted);
		surf.Text(T("Steam hesabınla giriş yap."), pad, 84 * s, lead, Theme.Muted);

		bool pwTab = tab == Tab.Password && !done;
		Place(userEdit, UserField, 19, pwTab && step is Step.Form or Step.Busy);
		Place(passEdit, PassField, 19, pwTab && step is Step.Form or Step.Busy);
		Place(codeEdit, CodeField, 30, pwTab && step == Step.GuardCode);

		if (done)
		{
			surf.TextCentered("✓", cw / 2f, 250 * s, g.Font(48 * s, 700, Theme.Sans), Theme.Leaf);
			surf.TextCentered(T("Giriş tamam"), cw / 2f, 298 * s, g.Font(15 * s, 700, Theme.Serif), Theme.Ink);
			if (message.Length > 0)
				surf.TextCentered(message, cw / 2f, MsgY, g.Font(12.5f * s, 400, Theme.Sans), Theme.Muted);
		}
		else
		{
			PaintTabs(pad);
			if (tab == Tab.Qr) PaintQr(pad);
			else PaintPassword(pad);
			if (canDemo && step == Step.Form) PaintDemoLink(pad);
		}
		surf.End();
		surf.CopyTo(bits, cw * 4);
		InvalidateRect(Hwnd, null, false);
	}

	// En altta: Steam'e bağlanmadan örnek tavukla dene
	void PaintDemoLink(float pad)
	{
		var g = Gfx2D.Shared;
		surf.FillRect(pad, ch - 50 * s, cw - 2 * pad, Math.Max(1, s), Theme.Outline);
		var f = g.Font(13.5f * s, 600, Theme.Sans);
		string text = T("Giriş yapmadan dene");
		var size = g.Measure(text, f);
		float x = (cw - size.X) / 2, y = ch - 25 * s - size.Y / 2;
		surf.Text(text, x, y, f, hover == "demo" ? StrawHot : Theme.Straw);
		hits.Add(("demo", x - 8 * s, y - 6 * s, size.X + 16 * s, size.Y + 12 * s, true));
	}

	void PaintTabs(float pad)
	{
		var g = Gfx2D.Shared;
		float y = 112 * s, h = 34 * s, w = cw - 2 * pad, half = w / 2;
		surf.FillRoundRect(pad, y, w, h, 9 * s, TabBg);
		var f = g.Font(13 * s, 600, Theme.Sans);
		bool locked = step != Step.Form; // giriş sürerken sekme değişmez
		(Tab T, string Id, string Label)[] tabs = [(Tab.Qr, "tab-qr", T("QR kod")), (Tab.Password, "tab-pw", T("Kullanıcı adı"))];
		for (int i = 0; i < 2; i++)
		{
			var (t, id, label) = tabs[i];
			float x = pad + i * half;
			bool on = tab == t;
			if (on) surf.FillRoundRect(x + 3 * s, y + 3 * s, half - 6 * s, h - 6 * s, 7 * s, TabOn);
			surf.TextCentered(label, x + half / 2, y + h / 2, f, on ? Theme.Ink : hover == id && !locked ? Theme.Ink : Theme.Muted);
			if (!on) hits.Add((id, x, y, half, h, !locked));
		}
	}

	void PaintQr(float pad)
	{
		var g = Gfx2D.Shared;
		// QR kutusu (QR beyaz zeminde olmalı ki telefon okusun)
		float box = 240 * s, bx = (cw - box) / 2, by = 162 * s;
		surf.FillRoundRect(bx, by, box, box, 6 * s, Theme.White);
		surf.StrokeRoundRect(bx + 0.5f, by + 0.5f, box - 1, box - 1, 6 * s, Theme.Outline, Math.Max(1, s));
		if (qr != null)
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
			surf.TextCentered(T("QR kod hazırlanıyor…"), cw / 2f, by + box / 2, g.Font(13 * s, 400, Theme.Sans), Theme.Muted);
		}

		// adımlar: Steam uygulamasındaki gerçek menü adlarıyla
		var stepFont = g.Font(13 * s, 400, Theme.Sans);
		var num = g.Font(13 * s, 700, Theme.Serif);
		float sy = by + box + 22 * s;
		string[] steps =
		[
			T("Telefonunda Steam uygulamasını aç."),
			T("Alttaki kalkan simgesine (Steam Guard) dokun."),
			T("\"QR kodu tara\"ya dokun, bu kodu okut, onayla."),
		];
		for (int i = 0; i < steps.Length; i++)
		{
			surf.Text($"{i + 1}", pad, sy, num, Theme.Straw);
			surf.Text(steps[i], pad + 20 * s, sy, stepFont, Theme.Ink);
			sy += 24 * s;
		}
		if (message.Length > 0)
			surf.TextCentered(message, cw / 2f, MsgY, g.Font(12.5f * s, 400, Theme.Sans), messageBad ? Theme.Danger : Theme.Muted);
	}

	void PaintPassword(float pad)
	{
		var g = Gfx2D.Shared;
		var label = g.Font(12.5f * s, 600, Theme.Sans);
		var small = g.Font(12.5f * s, 400, Theme.Sans);
		var focus = GetFocus();

		if (step is Step.Form or Step.Busy)
		{
			surf.Text(T("Steam kullanıcı adı"), pad + 2 * s, 164 * s, label, Theme.Muted);
			Field(UserField, focus == userEdit, "field-user");
			surf.Text(T("Şifre"), pad + 2 * s, 240 * s, label, Theme.Muted);
			Field(PassField, focus == passEdit, "field-pass");
			Button("login", step == Step.Busy ? T("Bağlanılıyor…") : T("Steam ile giriş yap"), pad, 322 * s, cw - 2 * pad, 44 * s, step == Step.Form, true);

			surf.Icon(LockIcon, pad + 2 * s, 384 * s, 14 * s, Theme.Muted, 1.75f);
			surf.Text(T("Şifren yalnızca Steam'e gider, Kümes kaydetmez."), pad + 22 * s, 382 * s, small, Theme.Muted);
			surf.Text(T("Steam Guard onayı ya da kodu istenebilir."), pad + 22 * s, 401 * s, small, Theme.Muted);
			if (pwError != null)
				surf.TextCentered(pwError, cw / 2f, MsgY, g.Font(12.5f * s, 400, Theme.Sans), Theme.Danger);
			return;
		}

		// Steam Guard
		surf.Text("Steam Guard", pad, 164 * s, g.Font(17 * s, 700, Theme.Serif), Theme.Ink);
		surf.Text(guardText, pad, 196 * s, g.Font(13.5f * s, 400, Theme.Sans), guardText == T("Kod yanlış, tekrar dene.") || guardText == T("Kod 5 karakter olmalı.") ? Theme.Danger : Theme.Ink);
		float y = 236 * s;
		if (step == Step.GuardWait)
		{
			surf.Text(T("Onaylayınca giriş kendiliğinden tamamlanır."), pad, 220 * s, small, Theme.Muted);
			if (canUseCode)
			{
				var link = g.Font(13.5f * s, 600, Theme.Sans);
				string text = T("Kod ile giriş yap");
				var size = g.Measure(text, link);
				surf.Text(text, pad, 252 * s, link, hover == "use-code" ? StrawHot : Theme.Straw);
				hits.Add(("use-code", pad - 4 * s, 248 * s, size.X + 8 * s, size.Y + 8 * s, true));
			}
			y = 296 * s;
		}
		else if (step == Step.GuardCode)
		{
			Field(CodeField, focus == codeEdit, "field-code");
			Button("send", T("Kodu gönder"), pad, 300 * s, cw - 2 * pad, 44 * s, true, true);
			y = 356 * s;
		}
		else y = 236 * s; // kod kontrol ediliyor
		Button("cancel", T("Vazgeç"), pad, y, cw - 2 * pad, 40 * s, true, false);
	}

	void Field((float X, float Y, float W, float H) f, bool focused, string id)
	{
		surf.FillRoundRect(f.X, f.Y, f.W, f.H, 8 * s, FieldBg);
		surf.StrokeRoundRect(f.X + 0.5f, f.Y + 0.5f, f.W - 1, f.H - 1, 8 * s, focused ? Theme.Straw : Theme.Outline, Math.Max(1, s));
		hits.Add((id, f.X, f.Y, f.W, f.H, true));
	}

	void Button(string id, string text, float x, float y, float w, float h, bool enabled, bool primary)
	{
		var f = Gfx2D.Shared.Font(14 * s, 600, Theme.Sans);
		bool hot = enabled && hover == id;
		if (primary)
		{
			surf.FillRoundRect(x, y, w, h, 9 * s, !enabled ? ButtonOff : hot ? StrawHot : Theme.Straw);
			surf.TextCentered(text, x + w / 2, y + h / 2, f, enabled ? Theme.Bg : Theme.Muted);
		}
		else
		{
			if (hot) surf.FillRoundRect(x, y, w, h, 9 * s, FieldBg);
			surf.StrokeRoundRect(x + 0.5f, y + 0.5f, w - 1, h - 1, 9 * s, Theme.Outline, Math.Max(1, s));
			surf.TextCentered(text, x + w / 2, y + h / 2, f, hot ? Theme.Ink : Theme.Muted);
		}
		hits.Add((id, x, y, w, h, enabled));
	}

	string? HitAt(nint lParam)
	{
		float x = LoWord(lParam), y = HiWord(lParam);
		foreach (var h in hits)
			if (x >= h.X && x < h.X + h.W && y >= h.Y && y < h.Y + h.H) return h.Id;
		return null;
	}

	void Click(string id)
	{
		var hit = hits.FirstOrDefault(h => h.Id == id);
		if (!hit.Enabled) return;
		switch (id)
		{
			case "tab-qr": SetTab(Tab.Qr); break;
			case "tab-pw": SetTab(Tab.Password); break;
			case "login": Start(); break;
			case "send": SendCode(); break;
			case "cancel": CancelGuard(); break;
			case "demo": DemoRequested?.Invoke(); break;
			case "use-code": canUseCode = false; Paint(); codeChosen?.TrySetResult(); break;
			case "field-user": SetFocus(userEdit); break;
			case "field-pass": SetFocus(passEdit); break;
			case "field-code": SetFocus(codeEdit); break;
		}
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
			case WM_CTLCOLOREDIT: // yazı kutuları koyu temada
				SetTextColor((nint)wParam, ColorRef(Theme.Ink));
				SetBkColor((nint)wParam, ColorRef(FieldBg));
				return fieldBrush;
			case WM_COMMAND when HiWord((nint)wParam) is EN_SETFOCUS or EN_KILLFOCUS: // odak çerçevesi
				Paint();
				return 0;
			case WM_SETFOCUS when tab == Tab.Password && !done: // pencereye dönünce imleç kutuya
				if (step is Step.Form) SetFocus(GetText(userEdit).Length == 0 ? userEdit : passEdit);
				else if (step == Step.GuardCode) SetFocus(codeEdit);
				return 0;
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
			{
				var h = hits.FirstOrDefault(x => x.Id == hover);
				if (h.Id == null) return null;
				SetCursor(LoadCursorW(0, h.Id.StartsWith("field-") ? IDC_IBEAM : h.Enabled ? IDC_HAND : IDC_ARROW));
				return 1;
			}
			case WM_LBUTTONDOWN:
				if (HitAt(lParam) is string hitId) Click(hitId);
				return 0;
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
		codeWait?.TrySetResult(null); // kod bekleyen giriş takılı kalmasın
		SetWindowTextW(passEdit, "");
		SelectObject(memDc, oldBmp);
		DeleteObject(dib);
		DeleteDC(memDc);
		surf.Dispose();
		base.Dispose(); // yazı kutuları pencereyle birlikte yok olur
		DeleteObject(font);
		DeleteObject(codeFont);
		DeleteObject(fieldBrush);
	}
}
