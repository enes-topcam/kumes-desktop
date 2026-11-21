// Kayıt: ortak kod buraya yazar, nereye gideceğine uygulama karar verir (masaüstü: kumes.log, mobil: logcat + dosya).
namespace Kumes;

public static class Log
{
	public static Action<string>? Sink;

	public static void Write(string msg)
	{
		try { Sink?.Invoke(msg); } catch { /* kayıt hatası uygulamayı durdurmasın */ }
	}
}
