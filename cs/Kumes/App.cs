// Uygulamanın beyni (src/main.js'in C# karşılığı): ayarlar, bağlı Steam hesapları ve güncellemeleri, CS2 takibi, tray,
// bildirimler, giriş penceresi, CS2 modellerinin çıkarılması ve ana döngü. Her şey tek (ana) iş parçacığında çalışır;
// Steam ve çıkarma gibi uzun işler arka planda yapılıp sonucu msg.Post ile ana iş parçacığına döner.
using System.Diagnostics;
using Kumes.Native;
using Kumes.Render;
using Kumes.Steam;
using Kumes.Ui;
using Microsoft.Win32;
using static Kumes.Native.Win32;
using static Kumes.Lang;

namespace Kumes;

sealed unsafe class App
{
	static readonly TimeSpan PeriodicRefresh = TimeSpan.FromHours(2);
	static readonly TimeSpan AfterCs2Close = TimeSpan.FromSeconds(15); // Steam oturumu bıraksın diye
	const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	const string RunName = "Kumes";

	readonly MessageWindow msg = new();
	readonly TrayIcon tray;
	readonly GlContext gl = new();
	Settings settings = Store.Load<Settings>("settings.json");
	readonly List<DesktopAccount> accounts = new();
	static readonly PetCache NoCache = new();
	PetView? view;
	LoginWindow? login;
	CancellationTokenSource? loginCts, fetchCts;
	bool fetching, cs2Running, cs2Known, watchBusy, running = true;
	AssetSet? assets;
	string? extracting;
	bool extractStarted;
	bool noCs2 = NeedsDownload; // Ayarlar'daki satır "indir" der (açılışta bir kere bakılır)
	bool ownReady;  // tavuk modeli hazır (indirildi ya da çıkarıldı): Ayarlar'daki "indir" satırı kapanır
	bool downloadAsked; // CS2'siz indirme için bu açılışta izin soruldu ("Şimdi değil" denince tekrar sorulmaz, Ayarlar'dan başlatılır)
	DateTime nextWatch, nextHousekeeping, nextMinute, nextPeriodic;
	DateTime afterCloseAt = DateTime.MaxValue, closeLoginAt = DateTime.MaxValue, closeDownloadAt = DateTime.MaxValue, trimAt = DateTime.MaxValue;
	DownloadWindow? download; // CS2'siz indirme penceresi

