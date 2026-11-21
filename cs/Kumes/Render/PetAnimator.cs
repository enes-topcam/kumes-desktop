// pet3d.js'in C# karşılığı: davranış durumunu CS2 animasyonlarına çevirir, aralarına oyundaki
// geçiş animasyonlarını koyar, tek seferlik numaraları oynatır, tavuğu sağa/sola döndürür.
using System.Diagnostics;
using System.Numerics;

namespace Kumes.Render;

record struct AnimState(string Desired, int Dir, float SpeedScale, float Dt);

sealed class PetAnimator
{
	public const float WalkUnitsPerSec = 0.248f / 1.2667f; // chick_walk içindeki root_motion ilerlemesi
	const float FaceYaw = 0.45f; // kameraya hafif dönük (3/4 görünüm)
	const float DefaultFade = 0.45f;

	static readonly Dictionary<string, string> Loop = new()
	{
		["walk"] = "chick_walk",
		["idle1"] = "chick_idle01",
		["idle2"] = "chick_idle02",
		["idle3"] = "chick_idle03",
		["idle3alt"] = "chick_idle03alt",
		["sit"] = "chick_squat_loop01",
		["sit3"] = "chick_squat_loop03",
		["sit4"] = "chick_squat_loop04",
		["sleep"] = "chick_sleep_loop01",
		["sad"] = "chick_feed_to_sad01_loop",
		["held"] = "chick_fall_flap",
		["fall"] = "chick_fall_flap",
	};
	static readonly HashSet<string> Seated = ["sit", "sit3", "sit4", "sleep", "sad"];
	static readonly HashSet<string> Air = ["held", "fall"];
	// tıklayınca: sadece yerinde kalanlar (trick02/05/07 tavuğu boyunun 3-4 katı zıplatıp pencereden çıkarıyor)
	public static readonly string[] Tricks =
	[
		"chick_react01", "chick_react02", "chick_trick01", "chick_trick03", "chick_trick06",
		"chick_trick08", "chick_trick09", "chick_trick10", "chick_trick12", "chick_trick13",
	];
	// kendiliğinden ara sıra: kanatların belirgin açıldığı animasyonlar (ölçüm: kanat kemiği 50°+ dönüyor)
	// ve daha uzun gösteriler
	static readonly string[] SpontaneousTricks =
	[
		"chick_trick08", "chick_trick13", "chick_trick04", "chick_react01",
		"chick_trick03", "chick_trick10", "chick_trick11",
	];
	// Not: oyundaki chick_sleep_flinch* (uyurken irkilme) dışa sadece "ekleme" (additive) türünde çıkıyor; tek başına
	// oynatınca tavuk havada yan yatmış görünüyor. Bu yüzden kullanılmıyor: dürtülen tavuk onun yerine kalkıyor.
	public bool IsSeated => Seated.Contains(desired);
	static readonly Dictionary<string, float> QuickFade = new() { ["chick_walk"] = 0.25f, ["chick_fall_flap"] = 0.2f, ["chick_land"] = 0.15f };

	readonly AnimationMixer mixer;
	readonly Dictionary<string, Clip> clips;
	readonly Random rng = new();
	string desired = "";
	readonly Queue<string> queue = new();
	Layer? current;
	bool oneShot;
	long oneShotDeadline;
	float yaw = MathF.PI / 2 - FaceYaw;

	public AnimationMixer Mixer => mixer;

	public PetAnimator(GlbModel model)
	{
		clips = model.Clips;
		rootBone = Array.IndexOf(model.Skeleton.Names, "root");
		if (rootBone >= 0)
		{
			var rest = new AnimationMixer(model.Skeleton); // klip yok = dinlenme pozu
			rest.Update(0, Matrix4x4.Identity);
			rootRestY = rest.World[rootBone].Translation.Y;
		}
		mixer = new AnimationMixer(model.Skeleton);
		mixer.Finished += OnFinished;
		SetDesired("idle1");
	}

