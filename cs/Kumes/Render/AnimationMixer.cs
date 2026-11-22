// three.js AnimationMixer'ın ihtiyacımız olan kısmı: birden çok klibi ağırlıklarıyla karıştırır,
// ağırlıkları zamanla açar/kapatır (crossfade), tek seferlik kliplerin bittiğini bildirir.
using System.Numerics;

namespace Kumes.Render;

sealed class Layer
{
	public required Clip Clip;
	public float Time;
	public float Weight;
	public bool Once;
	public float TimeScale = 1;
	public bool FinishedFired;
	internal bool Fading;
	internal float FadeFrom, FadeTo, FadeElapsed, FadeDuration;
}

sealed class AnimationMixer
{
	readonly Skeleton sk;
	readonly List<Layer> layers = new();
	readonly Vector3[] tmpT, tmpS, accT, accS;
	readonly Quaternion[] tmpR, accR;
	readonly float[] accW;
	readonly Matrix4x4[] world;
	public readonly Matrix4x4[] JointMatrices;
	readonly List<Layer> finished = new();

	public event Action<Layer>? Finished;

	public AnimationMixer(Skeleton skeleton)
	{
		sk = skeleton;
		int n = sk.Count;
		tmpT = new Vector3[n]; tmpS = new Vector3[n]; accT = new Vector3[n]; accS = new Vector3[n];
		tmpR = new Quaternion[n]; accR = new Quaternion[n];
		accW = new float[n];
		world = new Matrix4x4[n];
		JointMatrices = new Matrix4x4[sk.Joints.Length];
	}

	public IReadOnlyList<Layer> Layers => layers;
	public Matrix4x4[] World => world; // kemiklerin dünya matrisleri (ölçüm aracı için)

	// Klibi baştan başlat (three: action.reset().play()). Aynı klip zaten varsa yerine geçer.
	public Layer Play(Clip clip, bool once)
	{
		layers.RemoveAll(l => l.Clip == clip);
		var layer = new Layer { Clip = clip, Once = once };
		layers.Add(layer);
		return layer;
	}

	public static void FadeIn(Layer l, float duration) => Schedule(l, 0, 1, duration);
	public static void FadeOut(Layer l, float duration) => Schedule(l, l.Weight, 0, duration);

	static void Schedule(Layer l, float from, float to, float duration)
	{
		l.Weight = from;
		l.Fading = true;
		l.FadeFrom = from;
		l.FadeTo = to;
		l.FadeElapsed = 0;
		l.FadeDuration = Math.Max(duration, 1e-4f);
	}

	public void Update(float dt, Matrix4x4 rootTransform)
	{
		finished.Clear();
		for (int i = layers.Count - 1; i >= 0; i--)
		{
			var l = layers[i];
			if (l.Fading)
			{
				l.FadeElapsed += dt;
				float t = Math.Min(1, l.FadeElapsed / l.FadeDuration);
				l.Weight = l.FadeFrom + (l.FadeTo - l.FadeFrom) * t;
				if (t >= 1)
				{
					l.Fading = false;
					if (l.Weight <= 0) { layers.RemoveAt(i); continue; }
				}
			}
			float d = l.Clip.Duration;
			l.Time += dt * l.TimeScale;
			if (l.Once)
			{
				if (l.Time >= d)
				{
					l.Time = d; // clampWhenFinished
					if (!l.FinishedFired) { l.FinishedFired = true; finished.Add(l); }
				}
			}
			else if (d > 0)
			{
				l.Time %= d;
				if (l.Time < 0) l.Time += d;
			}
		}

		Blend();
		ComputeWorld(rootTransform);
		foreach (var l in finished) Finished?.Invoke(l);
	}

