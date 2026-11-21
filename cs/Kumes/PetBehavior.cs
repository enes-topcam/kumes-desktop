// pet.js'teki davranış durum makinesinin C# karşılığı: yürü, bekle, gagala, otur, uyu, kanat çırp;
// fareyle tutulma, havada kanat çırpma, bırakınca süzülerek iniş, tıklayınca numara + kalp.
// Tüm uzunluklar fiziksel piksel; pet.js'teki CSS px değerleri ekran ölçeğiyle çarpılır.
using Kumes.Render;

namespace Kumes;

sealed class Particle
{
	public float Dy;      // ayaklardan yukarı (px)
	public float Vy;
	public float Life;
	public string Text = "";
}

sealed class PetBehavior
{
	static readonly Dictionary<string, (float Min, float Max)> Dur = new()
	{
		["walk"] = (3, 8), ["idle"] = (1.5f, 4), ["peck"] = (1.5f, 3.5f), ["sit"] = (5, 12), ["sleep"] = (20, 45),
	};
	const float Accel = 3.5f; // hız yumuşatma katsayısı

	PetAnimator anim;
	readonly float s;
	readonly Func<DateTimeOffset?> foodExpiresAt;
	readonly Random rng = new();

	public float X;
	public int Dir = 1;
	public string Mode = "idle";
	public float Speed;            // px/sn, yumuşak hızlanıp yavaşlar
	public bool Hovering;
	public float MouseX;
	public float Alt;              // yerden yükseklik (px)
	float vy;                      // düşüş hızı
	public bool Held;              // fareyle tutuluyor
	public bool Airborne;          // tutulurken yerden kalktı mı (kanat çırpma sadece o zaman)
	public bool Falling;           // bırakıldı, süzülerek iniyor
	public float DragSpeed;        // tutarken yatay sürükleme hızı
	float prevX;
	float modeT, modeDur = 2;
	string idleVariant = "idle1";
	public readonly List<Particle> Particles = new();

	// Pencere kenarına konma: OnLedge doluysa tavuk o kenarın üstünde durur (Alt = kenarın yüksekliği).
	// FindLanding(x, öncekiAlt, yeniAlt): düşerken bu karede geçilen kenar varsa onu döndürür.
	public Kumes.Native.Perch? OnLedge;
	public Func<float, float, float, Kumes.Native.Perch?>? FindLanding;
	public float SupportAlt => OnLedge?.Alt ?? 0; // ayakların bastığı yer (0 = görev çubuğu)

	// Kenar kayboldu (pencere kapandı/küçüldü/önüne başka pencere geldi): kanat çırparak aşağı süzül
	public void LoseLedge(string reason)
	{
		if (OnLedge != null) Log.Write($"kenardan düştü: {reason}");
		OnLedge = null;
		if (Held || Falling || Jumping) return;
		StartJump(Dir * 60 * s, 20 * s); // küçük bir zıplama, sonra süzülerek aşağıdaki ilk yere in
	}

	// Konduğu pencere aşağı çekildi: tutunmayı bırak, olduğu yerden düz süzül (zıplamadan)
	public void DropFromLedge()
	{
		OnLedge = null;
		if (InAir) return;
		Falling = true;
		vy = 0;
		glideVx = 0;
	}

	// ---- zıplama: sabit güçte, gerçek gibi ----
	// Tavuk hedefe göre zıplamaz; hep aynı güçte zıplar (başlangıç hızı + yer çekimi). Tepe noktasında elle
	// bırakılmış gibi kanat çırparak süzülmeye geçer, zıplamanın ileri hızı süzülmede de sürer (iniş eğik
	// başlayıp dikleşir). Süzülürken altına ilk gelen pencere kenarına konar; yetişemezse aşağı devam eder.
	// FindClimbTarget(): yakında, daha yüksekte bir kenar (tırmanmayı DENER; çok yüksekse yetişemez)
	public Func<Kumes.Native.Perch?>? FindClimbTarget;
	public bool Jumping;
	float jvx, jvy;
	float glideVx; // süzülürken yatay hız (zıplamadan kalan; yavaşça azalır)