	bool Play(string name, bool once)
	{
		if (!clips.TryGetValue(name, out var clip)) return false;
		float fade = QuickFade.GetValueOrDefault(name, DefaultFade);
		if (current?.Clip == clip && !once) return true;
		var prev = current;
		var next = mixer.Play(clip, once);
		// iki animasyon kendi hızında oynar, ağırlıklar yumuşakça yer değiştirir
		if (prev != null && prev.Clip != clip) AnimationMixer.FadeOut(prev, fade);
		AnimationMixer.FadeIn(next, fade);
		current = next;
		return true;
	}

	void Advance()
	{
		while (queue.Count > 0)
		{
			var name = queue.Dequeue();
			if (Play(name, queue.Count > 0)) return;
		}
	}

	void OnFinished(Layer l)
	{
		if (l != current) return;
		if (oneShot)
		{
			oneShot = false;
			queue.Clear();
			queue.Enqueue(Loop.GetValueOrDefault(desired, Loop["idle1"]));
		}
		Advance();
	}

	// Bir durumdan diğerine geçerken araya oyundaki geçiş animasyonlarını koy
	static string[] TransitionQueue(string prev, string d)
	{
		var loop = Loop.GetValueOrDefault(d, Loop["idle1"]);
		if (prev == "" || Air.Contains(d) || Air.Contains(prev)) return [loop];
		bool wasSeated = Seated.Contains(prev), isSeated = Seated.Contains(d);
		if (!wasSeated && isSeated) return ["chick_squat_start", loop];
		if (wasSeated && !isSeated)
			return d == "walk" ? ["chick_squat_stand", "chick_start_walk", Loop["walk"]] : ["chick_squat_stand", loop];
		if (d == "walk" && prev != "walk") return ["chick_start_walk", loop];
		return [loop];
	}

	void SetDesired(string d)
	{
		if (d == desired) return;
		var prev = desired;
		desired = d;
		if (oneShot)
		{
			if (!Air.Contains(d)) return; // numara bitince uygulanır; ama tutulursa hemen kes
			oneShot = false;
		}
		queue.Clear();
		foreach (var n in TransitionQueue(prev, d)) queue.Enqueue(n);
		Advance();
	}

	bool PlayOneShot(string name)
	{
		if (!clips.TryGetValue(name, out var clip)) return false;
		queue.Clear();
		oneShot = Play(name, true);
		// emniyet: bir sebeple bitiş gelmezse gerçek zamana göre bırak
		if (oneShot) oneShotDeadline = Stopwatch.GetTimestamp() + (long)((clip.Duration * 1.5 + 1) * Stopwatch.Frequency);
		return oneShot;
	}

	void CheckOneShotDeadline()
	{
		if (oneShot && Stopwatch.GetTimestamp() > oneShotDeadline)
		{
			oneShot = false;
			queue.Clear();
			queue.Enqueue(Loop.GetValueOrDefault(desired, Loop["idle1"]));
			Advance();
		}
	}

	// Bu durum için animasyon var mı? (Electron'un eski model setinde yeni animasyonlar yok)
	public bool Has(string state) => Loop.TryGetValue(state, out var c) && clips.ContainsKey(c);

	float PlayRandom(string[] names)
	{
		var avail = names.Where(clips.ContainsKey).ToArray();
		if (avail.Length == 0) return 0;
		var name = avail[rng.Next(avail.Length)];
		return PlayOneShot(name) ? clips[name].Duration : 0;
	}

	// tıklama: ayaktaysa numara (otururken numara yapmaz; davranış onu kaldırır). Oynatılan klibin süresi (sn).
	public float Trick()
	{
		if (oneShot || Air.Contains(desired) || Seated.Contains(desired)) return 0;
		return PlayRandom(Tricks);
	}

	// kendiliğinden numara: tıklamadaki numaralar + kanat çırpma/uzun gösteriler; klibin süresini döndürür (sn)
	public float SpontaneousTrick()
	{
		if (oneShot || Air.Contains(desired) || Seated.Contains(desired)) return 0;
		return PlayRandom(Tricks.Union(SpontaneousTricks).ToArray());
	}

