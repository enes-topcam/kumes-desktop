// Kullanılan Win32 / WGL fonksiyonları.
using System.Runtime.InteropServices;

namespace Kumes.Native;

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct SIZE { public int Cx, Cy; }

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
struct MSG
{
	public nint Hwnd;
	public uint Message;
	public nuint WParam;
	public nint LParam;
	public uint Time;
	public POINT Pt;
	public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
struct WNDCLASSEXW
{
	public uint CbSize;
	public uint Style;
	public nint LpfnWndProc;
	public int CbClsExtra;
	public int CbWndExtra;
	public nint HInstance;
	public nint HIcon;
	public nint HCursor;
	public nint HbrBackground;
	public nint LpszMenuName;
	public nint LpszClassName;
	public nint HIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct BLENDFUNCTION
{
	public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
}

[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
	public uint BiSize;
	public int BiWidth, BiHeight;
	public ushort BiPlanes, BiBitCount;
	public uint BiCompression, BiSizeImage;
	public int BiXPelsPerMeter, BiYPelsPerMeter;
	public uint BiClrUsed, BiClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
struct TRACKMOUSEEVENT
{
	public uint CbSize, DwFlags;
	public nint HwndTrack;
	public uint DwHoverTime;
}

[StructLayout(LayoutKind.Sequential)]
struct PIXELFORMATDESCRIPTOR
{
	public ushort NSize, NVersion;
	public uint DwFlags;
	public byte IPixelType, CColorBits, CRedBits, CRedShift, CGreenBits, CGreenShift, CBlueBits, CBlueShift,
		CAlphaBits, CAlphaShift, CAccumBits, CAccumRedBits, CAccumGreenBits, CAccumBlueBits, CAccumAlphaBits,
		CDepthBits, CStencilBits, CAuxBuffers, ILayerType, BReserved;
	public uint DwLayerMask, DwVisibleMask, DwDamageMask;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct NOTIFYICONDATAW
{
	public uint CbSize;
	public nint HWnd;
	public uint UID;
	public uint UFlags;
	public uint UCallbackMessage;
	public nint HIcon;
	public fixed char SzTip[128];
	public uint DwState;
	public uint DwStateMask;
	public fixed char SzInfo[256];
	public uint UTimeoutOrVersion;
	public fixed char SzInfoTitle[64];
	public uint DwInfoFlags;
	public Guid GuidItem;
	public nint HBalloonIcon;
}

[StructLayout(LayoutKind.Sequential)]
struct ICONINFO
{
	public int FIcon;
	public uint XHotspot, YHotspot;
	public nint HbmMask, HbmColor;
}

[StructLayout(LayoutKind.Sequential)]
unsafe struct PAINTSTRUCT
{
	public nint Hdc;
	public int FErase;
	public RECT RcPaint;
	public int FRestore, FIncUpdate;
	public fixed byte RgbReserved[32];
}

static unsafe class Win32
{
	public const uint WS_OVERLAPPED = 0, WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
	public const uint WS_EX_TRANSPARENT = 0x20;
	public const uint WM_PAINT = 0xF, WM_RBUTTONDOWN = 0x204, WM_CAPTURECHANGED = 0x215, WM_APP = 0x8000, WM_USER = 0x400,
		WM_ERASEBKGND = 0x14, WM_CONTEXTMENU = 0x7B, WM_DISPLAYCHANGE = 0x7E;
	public const int SW_SHOW = 5, SW_RESTORE = 9;
	public const uint MF_CHECKED = 8;
	public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
	public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
	public const uint NIIF_INFO = 1, NIIF_USER = 4, NIIF_LARGE_ICON = 0x20;
	public const uint NIN_BALLOONUSERCLICK = WM_USER + 5;
	public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
	public const uint SRCCOPY = 0x00CC0020;

	public const uint WS_POPUP = 0x80000000;
	public const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
	public const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_QUIT = 0x12, WM_NULL = 0, WM_MOUSEACTIVATE = 0x21,
		WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSELEAVE = 0x2A3;
	public const int MA_NOACTIVATE = 3;
	public const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0;
	public const uint PM_REMOVE = 1;
	public const uint ULW_ALPHA = 2;
	public const byte AC_SRC_ALPHA = 1;
	public const uint SPI_GETWORKAREA = 0x30;
	public const uint TME_LEAVE = 2;
	public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
	public static readonly nint HWND_TOPMOST = -1;
	public const uint MF_STRING = 0, MF_GRAYED = 1, MF_SEPARATOR = 0x800;
	public const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 2, TPM_BOTTOMALIGN = 0x20;
	public const uint QS_ALLINPUT = 0x4FF, MWMO_INPUTAVAILABLE = 4, INFINITE = 0xFFFFFFFF, WAIT_OBJECT_0 = 0;
	public const uint CREATE_WAITABLE_TIMER_HIGH_RESOLUTION = 2, TIMER_ALL_ACCESS = 0x1F0003;
	public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
	public const int IDC_ARROW = 32512;

	const string U = "user32.dll", G = "gdi32.dll", K = "kernel32.dll", GL = "opengl32.dll";

	[DllImport(U)] public static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
	[DllImport(U, CharSet = CharSet.Unicode)]
	public static extern nint CreateWindowExW(uint exStyle, string className, string title, uint style,
		int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
	[DllImport(U)] public static extern nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
	[DllImport(U)] public static extern bool ShowWindow(nint hwnd, int cmd);
	[DllImport(U)] public static extern bool DestroyWindow(nint hwnd);
	[DllImport(U)] public static extern bool PeekMessageW(out MSG msg, nint hwnd, uint min, uint max, uint remove);
	[DllImport(U)] public static extern bool TranslateMessage(in MSG msg);
	[DllImport(U)] public static extern nint DispatchMessageW(in MSG msg);
	[DllImport(U)] public static extern void PostQuitMessage(int code);
	[DllImport(U)] public static extern bool PostMessageW(nint hwnd, uint msg, nuint w, nint l);
	[DllImport(U)] public static extern nint LoadCursorW(nint inst, nint name);
	[DllImport(U)] public static extern nint GetDC(nint hwnd);
	[DllImport(U)] public static extern int ReleaseDC(nint hwnd, nint dc);
	[DllImport(G)] public static extern int GetDeviceCaps(nint dc, int index);
	public const int VREFRESH = 116;
	[DllImport(U)] public static extern bool UpdateLayeredWindow(nint hwnd, nint hdcDst, POINT* pptDst, SIZE* psize,
		nint hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* pblend, uint flags);
	[DllImport(U)] public static extern bool SystemParametersInfoW(uint action, uint param, void* pv, uint winIni);
	[DllImport(U)] public static extern bool SetProcessDpiAwarenessContext(nint ctx);
	[DllImport(U)] public static extern uint GetDpiForSystem();
	[DllImport(U)] public static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
	[DllImport(U)] public static extern bool TrackMouseEvent(TRACKMOUSEEVENT* tme);
	[DllImport(U)] public static extern bool GetCursorPos(out POINT p);
	[DllImport(U)] public static extern nint CreatePopupMenu();
	[DllImport(U, CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(nint menu, uint flags, nuint id, string? text);
	[DllImport(U)] public static extern int TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint tpm);
	[DllImport(U)] public static extern bool DestroyMenu(nint menu);
	[DllImport(U)] public static extern bool SetForegroundWindow(nint hwnd);
	[DllImport(U)] public static extern uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint ms, uint wakeMask, uint flags);

	public const int GWL_EXSTYLE = -20, DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
	public const uint WS_EX_APPWINDOW = 0x40000;
	[DllImport(U)] public static extern bool EnumWindows(delegate* unmanaged<nint, nint, int> fn, nint lParam);
	[DllImport(U)] public static extern nint GetWindowLongPtrW(nint hwnd, int index);
	[DllImport(U)] public static extern bool IsIconic(nint hwnd);
	[DllImport(U)] public static extern bool IsWindow(nint hwnd);
	[DllImport(U)] public static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
	[DllImport(U, CharSet = CharSet.Unicode)] public static extern int GetClassNameW(nint hwnd, char* buf, int max);
	[DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(nint hwnd, int attr, void* value, int size);

	public const uint WM_KEYDOWN = 0x100, WM_SETFOCUS = 0x7, WM_KILLFOCUS = 0x8;
	public const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_ESCAPE = 0x1B, VK_SPACE = 0x20,
		VK_UP = 0x26, VK_DOWN = 0x28, IDC_NO = 32648;
	public const int DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
	[DllImport(U)] public static extern short GetKeyState(int vk);
	[DllImport(U)] public static extern bool IsZoomed(nint hwnd);
	[DllImport(U)] public static extern bool ScreenToClient(nint hwnd, ref POINT p);

	public const uint WM_SETCURSOR = 0x20, WM_LBUTTONDBLCLK = 0x203, WM_SETICON = 0x80;
	[DllImport(U)] public static extern nint SendMessageW(nint hwnd, uint msg, nuint w, nint l);
	public const int IDC_HAND = 32649;
	[DllImport(U)] public static extern nint SetCursor(nint cursor);

	public const uint WM_TIMER = 0x113, WM_ENTERSIZEMOVE = 0x231, WM_EXITSIZEMOVE = 0x232;
	[DllImport(U)] public static extern nuint SetTimer(nint hwnd, nuint id, uint ms, nint fn);
	[DllImport(U)] public static extern bool KillTimer(nint hwnd, nuint id);
	[DllImport(U)] public static extern nint SetCapture(nint hwnd);
	[DllImport(U)] public static extern bool ReleaseCapture();
	[DllImport(U)] public static extern nint GetCapture();
	[DllImport(U, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
	[DllImport(U, CharSet = CharSet.Unicode)] public static extern nint FindWindowW(string? cls, string? title);
	[DllImport(U)] public static extern nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
	[DllImport(U)] public static extern bool EndPaint(nint hwnd, PAINTSTRUCT* ps);
	[DllImport(U)] public static extern bool InvalidateRect(nint hwnd, RECT* rect, bool erase);
	[DllImport(U)] public static extern bool AdjustWindowRectEx(RECT* rect, uint style, bool menu, uint exStyle);
	[DllImport(U)] public static extern nint CreateIconIndirect(ICONINFO* info);
	[DllImport(U)] public static extern bool DestroyIcon(nint icon);
	[DllImport(U)] public static extern bool IsWindowVisible(nint hwnd);
	[DllImport(U)] public static extern bool BringWindowToTop(nint hwnd);
	[DllImport("shell32.dll")] public static extern bool Shell_NotifyIconW(uint msg, NOTIFYICONDATAW* data);
	[DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, void* value, int size);
	[DllImport("ole32.dll")] public static extern int CoInitializeEx(nint reserved, uint coInit);

	[DllImport(G)] public static extern bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
	[DllImport(G)] public static extern nint CreateBitmap(int w, int h, uint planes, uint bpp, void* bits);
	[DllImport(G)] public static extern nint CreateCompatibleDC(nint dc);
	[DllImport(G)] public static extern bool DeleteDC(nint dc);
	[DllImport(G)] public static extern nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bmi, uint usage, void** bits, nint section, uint offset);
	[DllImport(G)] public static extern nint SelectObject(nint dc, nint obj);
	[DllImport(G)] public static extern bool DeleteObject(nint obj);
	[DllImport(G)] public static extern int ChoosePixelFormat(nint dc, PIXELFORMATDESCRIPTOR* pfd);
	[DllImport(G)] public static extern bool SetPixelFormat(nint dc, int format, PIXELFORMATDESCRIPTOR* pfd);

	[DllImport(K, CharSet = CharSet.Unicode)] public static extern nint GetModuleHandleW(string? name);
	[DllImport(K, CharSet = CharSet.Unicode)] public static extern nint LoadLibraryW(string name);
	[DllImport(K, CharSet = CharSet.Ansi)] public static extern nint GetProcAddress(nint module, string name);
	[DllImport(K, CharSet = CharSet.Unicode)] public static extern nint CreateWaitableTimerExW(nint attrs, string? name, uint flags, uint access);
	[DllImport(K)] public static extern bool SetWaitableTimer(nint timer, long* due, int periodMs, nint fn, nint arg, bool resume);
	[DllImport(K)] public static extern bool CloseHandle(nint h);

	[DllImport(K)] public static extern nint GetCurrentProcess();
	[DllImport(K)] public static extern bool K32EmptyWorkingSet(nint process);

	[DllImport(GL)] public static extern nint wglCreateContext(nint dc);
	[DllImport(GL)] public static extern bool wglMakeCurrent(nint dc, nint ctx);
	[DllImport(GL)] public static extern bool wglDeleteContext(nint ctx);
	[DllImport(GL, CharSet = CharSet.Ansi)] public static extern nint wglGetProcAddress(string name);

	public static RECT GetWorkArea()
	{
		RECT r;
		SystemParametersInfoW(SPI_GETWORKAREA, 0, &r, 0);
		return r;
	}

	public static short LoWord(nint v) => (short)(v & 0xFFFF);
	public static short HiWord(nint v) => (short)((v >> 16) & 0xFFFF);

	// ---------- metin kutuları (giriş penceresindeki kullanıcı adı / şifre / kod) ----------
	public const uint WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPCHILDREN = 0x2000000;
	public const uint ES_CENTER = 0x1, ES_UPPERCASE = 0x8, ES_PASSWORD = 0x20, ES_AUTOHSCROLL = 0x80;
	public const uint WM_COMMAND = 0x111, WM_SETFONT = 0x30, WM_CTLCOLOREDIT = 0x133, EM_SETSEL = 0xB1, EM_LIMITTEXT = 0xC5, EM_SETPASSWORDCHAR = 0xCC;
	public const int EN_SETFOCUS = 0x100, EN_KILLFOCUS = 0x200, VK_CONTROL = 0x11;
	[DllImport(G, CharSet = CharSet.Unicode)]
	public static extern nint CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike,
		uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
	[DllImport(G)] public static extern nint CreateSolidBrush(uint colorRef);
	[DllImport(G)] public static extern uint SetTextColor(nint dc, uint colorRef);
	[DllImport(G)] public static extern uint SetBkColor(nint dc, uint colorRef);
	[DllImport(U)] public static extern nint SetFocus(nint hwnd);
	[DllImport(U)] public static extern nint GetFocus();
	[DllImport(U)] public static extern int GetWindowTextLengthW(nint hwnd);
	[DllImport(U, CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(nint hwnd, char* buf, int max);
	[DllImport(U, CharSet = CharSet.Unicode)] public static extern bool SetWindowTextW(nint hwnd, string text);
	[DllImport(U)] public static extern bool MoveWindow(nint hwnd, int x, int y, int w, int h, bool repaint);

	public static string GetText(nint hwnd)
	{
		int n = GetWindowTextLengthW(hwnd);
		if (n <= 0) return "";
		var buf = new char[n + 1];
		fixed (char* p = buf) n = GetWindowTextW(hwnd, p, buf.Length);
		var s = new string(buf, 0, n);
		Array.Clear(buf); // şifre bellekte gereksiz kopya bırakmasın
		return s;
	}

	// 0xRRGGBBAA -> GDI COLORREF (0x00BBGGRR)
	public static uint ColorRef(uint rgba) => ((rgba >> 24) & 255) | (((rgba >> 16) & 255) << 8) | (((rgba >> 8) & 255) << 16);
}