	void Blend()
	{
		int n = sk.Count;
		Array.Clear(accT); Array.Clear(accS); Array.Clear(accR); Array.Clear(accW);
		float total = 0;
		foreach (var l in layers)
		{
			if (l.Weight <= 0) continue;
			total += l.Weight;
			Array.Copy(sk.RestT, tmpT, n);
			Array.Copy(sk.RestR, tmpR, n);
			Array.Copy(sk.RestS, tmpS, n);
			Sample(l.Clip, l.Time);
			Accumulate(l.Weight);
		}
		// Ağırlıklar toplamı 1'den azsa kalan kısım dinlenme pozu (three ile aynı)
		if (total < 1)
		{
			Array.Copy(sk.RestT, tmpT, n);
			Array.Copy(sk.RestR, tmpR, n);
			Array.Copy(sk.RestS, tmpS, n);
			Accumulate(1 - total);
		}
		for (int i = 0; i < n; i++)
		{
			float w = accW[i];
			accT[i] /= w;
			accS[i] /= w;
			accR[i] = Quaternion.Normalize(accR[i]);
		}
		// evre şekli (kafa, kuyruk, bacak…): animasyonun üstüne kemik başına fark
		if (Shape is { } sh)
			for (int i = 0; i < n; i++)
			{
				accT[i] += sh.T[i];
				accR[i] = Quaternion.Normalize(accR[i] * sh.R[i]);
				accS[i] *= sh.S[i];
			}
	}

	public PoseShape? Shape;

	void Accumulate(float w)
	{
		for (int i = 0; i < sk.Count; i++)
		{
			accT[i] += tmpT[i] * w;
			accS[i] += tmpS[i] * w;
			var q = tmpR[i];
			if (Quaternion.Dot(accR[i], q) < 0) q = Quaternion.Negate(q);
			accR[i] += q * w;
			accW[i] += w;
		}
	}

	void Sample(Clip clip, float time)
	{
		foreach (var c in clip.Channels)
		{
			var times = c.Times;
			var v = c.Values;
			int k0, k1;
			float f;
			if (times.Length == 1 || time <= times[0]) { k0 = k1 = 0; f = 0; }
			else if (time >= times[^1]) { k0 = k1 = times.Length - 1; f = 0; }
			else
			{
				int idx = Array.BinarySearch(times, time);
				if (idx >= 0) { k0 = k1 = idx; f = 0; }
				else
				{
					k1 = ~idx;
					k0 = k1 - 1;
					f = (time - times[k0]) / (times[k1] - times[k0]);
				}
			}
			switch (c.Path)
			{
				case 0:
					tmpT[c.Node] = Vector3.Lerp(new(v[k0 * 3], v[k0 * 3 + 1], v[k0 * 3 + 2]), new(v[k1 * 3], v[k1 * 3 + 1], v[k1 * 3 + 2]), f);
					break;
				case 1:
					tmpR[c.Node] = Quaternion.Slerp(
						new(v[k0 * 4], v[k0 * 4 + 1], v[k0 * 4 + 2], v[k0 * 4 + 3]),
						new(v[k1 * 4], v[k1 * 4 + 1], v[k1 * 4 + 2], v[k1 * 4 + 3]), f);
					break;
				case 2:
					tmpS[c.Node] = Vector3.Lerp(new(v[k0 * 3], v[k0 * 3 + 1], v[k0 * 3 + 2]), new(v[k1 * 3], v[k1 * 3 + 1], v[k1 * 3 + 2]), f);
					break;
			}
		}
	}

	void ComputeWorld(Matrix4x4 rootTransform)
	{
		// evre şekli bacakları uzatıp kısaltınca ayaklar zeminde kalsın
		// ve toplam boy evreye göre ayarlansın (Scale)
		if (Shape is { } gs) rootTransform = Matrix4x4.CreateScale(gs.Scale) * Matrix4x4.CreateTranslation(0, gs.GroundLift, 0) * rootTransform;
		foreach (int i in sk.Order)
		{
			var local = Matrix4x4.CreateScale(accS[i]) * Matrix4x4.CreateFromQuaternion(accR[i]) * Matrix4x4.CreateTranslation(accT[i]);
			int p = sk.Parent[i];
			world[i] = p >= 0 ? local * world[p] : local * rootTransform;
		}
		for (int j = 0; j < sk.Joints.Length; j++)
			JointMatrices[j] = sk.InverseBind[j] * world[sk.Joints[j]];
	}
}
