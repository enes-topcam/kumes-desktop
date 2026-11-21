// Görünmez bir pencerede OpenGL bağlamı. Çizim ekran dışı yapılır (FBO), sonuç katmanlı pencereye kopyalanır.
// Aynı gizli pencere sağ tık menüsünün sahibi olarak da kullanılır (odak alabilen bir pencere gerekiyor).
using Silk.NET.OpenGL;
using static Kumes.Native.Win32;

namespace Kumes.Native;

sealed unsafe class GlContext : IDisposable
{
	public GL Gl { get; }
	public nint Hwnd { get; }
	readonly nint dc, ctx;

	public GlContext()
	{
		var opengl32 = LoadLibraryW("opengl32.dll");
		var hInst = GetModuleHandleW(null);
		var cls = System.Runtime.InteropServices.Marshal.StringToHGlobalUni("KumesGL");
		var wc = new WNDCLASSEXW
		{
			CbSize = (uint)sizeof(WNDCLASSEXW),
			LpfnWndProc = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW"),
			HInstance = hInst,
			LpszClassName = cls,
		};
		RegisterClassExW(&wc);
		Hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, "KumesGL", "Kümes", WS_POPUP, 0, 0, 1, 1, 0, 0, hInst, 0);
		dc = GetDC(Hwnd);

		var pfd = new PIXELFORMATDESCRIPTOR
		{
			NSize = (ushort)sizeof(PIXELFORMATDESCRIPTOR),
			NVersion = 1,
			DwFlags = 0x4 | 0x20 | 0x1, // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
			CColorBits = 32,
			CAlphaBits = 8,
			CDepthBits = 24,
			CStencilBits = 8,
		};
		int fmt = ChoosePixelFormat(dc, &pfd);
		if (fmt == 0 || !SetPixelFormat(dc, fmt, &pfd)) throw new InvalidOperationException("OpenGL piksel biçimi seçilemedi.");
		ctx = wglCreateContext(dc);
		if (ctx == 0 || !wglMakeCurrent(dc, ctx)) throw new InvalidOperationException("OpenGL bağlamı oluşturulamadı.");

		Gl = GL.GetApi(name =>
		{
			var p = wglGetProcAddress(name);
			if (p is 0 or 1 or 2 or 3 or -1) p = GetProcAddress(opengl32, name);
			return p;
		});
	}

	public void Dispose()
	{
		wglMakeCurrent(0, 0);
		wglDeleteContext(ctx);
		ReleaseDC(Hwnd, dc);
		DestroyWindow(Hwnd);
	}
}