	const float JumpGravity = 1100;      // px/sn² (ölçeklenmeden önce): zıplarken yer çekimi
	public const float HopHeight = 45;   // kenardan ayrılırken sabit zıplama yüksekliği (px)
	public const float ClimbHeight = 130; // yukarı tırmanmayı denerken en fazla bu kadar yükselir (px)
	public const float HopSpeed = 140;   // ileri hız (px/sn)

	// vx: ileri hız (px/sn, işaretli); height: tepe noktasının bulunduğu yerden yüksekliği (px)
	void StartJump(float vx, float height)
	{
		jvx = vx;
		jvy = MathF.Sqrt(2 * JumpGravity * s * height);
		if (Math.Abs(vx) > 1) Dir = vx > 0 ? 1 : -1;
		OnLedge = null;
		Jumping = true;
		Speed = 0;
	}

	void UpdateJump(float dt)
	{
		X += jvx * dt;
		Alt += jvy * dt;
		jvy -= JumpGravity * s * dt;
		float lo = 40 * s, hi = lastWorldWidth - 40 * s;
		if (X < lo || X > hi) { X = Math.Clamp(X, lo, hi); jvx = 0; } // ekranın kenarına çarptı
		if (jvy > 0) return;
		// tepe noktası: süzülmeye geç (elle bırakılınca olanın aynısı); ileri hız sürsün
		Jumping = false;
		Falling = true;
		vy = 0;
		glideVx = jvx;
	}

	// süzülürken yatayda kayma: zıplamanın hızıyla başlar, ~1,5 sn'de büyük ölçüde azalır (en az 25 px/sn sürüklenir)
	void UpdateGlideDrift(float dt)
	{
		if (glideVx == 0) return;
		float sign = Math.Sign(glideVx);
		float speed = Math.Max(Math.Abs(glideVx) * MathF.Exp(-dt / 1.5f), 25 * s);
		glideVx = sign * speed;
		X += glideVx * dt;
		float lo = 40 * s, hi = lastWorldWidth - 40 * s;
		if (X < lo || X > hi) { X = Math.Clamp(X, lo, hi); glideVx = 0; } // ekranın kenarında dur
	}

	// Yürürken kenarın ucuna gittiği yönde vardı: her zaman aynı zıplamayla dışarı atlar (nereye yürüyeceği
	// zaten rastgele). Yakında pencere varsa süzülürken ona konar, yetişemezse aşağıya iner.
	// Sadece kenar ekranın ucundaysa geri döner.
	bool TryLeaveLedge(int dir, float worldWidth)
	{
		if (OnLedge is not { } l) return false;
		float edge = dir > 0 ? l.Right : l.Left;
		if (edge + dir * 45 * s < 40 * s || edge + dir * 45 * s > worldWidth - 40 * s) return false; // ekranın dışına zıplamasın
		StartJump(dir * HopSpeed * s, HopHeight * s);
		return true;
	}

	float LiftPx => 10 * s;        // bu kadar yükselince "havada" say
	float GroundPx => 4 * s;       // bunun altına inince "yere değdi" say
	float GlideGravity => 260 * s; // px/sn²
	float GlideMaxSpeed => 95 * s; // kanat çırparak yavaş iniş

	public float WalkSpeedPx => PetAnimator.WalkUnitsPerSec * PetRenderer.PxPerUnitCss * s;
	public float SpeedScale => (Held ? DragSpeed : Speed) / WalkSpeedPx;
	public bool InAir => Held || Falling || Jumping;

	public PetBehavior(PetAnimator anim, float scale, Func<DateTimeOffset?> foodExpiresAt, float startX)
	{
		this.anim = anim;
		s = scale;
		this.foodExpiresAt = foodExpiresAt;
		X = startX;
		prevX = startX;
		NextMode();
	}

	public void SetAnimator(PetAnimator a) => anim = a;