	// Test menüsü: modeldeki tüm klipler (bozuk olduğu bilinen additive irkilmeler hariç) ve süreleri
	// Tavuğu boyunun 3-4 katı zıplatan numaralar (sadece test menüsünde). Oynarken pencere tavuğu takip eder.
	static readonly HashSet<string> BigJumps = ["chick_trick02", "chick_trick05", "chick_trick07"];
	int rootBone = -1;
	float rootRestY;

	// Büyük zıplamalarda iniş, çıkışa göre çok ani (trick02: ~250 px'i 0,3 sn'de iniyor). Gövde bundan hızlı
	// inerken klibi yavaşlat; iniş ~1 sn'ye yayılır.
	const float MaxFallSpeed = 0.95f; // model birimi/sn (~245 px/sn)
	float jumpTimeScale = 1, prevRootY = float.NaN;

	bool IsBigJump(Layer? l) => l != null && BigJumps.Contains(l.Clip.Name);

	void UpdateFallSpeed(float dt)
	{
		if (rootBone < 0 || !IsBigJump(current) || dt <= 0)
		{
			jumpTimeScale = 1;
			prevRootY = float.NaN;
			return;
		}
		float y = mixer.World[rootBone].Translation.Y;
		if (!float.IsNaN(prevRootY))
		{
			float vClip = (y - prevRootY) / (dt * current!.TimeScale); // klibin kendi zamanında dikey hız
			float target = vClip < -MaxFallSpeed ? Math.Max(0.3f, MaxFallSpeed / -vClip) : 1;
			jumpTimeScale += (target - jumpTimeScale) * Math.Min(1, dt * 8); // yumuşak geçiş
		}
		prevRootY = y;
	}

	// Büyük zıplamada gövdenin (root kemiği) dinlenme pozuna göre ne kadar yükseldiği (model birimi; değilse 0)
	public float JumpHeight()
	{
		if (rootBone < 0 || !mixer.Layers.Any(l => l.Weight > 0 && BigJumps.Contains(l.Clip.Name))) return 0;
		return Math.Max(0, mixer.World[rootBone].Translation.Y - rootRestY);
	}

	public IEnumerable<(string Name, float Duration)> TestClips =>
		clips.Values.Where(c => !c.Name.StartsWith("chick_sleep_flinch")).OrderBy(c => c.Name).Select(c => (c.Name, c.Duration));

	// Test menüsü: seçilen klibi baştan sona bir kez oynat (o an oynayan numarayı keserek)
	public float PlayTest(string name)
	{
		if (Air.Contains(desired) || !clips.TryGetValue(name, out var clip)) return 0;
		oneShot = false;
		return PlayOneShot(name) ? clip.Duration : 0;
	}

	// CS2'de beslendikten sonra: yem yeme
	public float Feed()
	{
		if (oneShot || Air.Contains(desired)) return 0;
		return PlayRandom(["chick_feed01"]);
	}

	public void Land() => PlayOneShot("chick_land");

	public void Update(AnimState s)
	{
		CheckOneShotDeadline();
		SetDesired(s.Desired);
		if (current != null)
		{
			bool walkish = current.Clip.Name is "chick_walk" or "chick_start_walk";
			current.TimeScale = walkish ? Math.Clamp(s.SpeedScale, 0.35f, 1.2f) : IsBigJump(current) ? jumpTimeScale : 1;
		}

		// yön: model +Z'ye (kameraya) bakar; +90° sağ, -90° sol. Hafif kameraya dönük bırak (3/4).
		float target = s.Dir > 0 ? MathF.PI / 2 - FaceYaw : -MathF.PI / 2 + FaceYaw;
		float diff = target - yaw;
		while (diff > MathF.PI) diff -= MathF.PI * 2;
		while (diff < -MathF.PI) diff += MathF.PI * 2;
		yaw += diff * Math.Min(1, s.Dt * 5);

		mixer.Update(s.Dt, Matrix4x4.CreateRotationY(yaw));
		UpdateFallSpeed(s.Dt);
	}
}
