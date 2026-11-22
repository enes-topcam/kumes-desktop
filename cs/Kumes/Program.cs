// Kümes (C#): CS2'deki evcil tavuğu görev çubuğunun üstünde yaşatır.
using Kumes;
using Kumes.Native;
using static Kumes.Native.Win32;

SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
Directory.CreateDirectory(Store.DataDir);
Log.Sink = FileLog.Write; // ortak koddaki kayıtlar kumes.log'a
CoInitializeEx(0, 2); // WIC (2D çizim) için COM

if (args.Length >= 2 && args[0] == "--ledges")
{
	// geliştirme: tavuğun konabileceği kenarlar (pencere başlığı yazılmaz, sadece sınıf adı ve konum)
	var wa = GetWorkArea();
	var lines = Ledges.Find(wa, wa.Top + 3, 60).Select(l =>
	{
		var cls = new string('\0', 0);
		unsafe { char* b = stackalloc char[64]; int n = GetClassNameW(l.Hwnd, b, 64); cls = new string(b, 0, n); }
		return $"{cls,-36} x {l.Left,5:F0}..{l.Right,5:F0}  yükseklik {l.Alt,5:F0}";
	});
	File.WriteAllLines(args[1], lines);
	return 0;
}
if (args.Length >= 5 && args[0] == "--sequence")
{
	Snapshot.Sequence(args);
	return 0;
}
if (args.Length >= 3 && args[0] == "--clip-report")
{
	Snapshot.ClipReport(args[1], args[2]);
	return 0;
}
if (args.Length >= 2 && args[0] == "--download-test")
{
	AssetManager.ExtractFromLocalVpk(args[1]); // geliştirme: CS2'siz indirme akışı, dosyalar yerel CS2'den
	return 0;
}
if (args.Length >= 1 && args[0] == "--login-test")
{
	LoginTest.Run(); // geliştirme: giriş penceresi, Steam'e bağlanmadan
	return 0;
}
if (args.Length >= 5 && args[0] == "--snapshot")
{
	Snapshot.Run(args);
	return 0;
}

// Tek kopya: ikinci kez açılırsa çalışan Kümes'e "kendini göster" de ve çık
using var mutex = new Mutex(true, @"Local\KumesCS", out bool first);
if (!first)
{
	var h = FindWindowW(MessageWindow.ClassName, null);
	if (h != 0) PostMessageW(h, MessageWindow.WM_SHOW_REQUEST, 0, 0);
	return 0;
}

Log.Write("---- Kümes (C#) başladı ----");
try
{
	new App(startedAtLogin: args.Contains("--autostart"), filming: args.Contains("--cekim")).Run();
	return 0;
}
catch (Exception e)
{
	Log.Write($"HATA: {e}");
	return 1;
}