	public string Hunger()
	{
		var f = foodExpiresAt();
		if (f == null) return "ok";
		var left = f.Value - DateTimeOffset.Now;
		if (left <= TimeSpan.Zero) return "starving";
		if (left <= TimeSpan.FromDays(1)) return "hungry";
		return "ok";
	}

	string Pick(params (string Name, float W)[] weights)
	{
		var list = weights.Where(w => w.W > 0).ToArray();
		float r = (float)rng.NextDouble() * list.Sum(w => w.W);
		foreach (var (name, w) in list)
			if ((r -= w) <= 0) return name;
		return list[0].Name;
	}

	void NextMode()
	{
		int hour = DateTime.Now.Hour;
		bool night = hour >= 1 && hour < 7;
		string mode = Hunger() switch
		{
			"starving" => Pick(("sit", 6), ("idle", 1)),
			"hungry" => Pick(("walk", 1.5f), ("idle", 1), ("peck", 3), ("sit", 3), ("flap", 0.3f)),
			// "flap" = kendiliğinden numara (tıklamadaki numaralar + kanat çırpma); ortalama ~40 sn'de bir
			// "climb" = yakındaki daha yüksek bir pencereye zıpla
			_ when Demo => Pick(("idle", 2), ("peck", 2), ("flap", 1)), // çekimde uyumasın, kendi yürüyüp tırmanmasın (komutla yürür)
			_ => Pick(("walk", 4), ("idle", 2), ("peck", 3), ("sit", 1), ("sleep", night ? 5 : 0.25f), ("flap", 1.5f), ("climb", 0.6f)),
		};
		modeT = 0;

		if (mode == "climb")
		{
			if (Speed < 3 * s && TryClimb()) return;
			mode = "idle"; // çıkacak yer yok
		}

		// kendiliğinden numara/kanat çırpma (CS2 animasyonu); olmazsa normal beklemeye düş
		if (mode == "flap")
		{
			float d = Speed < 3 * s ? anim.SpontaneousTrick() : 0;
			Mode = "idle";
			modeDur = d > 0 ? d + 0.4f : 2;
			return;
		}

		Mode = mode;
		var (a, b) = Dur[mode];
		modeDur = a + (float)rng.NextDouble() * (b - a);
		if (mode == "walk" && rng.NextDouble() < 0.35)
		{
			Dir = -Dir;
			Speed *= 0.2f; // geri dönerken önce yavaşlasın
		}
		if (mode == "idle")
		{
			double r = rng.NextDouble();
			idleVariant = r < 0.15 && anim.Has("idle3alt") ? "idle3alt" : r < 0.575 ? "idle1" : "idle2";
		}
		if (mode == "peck") idleVariant = "idle3";
		if (mode == "sit")
		{
			// oyundaki oturma çeşitleri (eski model setinde yoksa ilki)
			var options = new[] { "sit", "sit3", "sit4" }.Where(anim.Has).ToArray();
			sitVariant = options.Length > 0 ? options[rng.Next(options.Length)] : "sit";
		}
	}

	string sitVariant = "sit";

	// Sabit güçle yukarı zıplamayı dene: tepe noktası ~130 px yukarıda. Hedef ondan alçaksa süzülürken
	// üstüne konar, yüksekse yetişemez ve aşağı süzülür. İleri hız hedefin ortasına yönelir (sınırlı).
	public bool TryClimb()
	{
		if (InAir || FindClimbTarget?.Invoke() is not { } t) return false;
		float aim = Math.Clamp(X, t.Left + 30 * s, Math.Max(t.Left + 30 * s, t.Right - 30 * s));
		float tApex = MathF.Sqrt(2 * ClimbHeight * s / (JumpGravity * s));
		float vx = Math.Clamp((aim - X) / (tApex + 0.3f), -HopSpeed * s, HopSpeed * s);
		StartJump(vx, ClimbHeight * s);
		return true;
	}

	// ---- tanıtım videosu çekimi (--demo) ----
	public bool Demo;