	MainWindow? panel;
	byte[]? heroImage;    // seçili tavuğun portresi (Ana Sayfa), 3D modelden
	string? heroFor;      // heroImage hangi hesabın tavuğu (hesap değişip yeni model yüklenirken eski portre gösterilmesin)
	const int HeroSize = 320;
	Collection? collection; // diğer tavukların portreleri (Koleksiyon)
	static readonly string Version = typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}" : "1.0";
	readonly (nint Small, nint Big) windowIcon = Brand.WindowIcons(); // Kümes logosu: başlık/görev çubuğu simgesi

	// Ana Sayfa'da ve masaüstünde gösterilen tavuğun hesabı
	DesktopAccount? Sel => accounts.FirstOrDefault(a => a.Id == settings.Selected)
		?? accounts.FirstOrDefault(a => a.Cache.Pet is { Found: true }) ?? accounts.FirstOrDefault();
	PetCache cache => Sel?.Cache ?? NoCache;
	bool AnyLoggedIn => accounts.Any(a => a.LoggedIn && !a.IsDemo); // gerçek (Steam) hesap

	// Tanıtım videosu çekimi (--cekim): hesap adı ve profil fotoğrafı gösterilmez, tavuk uyumaz/oturmaz,
	// davranışlar WM_DEMO ile tetiklenir. Normal kullanımda kapalı. ("Giriş yapmadan dene" ayrı: demo hesabı)
	readonly bool filming;

	public App(bool startedAtLogin, bool filming = false)
	{
		this.filming = filming;
		Lang.Add(Strings.En);
		ApplyLanguage();
		DesktopAccount.MigrateLegacy(settings);
		foreach (var id in settings.Accounts.Where(DesktopAccount.ValidId))
		{
			var a = new DesktopAccount(id);
			if (!a.LoggedIn) a.State = "need-login";
			a.LoadAvatar();
			accounts.Add(a);
		}
		if (settings.Demo && accounts.Count == 0) accounts.Add(DesktopAccount.Demo()); // giriş yapmadan deniyordu
		else settings.Demo = false;
		if (Environment.GetEnvironmentVariable("KUMES_ORNEK") == "1") accounts.AddRange(DesktopAccount.Samples()); // geliştirme: her türden örnek
		tray = new TrayIcon(msg.Hwnd);
		tray.SetIcon(Brand.TrayMark());
		tray.BalloonIcon = Brand.BalloonIcon();

		msg.TrayEvent = OnTray;
		msg.TaskbarRecreated = () => { tray.Add(); UpdateTray(); };
		msg.ShowRequested = () =>
		{
			// Kümes zaten açıkken tekrar başlatıldı: pencereyi göster (giriş yoksa giriş penceresini de)
			OpenPanel();
			if (accounts.Count == 0) OpenLogin();
			else login?.ShowAndFocus();
		};
		if (filming) msg.Demo = DemoCommand;
		tray.Add();
		UpdateTray();
		if (Sel is { } sel) { Store.SeedHistory(sel.Cache); sel.Save(); }
		LoadAssets();
		// elle açılınca pencere de açılır; Windows açılışında sessizce (sadece tavuk + tray) başlar
		if (!startedAtLogin) OpenPanel();

		var now = DateTime.UtcNow;
		nextMinute = now.AddMinutes(1);
		nextPeriodic = now + PeriodicRefresh;
	}

	// ---------- ana döngü ----------

	public void Run()
	{
		nint timer = CreateWaitableTimerExW(0, null, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION, TIMER_ALL_ACCESS);
		if (timer == 0) timer = CreateWaitableTimerExW(0, null, 0, TIMER_ALL_ACCESS);
		long freq = Stopwatch.Frequency, frameTicks = freq / FrameRate();
		lastFrame = Stopwatch.GetTimestamp();
		long nextFrame = lastFrame;
		msg.Tick = () => { if (view?.Visible == true) DrawFrame(); };

		while (running)
		{
			bool animating = view?.Visible == true;
			long now = Stopwatch.GetTimestamp();
			long wait = animating ? nextFrame - now : freq / 4; // görünmüyorsa çeyrek saniyede bir uyan
			if (wait > 0)
			{
				long due = -Math.Max(1, wait * 10_000_000 / freq);
				SetWaitableTimer(timer, &due, 0, 0, 0, false);
				MsgWaitForMultipleObjectsEx(1, &timer, INFINITE, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
			}

			while (PeekMessageW(out var m, 0, 0, 0, PM_REMOVE))
			{
				if (m.Message == WM_QUIT) running = false;
				if (login != null && login.PreTranslate(m)) continue; // giriş penceresindeki yazı kutuları: Enter, Tab…
				TranslateMessage(m);
				DispatchMessageW(m);
			}
			msg.RunQueued();
			if (!running) break;
			Timers(DateTime.UtcNow);
			TickSwitch();

			// Koleksiyon sayfası açıksa tavuk portrelerini adım adım çiz (her döngüde bir adım, ilk seferde)
			if (panel?.WantsCollection == true && collection is { Complete: false })
			{
				collection.Step();
				panel.Update(PanelState());
				if (collection.Complete) ScheduleTrim();
			}

			now = Stopwatch.GetTimestamp();
			if (view?.Visible != true) { lastFrame = now; continue; }
			if (now < nextFrame) continue;
			nextFrame += frameTicks;
			if (nextFrame < now) nextFrame = now + frameTicks;
			DrawFrame();
		}

		CloseHandle(timer);
		loginCts?.Cancel();
		fetchCts?.Cancel();
		login?.Dispose();
		download?.Dispose();
		panel?.Dispose();
		tray.Dispose();
		view?.Dispose();
		gl.Dispose();
		Log.Write("kapatıldı");
	}

	long lastFrame;

	// Ana ekranın yenileme hızına tam bölünen, 100'ü geçmeyen kare hızı: 300 Hz -> 100, 165 Hz -> 82, 60 Hz -> 60 fps.
	// Sabit 40 fps hem azdı hem de 300 Hz'de kareler 7-8 yenilemede bir düzensiz düşüp takılıyormuş gibi görünüyordu.
	static int FrameRate()
	{
		nint dc = GetDC(0);
		int hz = dc != 0 ? GetDeviceCaps(dc, VREFRESH) : 0;
		if (dc != 0) ReleaseDC(0, dc);
		if (hz < 30 || hz > 500) hz = 60; // okunamadı (0/1 = varsayılan donanım)
		int fps = hz / ((hz + 99) / 100);
		Log.Write($"ekran {hz} Hz, tavuk {fps} fps");
		return fps;
	}

	void DrawFrame()
	{
		long now = Stopwatch.GetTimestamp();
		float dt = Math.Min((now - lastFrame) / (float)Stopwatch.Frequency, 0.1f);
		lastFrame = now;
		view?.Frame(dt);
	}

	void Timers(DateTime now)
	{
		if (now >= nextWatch && !watchBusy)
		{
			// cs2.exe çalışıyor mu? (4 sn'de bir, arka planda)
			watchBusy = true;
			nextWatch = now.AddSeconds(4);
			Task.Run(() => Process.GetProcessesByName("cs2").Length > 0)
				.ContinueWith(t => msg.Post(() => { watchBusy = false; if (t.IsCompletedSuccessfully) OnCs2(t.Result); }));
		}
		if (now >= nextHousekeeping)
		{
			view?.RefreshWorkArea(); // görev çubuğu taşınmış / boyu değişmiş olabilir
			view?.KeepOnTop();       // görev çubuğuna tıklanınca altta kalmasın
			nextHousekeeping = now.AddSeconds(1);
		}
		if (now >= nextMinute)
		{
			CheckNotifications();
			UpdateTray();
			nextMinute = now.AddMinutes(1);
		}
		if (now >= nextPeriodic)
		{
			Refresh("periodic");
			nextPeriodic = now + PeriodicRefresh;
		}
		if (now >= afterCloseAt)
		{
			afterCloseAt = DateTime.MaxValue;
			Refresh("cs2-closed");
		}
		if (now >= closeDownloadAt)
		{
			closeDownloadAt = DateTime.MaxValue;
			download?.Dispose();
			download = null;
		}
		if (now >= closeLoginAt)
		{
			closeLoginAt = DateTime.MaxValue;
			loginCts?.Cancel(); // açık kalan QR/şifre girişi de dursun
			login?.Dispose();
			login = null;
		}
		if (now >= trimAt)
		{
			// Yükleme/güncelleme sırasında kullanılıp bir daha dokunulmayan belleği Windows'a geri ver
			trimAt = DateTime.MaxValue;
			GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
			K32EmptyWorkingSet(GetCurrentProcess());
		}
	}

	void ScheduleTrim() => trimAt = DateTime.UtcNow.AddSeconds(3);

	// ---------- durum ----------

	static void SetState(DesktopAccount a, string state, string message = "")
	{
		a.State = state;
		a.Message = message;
	}

	void ChangeSetting(Action<Settings> change)
	{
		change(settings);
		Store.Save("settings.json", settings);
		ApplyVisibility();
		UpdateTray();
	}

	// Giriş yoksa tavuk görünmez (sadece QR penceresi); CS2 açıkken istenirse gizlenir
	void ApplyVisibility()
	{
		if (view == null) return;
		view.Topmost = settings.AlwaysOnTop;
		bool show = Sel is { LoggedIn: true } && settings.Visible && !(settings.HideWhileCs2 && cs2Running);
		if (show && !view.Visible) view.Show();
		else if (!show && view.Visible) view.Hide();
		view.KeepOnTop();
	}

	string StatusText()
	{
		var a = Sel;
		string text = a == null ? T("Steam girişi gerekiyor (sağ tık)") : cs2Running && a.State != "fetching" ? T("CS2 açık, kapatınca güncellenecek") : a.State switch
		{
			"fetching" => T("Güncelleniyor…"),
			"cs2" => a.Message.Length > 0 ? a.Message : T("CS2 açık, kapatınca güncellenecek"),
			"waiting" or "error" or "playing" => a.Message,
			"need-login" => T("Steam girişi gerekiyor (sağ tık)"),
			_ => "",
		};
		if (cache.Pet is PetInfo p)
			text = text.Length > 0 ? $"{text} · {Fmt.Ago(p.FetchedAt)}" : Tf("Güncellendi: {0}", Fmt.Ago(p.FetchedAt));
		if (extracting != null) text = extracting;
		return text;
	}

	// ---------- CS2 ----------

	void OnCs2(bool isRunning)
	{
		if (cs2Known && isRunning == cs2Running) return;
		bool first = !cs2Known;
		cs2Known = true;
		cs2Running = isRunning;
		afterCloseAt = DateTime.MaxValue;
		ApplyVisibility();
		if (isRunning)
		{
			fetchCts?.Cancel(); // Steam aynı anda iki oyun oturumuna izin vermiyor
			if (first)
			{
				if (AnyLoggedIn) Refresh("startup"); // CS2 açık: güncellenmez ama "CS2 açık" diye söylenir
				else if (accounts.Count == 0) OpenLogin(); // ilk açılış: CS2 açık olsa da giriş penceresi gelsin
			}
			UpdateTray();
		}
		else if (first)
		{
			if (AnyLoggedIn) Refresh("startup");
			else if (accounts.Count == 0) OpenLogin();
			UpdateTray();
		}
		else
		{
			foreach (var a in accounts.Where(a => a.LoggedIn && !a.IsDemo)) SetState(a, "waiting", T("CS2 kapandı, birazdan güncellenecek…"));
			afterCloseAt = DateTime.UtcNow + AfterCs2Close;
			UpdateTray();
		}
	}

	// ---------- Steam ----------

	// Bağlı hesapları sırayla güncelle (only: sadece o hesap)
	void Refresh(string reason, DesktopAccount? only = null)
	{
		if (fetching) return;
		var list = (only != null ? [only] : accounts).Where(a => a.LoggedIn && !a.IsDemo).ToList();
		if (cs2Running)
		{
			// Steam aynı hesapta iki oyun oturumuna izin vermiyor: CS2 kapanınca (OnCs2) kendiliğinden güncellenir
			Log.Write($"güncelleme ertelendi ({reason}): CS2 açık");
			foreach (var a in list) SetState(a, "cs2", T("CS2 açıkken tavuk bilgisi alınamıyor. CS2'yi kapatınca kendiliğinden güncellenir."));
			UpdateTray();
			return;
		}
		if (list.Count == 0) { UpdateTray(); return; }
		fetching = true;
		foreach (var a in list) SetState(a, "fetching", T("Güncelleniyor…"));
		UpdateTray();
		fetchCts = new CancellationTokenSource();
		var ct = fetchCts.Token;
		Log.Write($"güncelleme ({reason}, {list.Count} hesap)");
		Task.Run(() =>
		{
			foreach (var a in list)
			{
				if (ct.IsCancellationRequested) { msg.Post(() => SetState(a, "cs2", T("CS2 açık, kapatınca güncellenecek"))); continue; }
				try
				{
					var r = a.Steam.FetchPetAsync(ct).GetAwaiter().GetResult(); // sırayla, arka planda
					msg.Post(() =>
					{
						if (!accounts.Contains(a)) return;
						SetPet(a, r.Pet);
						SetProfile(a, r.Profile);
						SetState(a, "ok");
						UpdateTray();
					});
				}
				catch (Exception e)
				{
					msg.Post(() => HandleSteamError(a, e, reason));
				}
			}
		}).ContinueWith(_ => msg.Post(() =>
		{
			fetching = false;
			ApplyVisibility();
			UpdateTray();
			ScheduleTrim();
		}));
	}

	void HandleSteamError(DesktopAccount a, Exception? e, string reason)
	{
		switch (e)
		{
			case SteamException { Code: "NEED_LOGIN" } se:
				SetState(a, "need-login", se.Message);
				if (reason != "periodic" && !AnyLoggedIn) OpenLogin();
				break;
			case SteamException { Code: "PLAYING_ELSEWHERE" } se:
				// Steam hesabında (bu bilgisayarda ya da başka yerde) başka bir oyun açık: oyundan çıkınca elle güncellenir
				SetState(a, "playing", se.Message);
				break;
			case OperationCanceledException or TaskCanceledException:
				SetState(a, "cs2", T("CS2 açık, kapatınca güncellenecek")); // CS2 açıldığı için iptal edildi
				break;
			default:
				Log.Write($"güncelleme hatası ({a.Id}): {e}");
				SetState(a, "error", e?.Message ?? "Bilinmeyen hata");
				break;
		}
		UpdateTray();
	}

	void SetPet(DesktopAccount a, PetInfo pet)
	{
		// Yem süresi belirgin uzadıysa CS2'de beslenmiş demektir: tavuk yem yesin
		bool fed = a.Cache.Pet?.FoodExpiresAt is DateTimeOffset before && pet.FoodExpiresAt is DateTimeOffset after
			&& after - before > TimeSpan.FromHours(12);
		a.Cache.Pet = pet;
		PetStats.Add(a.Cache.History, pet); // tavuk detayındaki geçmiş için ölçüm
		a.Save();
		Log.Write(pet.Found ? $"{a.Id}: {pet.Name}, yem bitişi {pet.FoodExpiresAt?.ToLocalTime():yyyy-MM-dd HH:mm}" : $"{a.Id}: envanterde tavuk yok");
		if (a == Sel)
		{
			EnsureModel(); // ırk/renk değiştiyse
			if (fed)
			{
				Log.Write("beslenmiş: yem yeme animasyonu");
				view?.Behavior.Feed();
			}
		}
		collection?.Want([pet]);
		CheckNotifications();
		EnsureCoats(); // bu tavuğun rengi hazır değilse (yeni hesap, renk değişti) ekle
	}

	// ---------- Steam profili (görünen ad, profil fotoğrafı) ----------

	static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

	void SetProfile(DesktopAccount a, SteamProfile p)
	{
		bool newAvatar = p.AvatarHash != null && (p.AvatarHash != a.Cache.Profile?.AvatarHash || !File.Exists(a.AvatarPath));
		a.Cache.Profile = p with { PersonaName = p.PersonaName ?? a.Cache.Profile?.PersonaName, AvatarHash = p.AvatarHash ?? a.Cache.Profile?.AvatarHash };
		a.Save();
		if (!newAvatar) return;
		// Steam'in profil fotoğrafı sunucusundan (avatars.steamstatic.com) indir
		var url = $"https://avatars.steamstatic.com/{p.AvatarHash}_full.jpg";
		var path = a.AvatarPath;
		Task.Run(() =>
		{
			var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
			File.WriteAllBytes(path, bytes);
		}).ContinueWith(t => msg.Post(() =>
		{
			if (t.IsCompletedSuccessfully) { a.LoadAvatar(); UpdateTray(); }
			else Log.Write($"profil fotoğrafı indirilemedi: {t.Exception?.InnerException?.Message}");
		}));
	}

	static void OpenShell(string target)
	{
		try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
		catch (Exception e) { Log.Write($"açılamadı ({target}): {e.Message}"); }
	}

	// Hesabı kaldır: oturumu, önbelleği ve profil fotoğrafı silinir
	void Logout(DesktopAccount a)
	{
		if (a.IsDemo) { EndDemo(); return; }
		bool wasSelected = a == Sel;
		a.Store.Clear("kullanıcı çıkış yaptı");
		try { Directory.Delete(a.Dir, true); } catch (Exception e) { Log.Write($"hesap klasörü silinemedi: {e.Message}"); }
		accounts.Remove(a);
		settings.Accounts.Remove(a.Id);
		if (settings.Selected == a.Id) settings.Selected = null;
		Store.Save("settings.json", settings);
		if (wasSelected) EnsureModel();
		ApplyVisibility();
		UpdateTray();
	}

	// Giriş penceresindeki "Giriş yapmadan dene": örnek tavuk masaüstünde dolaşır, Kümes penceresi açılır
	void StartDemo()
	{
		if (accounts.Any(a => a.IsDemo)) return;
		Log.Write("giriş yapmadan deneniyor (örnek tavuk)");
		var d = DesktopAccount.Demo();
		accounts.Add(d);
		settings.Demo = true;
		if (login != null) { loginCts?.Cancel(); login.Dispose(); login = null; }
		Select(d);
		OpenPanel();
		if (AssetManager.LoadOwn() == null) StartExtract(); // CS2 yoksa ve önceki indirme olmadıysa yeniden dene
	}

	// Demo kapatıldı (Ayarlar) ya da gerçek hesapla giriş yapıldı
	void EndDemo(bool openLogin = true)
	{
		if (accounts.FirstOrDefault(a => a.IsDemo) is not { } d) return;
		accounts.Remove(d);
		settings.Demo = false;
		if (settings.Selected == d.Id) settings.Selected = null;
		Store.Save("settings.json", settings);
		EnsureModel();
		ApplyVisibility();
		UpdateTray();
		if (openLogin && accounts.Count == 0) OpenLogin();
	}

	void Select(DesktopAccount a)
	{
		settings.Selected = a.Id;
		Store.Save("settings.json", settings);
		EnsureModel();
		ApplyVisibility();
		UpdateTray();
	}

	// ---------- giriş penceresi ----------

	// QR ya da kullanıcı adı + şifre ile giriş: yeni hesap eklenir ya da süresi dolan hesabın oturumu yenilenir
	void OpenLogin()
	{
		if (login != null) { login.ShowAndFocus(); return; }
		var w = new LoginWindow(canDemo: accounts.Count == 0); // hiç hesap yoksa "Giriş yapmadan dene"
		w.SetIcon(windowIcon.Small, windowIcon.Big);
		login = w;
		bool moving = false;
		w.Moving = m =>
		{
			if (m == moving) return;
			moving = m;
			if (m) msg.BeginModal(); else msg.EndModal(); // pencere sürüklenirken tavuk donmasın
		};
		w.Closed = () => { loginCts?.Cancel(); if (login == w) login = null; };
		w.DemoRequested = () => msg.Post(StartDemo); // pencerenin kendi mesajı bitince kapansın
		w.ShowAndFocus();
		ArrangeWindows();

		loginCts?.Cancel();
		loginCts = new CancellationTokenSource();
		var ct = loginCts.Token;
		var temp = new MemorySessionStore();
		var qr = new SteamService(temp, DesktopAccount.DeviceName, SteamService.DesktopLoginId);
		Task.Run(() => qr.QrLoginAsync(url => msg.Post(() => { if (login == w) w.SetQr(url); }), null, ct))
			.ContinueWith(t => msg.Post(() =>
			{
				if (t.IsCompletedSuccessfully) { Log.Write("QR ile giriş yapıldı"); LoggedIn(w, t.Result); }
				else if (t.Exception?.InnerException is not (OperationCanceledException or null))
				{
					var err = t.Exception.InnerException;
					Log.Write($"giriş hatası: {err}");
					if (login == w) w.SetMessage(err.Message, bad: true);
				}
			}));

		// Kullanıcı adı + şifre (mobildeki gibi): Steam Guard adımları pencereye sorulur
		CancellationTokenSource? pwCts = null;
		w.PasswordCancel = () => pwCts?.Cancel();
		w.PasswordLogin = (user, pass) =>
		{
			pwCts?.Cancel();
			var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
			cts.CancelAfter(TimeSpan.FromMinutes(6));
			pwCts = cts;
			var svc = new SteamService(new MemorySessionStore(), DesktopAccount.DeviceName, SteamService.DesktopLoginId);
			var guard = w.CreateGuard(msg.Post);
			Task.Run(() => svc.PasswordLoginAsync(user, pass, guard, cts.Token))
				.ContinueWith(t => msg.Post(() =>
				{
					if (t.IsCompletedSuccessfully) { Log.Write("şifreyle giriş yapıldı"); LoggedIn(w, t.Result); return; }
					string? err = null;
					switch (t.Exception?.InnerException)
					{
						case null or OperationCanceledException: break; // vazgeçildi: hata değil
						case SteamException e: err = e.Message; break;
						case var e:
							Log.Write($"şifreyle giriş hatası: {e}");
							err = T("Steam'e bağlanılamadı. Biraz sonra tekrar dene.");
							break;
					}
					if (login == w) w.PasswordFailed(err);
				}));
		};
	}

	// Giriş tamam (QR ya da şifre): hesap eklenir ya da oturumu yenilenir, tavuğu aranır
	void LoggedIn(LoginWindow w, SavedSession session)
	{
		loginCts?.Cancel(); // diğer yol (şifreyle girildiyse QR, QR ile girildiyse şifre) Steam'e bağlanmayı bıraksın
		var id = session.AccountName.ToLowerInvariant();
		if (!DesktopAccount.ValidId(id))
		{
			Log.Write("girişten beklenmeyen hesap adı geldi, kaydedilmedi");
			if (login == w) w.SetMessage(T("Giriş yapılamadı. Tekrar dene."), bad: true);
			return;
		}
		EndDemo(openLogin: false); // gerçek hesap geldi: örnek tavuk kalkar
		var a = accounts.FirstOrDefault(x => x.Id == id && !x.IsDemo);
		if (a == null)
		{
			a = new DesktopAccount(id);
			accounts.Add(a);
			settings.Accounts.Add(id);
		}
		a.Store.Save(session);
		SetState(a, "idle");
		if (login == w)
		{
			w.SetDone(T("Giriş yapıldı, tavuk bilgileri alınıyor…"));
			closeLoginAt = DateTime.UtcNow.AddSeconds(1.5);
		}
		Select(a);
		Refresh("login", a);
		if (AssetManager.LoadOwn() == null) StartExtract(); // önceki indirme olmadıysa şimdi (hesapla da denenebilir)
	}

	// Dil: ayarda seçilmişse o, değilse Windows'un dili (Türkçe değilse İngilizce)
	void ApplyLanguage()
	{
		Lang.English = settings.Language switch
		{
			"en" => true,
			"tr" => false,
			_ => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "tr",
		};
		login?.Repaint();
	}

	// ---------- tray ve menü ----------

	string TrayLabel()
	{
		var p = cache.Pet;
		if (p == null) return T("Tavuk verisi bekleniyor");
		if (!p.Found) return T("Envanterde tavuk bulunamadı");
		var food = p.FoodExpiresAt is DateTimeOffset f ? Tf("Yem: {0}", Fmt.Duration(f - DateTimeOffset.Now)) : T("Yem: ?");
		return $"{p.Name} ({(p.StageName != null ? PetSummary.StageLabel(p.StageName) : "?")}) | {food}";
	}

	// tray ipucu + (açıksa) Kümes penceresi güncel durumla yeniden çizilir
	void UpdateTray()
	{
		tray.SetTip($"Kümes – {(extracting ?? TrayLabel())}");
		panel?.Update(PanelState());
	}

	// ---------- Kümes penceresi ----------

	MainModel PanelState() => new(
		accounts.Select(a => new AccountModel(
			a.Id, filming ? "Oyuncu" : a.DisplayName, a.Cache.Pet,
			cs2Running && a.State is "ok" or "idle" ? "cs2" : a.State, a.Message,
			filming ? null : a.Avatar, a.AvatarW, a.AvatarH,
			a == Sel && heroFor == a.Id ? heroImage : collection?.ThumbFor(a.Cache.Pet), a == Sel && heroFor == a.Id ? HeroSize : Collection.ThumbSize,
			a.Cache.History,
			assets != null ? AssetManager.CoatName(assets, a.Cache.Pet) : null)).ToList(),
		Sel?.Id,
		fetching,
		settings,
		IsAutostart(),
		extracting,
		Version,
		noCs2,
		ownReady,
		switching is { } sw ? (sw.Id, SwitchProgress) : null);

	// Tanıtım çekimi komutları (WM_DEMO; sadece --cekim): 1 tırman, 2 yürü (arg: +1/-1), 3 koy (arg: x px, sağa bakar),
	// 4 numara, 5 tavuğu gizle/göster, 6 koy (sola bakar)
	void DemoCommand(int cmd, int arg)
	{
		if (view == null) return;
		var b = view.Behavior;
		switch (cmd)
		{
			case 1: b.TryClimb(); break;
			case 2: b.WalkTest(arg >= 0 ? 1 : -1, 6); break;
			case 3: b.Place(arg, 1); break;
			case 4: view.DemoHop(); break;
			case 5: ChangeSetting(s => s.Visible = !s.Visible); break;
			case 6: b.Place(arg, -1); break;
		}
	}

	void OpenPanel()
	{
		RefreshOnOpen();
		if (panel != null) { panel.ShowAndFocus(); return; }
		var w = new MainWindow(PanelState());
		panel = w;
		bool moving = false;
		w.Moving = m =>
		{
			if (m == moving) return;
			moving = m;
			if (m) msg.BeginModal(); else msg.EndModal(); // pencere sürüklenirken tavuk donmasın
		};
		w.Closed = () => { if (panel == w) panel = null; };
		w.Clicked = OnPanelClick;
		w.SetIcon(windowIcon.Small, windowIcon.Big);
		w.ShowAndFocus();
	}

	// Pencere her açıldığında (tepsiden, kısayoldan) tavuk bilgisi güncellenir; az önce güncellendiyse tekrar sorulmaz.
	// İlk açılışta OnCs2 zaten günceller (CS2 açık mı bilinmeden başlanmaz).
	void RefreshOnOpen()
	{
		if (!cs2Known || fetching || !AnyLoggedIn) return;
		var oldest = accounts.Where(a => a.LoggedIn && !a.IsDemo).Min(a => a.Cache.Pet?.FetchedAt ?? DateTimeOffset.MinValue);
		if (DateTimeOffset.UtcNow - oldest < TimeSpan.FromMinutes(1)) return;
		Refresh("open");
	}

	void OnPanelClick(string id)
	{
		// hesaba özel eylemler: "eylem:hesap"
		var parts = id.Split(':', 2);
		var a = parts.Length == 2 ? accounts.FirstOrDefault(x => x.Id == parts[1]) : null;
		switch (parts[0])
		{
			case "refresh": Refresh("manual", a); break;
			case "select" when a != null: SwitchAccount(a); break;
			case "logout" when a != null: Logout(a); break;
			case "login": OpenLogin(); break;
			case "profile" when a?.Cache.Profile is { SteamId64: > 0 } pr: OpenShell($"https://steamcommunity.com/profiles/{pr.SteamId64}"); break;
			case "visible": ChangeSetting(s => s.Visible = !s.Visible); break;
			case "topmost": ChangeSetting(s => s.AlwaysOnTop = !s.AlwaysOnTop); break;
			case "hidecs2": ChangeSetting(s => s.HideWhileCs2 = !s.HideWhileCs2); break;
			case "autostart": SetAutostart(!IsAutostart()); UpdateTray(); break;
			case "quit": running = false; break;
			case "openfolder": OpenShell(Store.DataDir); break;
			case "openlog": OpenShell(Store.File("kumes.log")); break;
			case "reextract": StartExtract(confirmed: true); break;
			case "lang":
				settings.Language = Lang.English ? "tr" : "en";
				Store.Save("settings.json", settings);
				ApplyLanguage();
				UpdateTray();
				break;
		}
	}

	void OnTray(uint mouseMsg)
	{
		if (mouseMsg is WM_LBUTTONDBLCLK or NIN_BALLOONUSERCLICK) OpenPanel();
		else if (mouseMsg == WM_RBUTTONUP)
		{
			GetCursorPos(out var p);
			ShowMenu(p.X, p.Y);
		}
	}

	void ShowMenu(int x, int y)
	{
		bool loggedIn = AnyLoggedIn;
		bool autostart = IsAutostart();
		var menu = new PopupMenu()
			.Item(TrayLabel(), null, enabled: false)
			.Item(T("Kümes penceresini aç"), OpenPanel)
			.Separator()
			.Item(T("Şimdi güncelle"), () => Refresh("manual"), enabled: loggedIn && !fetching)
			.Item(AnyLoggedIn ? T("Steam hesabı ekle") : T("Steam ile giriş yap"), OpenLogin)
			.Separator()
			.Item(T("Tavuğu göster"), () => ChangeSetting(s => s.Visible = !s.Visible), isChecked: settings.Visible)
			.Item(T("Her zaman üstte"), () => ChangeSetting(s => s.AlwaysOnTop = !s.AlwaysOnTop), isChecked: settings.AlwaysOnTop)
			.Item(T("CS2 açıkken gizle"), () => ChangeSetting(s => s.HideWhileCs2 = !s.HideWhileCs2), isChecked: settings.HideWhileCs2)
			.Item(T("Windows açılınca başlat"), () => SetAutostart(!autostart), isChecked: autostart)
			.Separator()
			.Submenu(T("Animasyonlar (test)"), AnimationMenu(), enabled: view?.Visible == true)
			.Separator()
			.Item(T("Kapat"), () => running = false);
		msg.BeginModal(); // menü açıkken de tavuk hareket etsin
		try { menu.Show(msg.Hwnd, x, y); }
		finally { msg.EndModal(); }
	}

	static readonly Dictionary<string, string> AnimLabels = new()
	{
		["chick_idle01"] = "Bekleme 1", ["chick_idle02"] = "Bekleme 2", ["chick_idle03"] = "Gagalama",
		["chick_idle03alt"] = "Bekleme (uzun)", ["chick_walk"] = "Yürüme", ["chick_start_walk"] = "Yürümeye başlama",
		["chick_squat_start"] = "Oturma", ["chick_squat_loop01"] = "Oturuş 1", ["chick_squat_loop03"] = "Oturuş 2",
		["chick_squat_loop04"] = "Oturuş 3", ["chick_squat_stand"] = "Kalkma", ["chick_sleep_loop01"] = "Uyku",
		["chick_feed_to_sad01_loop"] = "Küskün (aç)", ["chick_feed01"] = "Yem yeme", ["chick_fall_flap"] = "Havada kanat çırpma",
		["chick_land"] = "Yere konma", ["chick_react01"] = "Tepki 1", ["chick_react02"] = "Tepki 2",
	};

	// Test: modeldeki her animasyonu menüden oynat (sınıfa göre gruplu)
	PopupMenu AnimationMenu()
	{
		var groups = new (string Title, Func<string, bool> Match)[]
		{
			(T("Numaralar"), n => n.Contains("trick") || n.Contains("react")),
			(T("Bekleme ve yürüme"), n => n.Contains("idle") || n.Contains("walk")),
			(T("Oturma ve uyku"), n => n.Contains("squat") || n.Contains("sleep") || n.Contains("sad")),
			(T("Diğer"), _ => true),
		};
		var menu = new PopupMenu()
			.Item(T("Sağa yürü (3 sn)"), () => view?.Behavior.WalkTest(1))
			.Item(T("Sola yürü (3 sn)"), () => view?.Behavior.WalkTest(-1))
			.Separator();
		var clips = view?.Behavior.TestClips.ToList() ?? new();
		var used = new HashSet<string>();
		foreach (var (title, match) in groups)
		{
			var sub = new PopupMenu();
			int count = 0;
			foreach (var (name, dur) in clips)
			{
				if (used.Contains(name) || !match(name)) continue;
				used.Add(name);
				count++;
				var label = AnimLabels.TryGetValue(name, out var l) ? T(l)
					: name.StartsWith("chick_trick") ? Tf("Numara {0}", name[11..].TrimStart('0')) : name;
				if (name is "chick_trick02" or "chick_trick05" or "chick_trick07") label += T(" (büyük zıplama)");
				var clip = name;
				sub.Item($"{label}\t{name[6..]} · {dur:F1} {T("sn")}", () => view?.Behavior.PlayTest(clip));
			}
			if (count > 0) menu.Submenu(title, sub);
		}
		return menu;
	}

	static bool IsAutostart()
	{
		try { return Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) != null; }
		catch { return false; }
	}

	static void SetAutostart(bool on)
	{
		try
		{
			using var key = Registry.CurrentUser.CreateSubKey(RunKey);
			if (on) key.SetValue(RunName, $"\"{Environment.ProcessPath}\" --autostart"); // Windows açılışında pencere açılmasın
			else key.DeleteValue(RunName, false);
		}
		catch (Exception e)
		{
			Log.Write($"otomatik başlatma ayarlanamadı: {e.Message}");
		}
	}

	// ---------- bildirimler ----------

	void CheckNotifications()
	{
		foreach (var a in accounts) CheckNotifications(a);
	}

	void CheckNotifications(DesktopAccount a)
	{
		CheckGrowth(a);
		if (a.IsDemo || a.Cache.Pet is not { Found: true, FoodExpiresAt: DateTimeOffset f } p) return;
		var remaining = f - DateTimeOffset.Now;
		var key = f.ToUnixTimeMilliseconds().ToString();
		var flags = a.Cache.Notified.GetValueOrDefault(key) ?? new NotifyFlags();
		bool changed = false;

		if (remaining <= TimeSpan.Zero && !flags.Expired)
		{
			tray.Notify(Tf("{0} aç kaldı", p.Name), T("Yemi bitti. CS2'ye girip besle."));
			flags.Expired = flags.Hours = flags.Day = true;
			changed = true;
		}
		else if (remaining > TimeSpan.Zero && remaining <= TimeSpan.FromHours(3) && !flags.Hours)
		{
			tray.Notify(Tf("{0} çok acıktı", p.Name), Tf("Yemin bitmesine {0} kaldı. CS2'ye girip besle.", Fmt.Duration(remaining)));
			flags.Hours = flags.Day = true;
			changed = true;
		}
		else if (remaining > TimeSpan.FromHours(3) && remaining <= TimeSpan.FromDays(1) && !flags.Day)
		{
			tray.Notify(Tf("{0} acıkıyor", p.Name), Tf("Yemin bitmesine {0} kaldı. Yarına kadar beslemeyi unutma.", Fmt.Duration(remaining)));
			flags.Day = true;
			changed = true;
		}

		if (changed)
		{
			a.Cache.Notified = new() { [key] = flags }; // sadece güncel yem dönemini tut
			a.Save();
		}
	}

	// Büyüme süresi doldu ama evre değişmedi: oyun evreyi oyuncu CS2'ye girince değiştiriyor (22 Kasım 2026, denendi).
	// Her evre geçişi için bir kere haber ver; CS2 kapanınca Kümes zaten güncelliyor, yeni evre kendiliğinden gelir.
	void CheckGrowth(DesktopAccount a)
	{
		if (a.IsDemo || a.Cache.Pet is not { Found: true, NextUpgradeAt: DateTimeOffset n } p || n > DateTimeOffset.Now) return;
		long key = n.ToUnixTimeMilliseconds();
		if (a.Cache.GrowNotified == key) return;
		var summary = PetSummary.From(p, DateTimeOffset.Now);
		if (PetSummary.StageIndex(summary.NextStage) is not (> 0 and var next)) return;
		tray.Notify(Tf("{0} büyümeye hazır", p.Name), Tf("CS2'ye girince {0}. evreye geçer.", next));
		Log.Write($"{a.Id}: büyümeye hazır ({next}. evre), bildirim gönderildi");
		a.Cache.GrowNotified = key;
		a.Save();
	}

	// ---------- CS2 modelleri ----------

	void LoadAssets()
	{
		assets = AssetManager.LoadBest();
		if (assets != null) EnsureModel();
		else Log.Write("CS2 modelleri henüz yok, çıkarılacak");

		var own = AssetManager.LoadOwn();
		ownReady = own != null;
		if (own != null) EnsureCoats(); // kapalıyken eklenen hesap ya da değişen renk
		// CS2 kurulu değilse ilk açılışta hemen inmez: önce giriş penceresi, sonra (giriş ya da "Giriş yapmadan dene") izin sorulur
		if (own == null && accounts.Count > 0) StartExtract();
		else if (own == null || AssetManager.Cs2Changed(own)) { if (!NeedsDownload) StartExtract(); }
	}

	// CS2 kurulu değil ya da Source2Viewer-CLI yok (kaynaktan derlenmiş, tools/vrf konmamış): dosyalar Valve'dan indirilir
	static bool NeedsDownload => AssetManager.FindCs2Dir() == null || AssetManager.FindCli() == null;

	// confirmed: kullanıcı zaten istedi (Ayarlar, Tekrar dene); değilse indirmeden önce izin sorulur
	void StartExtract(bool confirmed = false)
	{
		if (extractStarted) return;
		if (NeedsDownload)
		{
			if (AssetManager.FindCs2Dir() != null) Log.Write("Source2Viewer-CLI bulunamadı (tools/vrf), tavuk dosyaları Valve'dan indirilecek");
			if (confirmed) StartDownload();
			else AskDownload();
			return;
		}
		extractStarted = true;
		extracting = T("CS2 modelleri hazırlanıyor…");
		UpdateTray();
		Log.Write("CS2 modelleri çıkarılıyor");
		// İlk sefer (ya da Ayarlar'dan istendiyse) pencerede gösterilir: tavuk görünmeden önce ne olduğu belli olsun.
		// CS2 güncellendiği için arka planda yeniden çıkarılırken eski model görünmeye devam eder, pencere açılmaz.
		if (confirmed || AssetManager.LoadOwn() == null)
		{
			OpenDownloadWindow();
			download?.SetLocal("", 0, 0);
		}
		var sw = Stopwatch.StartNew();
		var pets = WantPets();
		Task.Run(() => AssetManager.Extract(pets,
				t => msg.Post(() => { extracting = t; UpdateTray(); }),
				(n, total) => msg.Post(() => download?.SetLocal(extracting ?? "", n, total))))
			.ContinueWith(t => msg.Post(() => ExtractDone(t, sw)));
	}

	// İndirme penceresi izin sorarak açılır; "İndir"e basılınca aynı pencerede başlar
	void AskDownload()
	{
		if (downloadAsked || filming) return;
		downloadAsked = true;
		Log.Write("CS2 kurulu değil: tavuk dosyalarını indirmek için izin soruluyor");
		OpenDownloadWindow();
		if (download is not { } w) return;
		w.SetAsk();
		var closed = w.Closed;
		w.Closed = () =>
		{
			closed?.Invoke();
			if (extractStarted || AssetManager.LoadOwn() != null) return;
			Log.Write("tavuk dosyaları indirilmedi (Şimdi değil)");
			tray.Notify(T("Tavuk modeli indirilmedi"), T("İstediğinde Ayarlar → Tavuk modelini indir."));
		};
	}

	// CS2 kurulu değilse: tavuk dosyaları Valve'dan anonim Steam girişiyle indirilir (sadece gerekenler).
	void StartDownload()
	{
		// Tavuk güncellemesiyle aynı anda çalışabilir: indirme ayrı Steam oturum kimliğiyle bağlanır (Cs2Download)
		if (extractStarted) return;
		// hesap gerekmez (anonim Steam girişi); giriş yapılmış hesap varsa sadece yedek olarak kullanılır
		var account = accounts.FirstOrDefault(x => x.LoggedIn && !x.IsDemo)?.Steam;
		var anon = new SteamService(new MemorySessionStore(), DesktopAccount.DeviceName, SteamService.DesktopLoginId);
		extractStarted = true;
		extracting = T("CS2 tavuk dosyaları indiriliyor…");
		UpdateTray();
		Log.Write("CS2 kurulu değil: tavuk dosyaları Valve'dan indiriliyor (anonim)");
		OpenDownloadWindow();
		var sw = Stopwatch.StartNew();
		var pets = WantPets();
		Task.Run(() => AssetManager.ExtractFromSteam(anon, account, pets,
				t => msg.Post(() => { extracting = t; UpdateTray(); }),
				n => msg.Post(() => download?.SetStep(n)),
				b => msg.Post(() => download?.AddBytes(b)),
				CancellationToken.None))
			.ContinueWith(t => msg.Post(() => ExtractDone(t, sw, downloaded: true)));
	}

	// Giriş ve tavuk hazırlama pencereleri aynı anda açıksa yan yana dursun: hazırlama penceresi en üstte, QR'ın üstüne binmesin
	void ArrangeWindows()
	{
		if (login == null || download == null) return;
		GetWindowRect(login.Hwnd, out var l);
		GetWindowRect(download.Hwnd, out var d);
		var wa = GetWorkArea();
		int lw = l.Right - l.Left, lh = l.Bottom - l.Top, dw = d.Right - d.Left, gap = (int)(16 * GetDpiForSystem() / 96f);
		int total = lw + gap + dw;
		if (total > wa.Right - wa.Left) return; // sığmıyorsa olduğu gibi kalsın
		int x = wa.Left + (wa.Right - wa.Left - total) / 2, y = Math.Max(wa.Top, wa.Top + (wa.Bottom - wa.Top - lh) / 2);
		const uint flags = SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE;
		SetWindowPos(login.Hwnd, 0, x, y, 0, 0, flags);
		SetWindowPos(download.Hwnd, 0, x + lw + gap, y, 0, 0, flags);
	}

	// Tavuk hazırlama penceresi: CS2'siz indirmede (izin, adım, MB) ve ilk sefer CS2'den çıkarırken (adım). Kapatılırsa arka planda sürer.
	void OpenDownloadWindow()
	{
		closeDownloadAt = DateTime.MaxValue;
		if (download != null) { download.Restart(); download.ShowAndFocus(); return; }
		if (filming) return;
		var w = new DownloadWindow();
		w.SetIcon(windowIcon.Small, windowIcon.Big);
		download = w;
		bool moving = false;
		w.Moving = m =>
		{
			if (m == moving) return;
			moving = m;
			if (m) msg.BeginModal(); else msg.EndModal(); // pencere sürüklenirken tavuk donmasın
		};
		w.Closed = () => { if (download == w) download = null; };
		w.Retry = () => msg.Post(() => StartExtract(confirmed: true));
		w.Accept = () => msg.Post(() => StartExtract(confirmed: true));
		w.ShowAndFocus();
		ArrangeWindows();
	}

	void ExtractDone(Task t, Stopwatch sw, bool downloaded = false)
	{
		extracting = null;
		extractStarted = false;
		if (t.IsCompletedSuccessfully && AssetManager.LoadOwn() is AssetSet own)
		{
			Log.Write($"CS2 modelleri hazır ({sw.Elapsed.TotalSeconds:F0} sn)");
			if (download != null) { download.SetDone(); closeDownloadAt = DateTime.UtcNow.AddSeconds(3); }
			assets = own;
			ownReady = true;
			collection = null; // koleksiyon resimleri yeni modellerle yeniden çizilsin
			EnsureModel(force: true); // aynı klasöre yeniden çıkarıldı: yol değişmese de dosyalar yeni
			EnsureCoats(); // hazırlık sürerken giriş yapıldıysa o tavuğun rengi
		}
		else
		{
			Log.Write($"CS2 modelleri hazırlanamadı: {t.Exception?.InnerException}");
			if (downloaded)
			{
				var why = t.Exception?.InnerException is SteamException se ? se.Message : T("Bağlantını kontrol edip yeniden dene.");
				if (download != null) download.SetFailed(why);
				else tray.Notify(T("Tavuk dosyaları indirilemedi"), why);
			}
			else download?.SetFailed(T("CS2 dosyaları okunamadı, tekrar dene."));
		}
		UpdateTray();
		ScheduleTrim();
	}

	// Doğru ırk/renk modeli yüklü değilse yükle
	// Model dosyası okunup dokular çözülürken (~3 sn) ana iş parçacığı donmasın: ağır kısım arka planda, hazır olunca
	// burada GPU'ya yüklenip tavuk değişir (o sırada eski tavuk dolaşmaya devam eder). Arka arkaya hesap değiştirilirse
	// sadece en son istenen model uygulanır.
	string? loadingKey;
	int modelGen;

	// target: modeli yüklenecek hesap (varsayılan seçili hesap). progress: 0..1 gerçek ilerleme (model okundu, dokular
	// çözüldü, hazır). defer: hazır olunca hemen uygulama, bekleyen olarak sakla (hesap değiştirmede bilgilerle birlikte uygulanır).
	void EnsureModel(bool force = false, DesktopAccount? target = null, Action<float>? progress = null, bool defer = false)
	{
		if (assets == null) { progress?.Invoke(1); return; }
		if (switching != null && target == null && !force) return; // hesap değişiyor: bitince Select zaten yükler
		var who = target ?? Sel;
		(string Model, CoatTextures Coat) r;
		try { r = AssetManager.Resolve(assets, who?.Cache.Pet); }
		catch (Exception e) { Log.Write($"model bulunamadı: {e}"); progress?.Invoke(1); return; }
		var (modelPath, coat) = r;
		var pet = who?.Cache.Pet;
		var key = $"{modelPath}|{coat.Color}|{pet?.StageName}|{pet?.Seed}"; // evre ya da seed değişince şekil de değişir
		var forId = who?.Id;
		if (!force && loadingKey == null && view?.ModelKey == key)
		{
			// aynı model zaten yüklü (ör. iki hesapta aynı tür ve renk)
			if (defer) pendingModel = () => heroFor = forId; else heroFor = forId;
			progress?.Invoke(1);
			return;
		}
		if (!force && progress == null && loadingKey == key) return; // aynısı zaten yükleniyor
		loadingKey = key;
		int gen = ++modelGen;
		pendingModel = null;
		var sw = Stopwatch.StartNew();
		void Report(float p) => msg.Post(() => { if (gen == modelGen) progress?.Invoke(p); });
		Report(0.05f);
		Task.Run(() =>
		{
			var m = GlbModel.Load(modelPath);
			Report(0.6f);
			PetRenderer.Preload(coat);
			Report(0.95f);
			return m;
		}).ContinueWith(t => msg.Post(() =>
		{
			if (gen != modelGen) return; // bu arada başka bir model istendi
			loadingKey = null;
			if (t.IsFaulted) { Log.Write($"model yüklenemedi: {t.Exception?.InnerException}"); PetRenderer.ClearPreloaded(); progress?.Invoke(1); return; }
			void Apply()
			{
				ApplyModel(t.Result, coat, key, modelPath, sw);
				heroFor = forId;
				UpdateTray();
			}
			if (defer) pendingModel = Apply; else Apply();
			progress?.Invoke(1);
		}));
	}

	// ---- hesap değiştirme ----
	// Yeni hesabın tavuğu hazır olana kadar eski hesap ekranda kalır; Kümes penceresinde yükleme ekranı (0-100) görünür.
	// Hazır olunca bilgiler ve masaüstündeki tavuk birlikte değişir. Çubuk en az SwitchMinSec'de dolar (yoksa göz kırpar).
	DesktopAccount? switching;
	float switchLoad;     // gerçek ilerleme
	long switchStart;
	Action? pendingModel; // hazır ama henüz uygulanmamış model (hesap değiştirme bitince uygulanır)
	const double SwitchMinSec = 0.5;

	// Gösterilen ilerleme zamanla akar ama gerçek ilerlemenin çok önüne geçmez (bitmeden %95'i aşmaz)
	float SwitchProgress => switching == null ? 0
		: (float)Math.Min(switchLoad >= 1 ? 1 : Math.Min(0.95, switchLoad + 0.4), Stopwatch.GetElapsedTime(switchStart).TotalSeconds / SwitchMinSec);

	void SwitchAccount(DesktopAccount a)
	{
		if (a == Sel && switching == null) return;
		switching = a;
		switchLoad = 0;
		switchStart = Stopwatch.GetTimestamp();
		Log.Write($"hesap değiştiriliyor: {a.Id}");
		// önce tavuğun rengi hazır değilse o (çubuğun ilk yarısı), sonra model (ikinci yarısı)
		EnsureCoats(p => { if (switching == a) switchLoad = Math.Max(switchLoad, p * 0.5f); }, () =>
		{
			if (switching != a) return;
			EnsureModel(target: a, progress: p => switchLoad = Math.Max(switchLoad, 0.5f + p * 0.5f), defer: true);
		});
		UpdateTray();
	}

	// ---- sadece gereken renkler ----
	// Hazırlık bütün türlerin modellerini ama sadece gereken renkleri içerir (demo, civciv ve hesapların tavukları).
	// Bir tavuğun rengi yoksa (yeni hesap, oyunda renk değişti) sadece o eklenir: CS2 kuruluysa kurulumdan, değilse
	// Valve'dan (birkaç MB). Bitince model yeniden yüklenir.
	IEnumerable<PetInfo?> WantPets() => accounts.Select(a => a.Cache.Pet).Where(p => p is { Found: true }).ToList(); // demo: Silkie Splash, zaten hep var

	bool addingCoats;
	readonly List<Action> coatsDone = new();

	void EnsureCoats(Action<float>? progress = null, Action? done = null)
	{
		if (done != null) coatsDone.Add(done);
		if (addingCoats) return;
		List<(string Breed, string Coat, string Vmat)> missing = [];
		if (!extractStarted && AssetManager.LoadOwn() is { } own)
			using (own.Manifest) missing = AssetManager.MissingCoats(own, WantPets());
		if (missing.Count == 0) { FinishCoats(); return; }
		addingCoats = true;
		Log.Write($"eksik renkler hazırlanıyor: {string.Join(", ", missing.Select(m => $"{m.Breed}/{m.Coat}"))}");
		progress?.Invoke(0.1f);
		bool local = !NeedsDownload;
		long got = 0, expect = missing.Count * 6L * 1024 * 1024; // indirmede renk başına ~6 MB (parça fazlasıyla)
		var account = accounts.FirstOrDefault(x => x.LoggedIn && !x.IsDemo)?.Steam;
		var anon = local ? null : new SteamService(new MemorySessionStore(), DesktopAccount.DeviceName, SteamService.DesktopLoginId);
		var sw = Stopwatch.StartNew();
		Task.Run(() =>
		{
			if (local) AssetManager.AddCoatsLocal(missing);
			else AssetManager.AddCoatsFromSteam(anon!, account, missing,
				b => msg.Post(() => progress?.Invoke(0.1f + 0.8f * Math.Min(1f, (got += b) / (float)expect))), CancellationToken.None);
		}).ContinueWith(t => msg.Post(() =>
		{
			addingCoats = false;
			if (t.IsFaulted) Log.Write($"renkler eklenemedi: {t.Exception?.InnerException}");
			else
			{
				Log.Write($"renkler hazır ({sw.ElapsedMilliseconds} ms)");
				if (AssetManager.LoadOwn() is { } set) { assets = set; collection = null; }
			}
			progress?.Invoke(1);
			FinishCoats();
			EnsureModel(); // seçili tavuğun rengi artık hazırsa yüklenir
		}));
	}

	void FinishCoats()
	{
		var list = coatsDone.ToList();
		coatsDone.Clear();
		foreach (var d in list) d();
	}

	// her döngüde: yükleme ekranını ilerlet, bitince bilgileri ve tavuğu birlikte değiştir
	void TickSwitch()
	{
		if (switching is not { } a) return;
		if (switchLoad >= 1 && SwitchProgress >= 1)
		{
			switching = null;
			var apply = pendingModel;
			pendingModel = null;
			if (!accounts.Contains(a)) { UpdateTray(); return; }
			settings.Selected = a.Id; // önce seçili hesap: model uygulanırken tavuğun evre şekli yeni hesaba göre hesaplansın
			apply?.Invoke();
			Select(a);
			return;
		}
		panel?.Update(PanelState());
	}

	void ApplyModel(GlbModel model, CoatTextures coat, string key, string modelPath, Stopwatch sw)
	{
		try
		{
			long bg = sw.ElapsedMilliseconds;
			if (view == null)
			{
				view = new PetView(gl, model, coat, key, () => cache.Pet, StatusText);
				view.MenuRequested = ShowMenu;
				view.HoldChanged = _ => view.KeepOnTop();
				view.Behavior.Filming = filming;
			}
			else
			{
				view.ReplaceModel(model, coat, key);
			}
			heroImage = view.RenderHero(HeroSize);
			if (assets != null && collection == null) collection = new Collection(gl, assets);
			collection?.Want(accounts.Select(x => x.Cache.Pet));
			UpdateTray();
			Log.Write($"model yüklendi: {Path.GetFileName(modelPath)}, {Path.GetFileName(coat.Color)} ({sw.ElapsedMilliseconds} ms, ana iş parçacığında {sw.ElapsedMilliseconds - bg} ms)");
			ApplyVisibility();
			ScheduleTrim();
		}
		catch (Exception e)
		{
			Log.Write($"model yüklenemedi: {e}");
		}
		finally
		{
			PetRenderer.ClearPreloaded();
		}
	}
}