	// Görev çubuğunun üstünde verilen yere koy (kesme arasında; yürüyerek gitmez)
	public void Place(float x, int dir)
	{
		Held = Falling = Jumping = Airborne = false;
		OnLedge = null;
		X = prevX = x;
		Alt = 0;
		vy = 0;
		Speed = 0;
		Dir = dir;
		StandStill(1);
	}

	// davranış modu -> CS2 animasyonu
	public string DesiredAnim()
	{
		if (Held) return Airborne ? "held" : DragSpeed > 15 * s ? "walk" : idleVariant; // havada: kanat çırp
		if (Falling || Jumping) return "fall";
		if (Speed > 6 * s) return "walk"; // yavaşlarken de yürüme animasyonu sürsün
		return Mode switch
		{
			"walk" => "walk",
			"sleep" => "sleep",
			"sit" => Hunger() is "hungry" or "starving" ? "sad" : sitVariant,
			"peck" => "idle3",
			_ => idleVariant,
		};
	}

	float lastWorldWidth = 1920;

	public void Update(float dt, float worldWidth)
	{
		lastWorldWidth = worldWidth;
		if (InAir)
		{
			modeT = 0; // tutulurken/düşerken/atlarken davranış zamanlayıcısı durur
		}
		else // fare üstüne gelince durmaz, dönmez; kendi işine devam eder (uyuyorsa da uyanmaz, tıklayınca kalkar)
		{
			modeT += dt;
			if (modeT >= modeDur) NextMode();
		}

		// tutulurken: yerden kalkma / yere değme ve sürükleme hızı
		if (Held)
		{
			float inst = Math.Abs(X - prevX) / Math.Max(dt, 0.001f);
			DragSpeed += (inst - DragSpeed) * Math.Min(1, dt * 8);
			if (!Airborne && Alt > LiftPx) Airborne = true;
			else if (Airborne && Alt < GroundPx)
			{
				Airborne = false;
				anim.Land(); // tutarken yere koydu: kon ve normal duruşa dön
			}
		}
		else
		{
			DragSpeed = 0;
		}
		prevX = X;

		if (Jumping) UpdateJump(dt);

		// süzülerek iniş: yolda bir pencere kenarı varsa ona kon, yoksa görev çubuğuna
		if (Falling)
		{
			UpdateGlideDrift(dt);
			float before = Alt;
			vy = Math.Min(GlideMaxSpeed, vy + GlideGravity * dt);
			Alt -= vy * dt;
			if (FindLanding?.Invoke(X, before, Alt) is Kumes.Native.Perch ledge)
			{
				OnLedge = ledge;
				Landed();
			}
			else if (Alt <= 0)
			{
				OnLedge = null;
				Landed();
			}
		}

		// yürüme: hedef hıza yumuşakça yaklaş; hız animasyonla aynı ki ayaklar kaymasın
		float full = WalkSpeedPx * (Hunger() == "hungry" ? 0.6f : 1);
		float target = Mode == "walk" && !InAir ? full : 0;
		Speed += (target - Speed) * Math.Min(1, dt * Accel);
		if (InAir) Speed = 0;
		if (Speed > 0.5f * s)
		{
			X += Dir * Speed * dt;
			// sınırlar: ekranın kenarları ya da konduğu pencere kenarının uçları (uca gelince döner)
			float lo = 40 * s, hi = worldWidth - 40 * s;
			if (OnLedge is { } l)
			{
				lo = Math.Max(lo, l.Left + 22 * s);
				hi = Math.Min(hi, l.Right - 22 * s);
				if (hi < lo) lo = hi = (l.Left + l.Right) / 2;
			}
			// Kenar sonradan kısaldıysa (önüne pencere geldi) tavuk uçtan çok dışarıda kalır: yeni uca çekme
			// (ışınlanma gibi görünür), yürüyerek içeri dönsün.
			float step = Speed * dt + 2 * s;
			if (X < lo - step) Dir = 1;
			else if (X > hi + step) Dir = -1;
			// kenarın ucuna gittiği yönde vardıysa: ara sıra yana ya da aşağı zıpla, yoksa geri dön
			else if (X < lo) { X = lo; if (!(Dir < 0 && TryLeaveLedge(-1, worldWidth))) Dir = 1; }
			else if (X > hi) { X = hi; if (!(Dir > 0 && TryLeaveLedge(1, worldWidth))) Dir = -1; }
		}

		for (int i = Particles.Count - 1; i >= 0; i--)
		{
			var p = Particles[i];
			p.Dy -= p.Vy * dt;
			p.Life -= dt;
			if (p.Life <= 0) Particles.RemoveAt(i);
		}
	}

	// tıklama: kalp + ayaktaysa numara; uyuyor/oturuyorsa dürtülünce kalkar (oyundaki kalkma animasyonuyla)
	public void Hop(float headHeight)
	{
		if (InAir) return;
		Particles.Add(new Particle { Dy = headHeight, Vy = 30 * s, Life = 1.4f, Text = "❤️" });
		if (Mode is "sit" or "sleep" || anim.IsSeated)
		{
			StandStill(1.5f + (float)rng.NextDouble() * 1.5f);
			return;
		}
		float d = anim.Trick();
		if (d > 0) StandStill(d); // numara bitene kadar yürümesin (ayaklar kaymasın)
	}

	public IEnumerable<(string Name, float Duration)> TestClips => anim.TestClips;

	// Test menüsü: verilen yöne birkaç saniye yürü (kenarın ucuna varırsa normal kural: atla/süzül/dön)
	public void WalkTest(int dir, float seconds = 3)
	{
		if (InAir) return;
		Mode = "walk";
		Dir = dir;
		modeT = 0;
		modeDur = seconds;
	}

	// Test menüsü: seçilen animasyonu oynat, bitene kadar yerinde dursun
	public void PlayTest(string name)
	{
		if (InAir) return;
		if (name == "chick_sleep_loop01")
		{
			// gerçek uyku: oturma geçişiyle yatar, "zzz" çıkar, 20 sn sonra kendi kalkar
			Mode = "sleep";
			modeT = 0;
			modeDur = 20;
			return;
		}
		Mode = "idle"; // yürümeyi durdur; klip bitince normal duruşa döner
		float d = anim.PlayTest(name);
		if (d > 0) StandStill(d);
	}

	// CS2'de beslendikten sonra yem yeme animasyonu
	public void Feed()
	{
		if (InAir) return;
		float d = anim.Feed();
		if (d > 0) StandStill(d);
	}

	void StandStill(float seconds)
	{
		Mode = "idle";
		modeT = 0;
		modeDur = seconds + 0.4f;
	}

	// ---- fareyle tutma (koordinatlar dünya: çalışma alanına göre x, alttan yükseklik) ----

	public void StartHold()
	{
		OnLedge = null; // kenardan kaldırıldı
		Jumping = false; // atlarken yakalandı
		Held = true;
		Airborne = Alt > LiftPx; // düşerken yakalandıysa havada başlar
		Falling = false;
		vy = 0;
		Hovering = false;
	}

	public void MoveHold(float x, float alt, float maxAlt, float worldWidth, float moveX)
	{
		X = Math.Clamp(x, 30 * s, worldWidth - 30 * s);
		Alt = alt < GroundPx ? 0 : Math.Min(alt, maxAlt); // ekranın üstünden taşmasın
		if (Math.Abs(moveX) > 2) Dir = moveX > 0 ? 1 : -1;
	}

	public void EndHold()
	{
		Held = false;
		Airborne = false;
		if (Alt > GroundPx)
		{
			Falling = true;
			vy = 0;
			glideVx = 0; // elle bırakıldı: düz süzül
		}
		else
		{
			Landed();
		}
	}

	void Landed()
	{
		bool wasFalling = Falling;
		Alt = SupportAlt;
		vy = 0;
		glideVx = 0;
		Falling = false;
		Mode = "idle";
		modeT = 0;
		modeDur = 1.5f + (float)rng.NextDouble() * 2;
		if (wasFalling) anim.Land();
	}
}
