// Kümes çizgi simgeleri (mobille aynı set; 24x24 ızgara, 1.75 çizgi, yuvarlak uçlar) ve SVG yol verisini
// Direct2D geometrisine çeviren küçük çözücü (M L H V C S Q T A Z, büyük/küçük harf).
using System.Globalization;
using System.Numerics;
using Vortice.Direct2D1;

namespace Kumes.Ui;

static class SvgIcons
{
	public const string Home = "M15 21v-8a1 1 0 0 0-1-1h-4a1 1 0 0 0-1 1v8 M3 10a2 2 0 0 1 .709-1.528l7-5.999a2 2 0 0 1 2.582 0l7 5.999A2 2 0 0 1 21 10v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z";
	public const string Layers = "M12.83 2.18a2 2 0 0 0-1.66 0L2.6 6.08a1 1 0 0 0 0 1.83l8.58 3.91a2 2 0 0 0 1.66 0l8.58-3.9a1 1 0 0 0 0-1.83z M2 12a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 12 M2 17a1 1 0 0 0 .58.91l8.6 3.91a2 2 0 0 0 1.65 0l8.58-3.9A1 1 0 0 0 22 17";
	public const string Gear = "M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2z M12 9a3 3 0 1 0 0 6 3 3 0 1 0 0-6z";
	public const string Refresh = "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5";
	public const string Calendar = "M8 2v4 M16 2v4 M5 4h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z M3 10h18";
	public const string CheckCircle = "M12 2a10 10 0 1 0 0 20 10 10 0 1 0 0-20z M9 12l2 2 4-4";
	public const string AlertCircle = "M12 2a10 10 0 1 0 0 20 10 10 0 1 0 0-20z M12 8v4 M12 16h.01";
	public const string Info = "M12 2a10 10 0 1 0 0 20 10 10 0 1 0 0-20z M12 16v-4 M12 8h.01";
	public const string Plus = "M5 12h14 M12 5v14";
	public const string ChevronRight = "M9 18l6-6-6-6";
	public const string Back = "M15 18l-6-6 6-6";
	public const string Logout = "M16 17l5-5-5-5 M21 12H9 M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4";
	public const string Folder = "M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2z";
	public const string File = "M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7z M14 2v4a2 2 0 0 0 2 2h4 M16 13H8 M16 17H8 M10 9H8";
	public const string Box = "M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z M3.3 7l8.7 5 8.7-5 M12 22V12";
	public const string Power = "M12 2v10 M18.4 6.6a9 9 0 1 1-12.77.04";
	public const string Globe = "M12 2a10 10 0 1 0 0 20 10 10 0 1 0 0-20z M2 12h20 M12 2a15.3 15.3 0 0 1 4 10 15.3 15.3 0 0 1-4 10 15.3 15.3 0 0 1-4-10 15.3 15.3 0 0 1 4-10z";
	public const string External = "M15 3h6v6 M10 14 21 3 M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6";
	public const string Egg = "M12 2.5c-3.6 0-7 5.6-7 10.5a7 7 0 0 0 14 0c0-4.9-3.4-10.5-7-10.5z";
	public const string Bowl = "M3 11h18a9 9 0 0 1-18 0z M8 7.5h.01 M12 6h.01 M16 7.5h.01";
	public const string Chicken = "M17.4 9.8C18.6 11.1 19.2 12.5 19.2 14 19.2 17.4 16.2 20 12.3 20 8 20 4.8 17.2 4.8 13.2V6.5L8.3 10.2C9.4 8.2 10.6 6.7 12.2 6 13.9 5.2 16 5.8 16.8 7.4L19.3 8.6Z M12.5 5.9c-.4-1.1.3-2.1 1.3-2 .4-.9 1.7-.9 2 .1.9 0 1.5.9 1.1 1.7 M15.3 8.2h.01 M8.6 12.6c2.3-.3 4.6.6 5.6 2.6-2.4.9-4.6.1-5.6-2.6z";

	static readonly Dictionary<string, ID2D1PathGeometry> cache = new();
	static ID2D1StrokeStyle? round;

	public static ID2D1StrokeStyle Round => round ??= Gfx2D.Shared.D2D.CreateStrokeStyle(new StrokeStyleProperties
	{
		LineJoin = LineJoin.Round, StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round,
	});

	// Dolgu simgesi (mobildeki IconSet): yolun başına Fill eklenir, Surface2D.Icon bunu görünce çizgi yerine doldurur
	public const string Fill = "F";
	public static bool IsFill(string d) => d.StartsWith(Fill, StringComparison.Ordinal);

	public static ID2D1PathGeometry Geometry(string d)
	{
		if (cache.TryGetValue(d, out var g)) return g;
		bool fill = IsFill(d);
		g = Gfx2D.Shared.D2D.CreatePathGeometry();
		using (var sink = g.Open())
		{
			if (fill) sink.SetFillMode(FillMode.Alternate); // even-odd: iç boşluklar delik kalsın
			new Parser(fill ? d[Fill.Length..] : d, sink, fill).Run();
			sink.Close();
		}
		cache[d] = g;
		return g;
	}

	sealed class Parser(string d, ID2D1GeometrySink sink, bool filled = false)
	{
		int i;
		bool open;
		Vector2 cur, start, lastCtrl;
		char lastCmd;

		void SkipSep() { while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; }
		bool HasNumber() { SkipSep(); return i < d.Length && (char.IsDigit(d[i]) || d[i] is '-' or '+' or '.'); }

		float Num()
		{
			SkipSep();
			int s = i;
			if (i < d.Length && d[i] is '-' or '+') i++;
			bool dot = false, exp = false;
			while (i < d.Length)
			{
				char c = d[i];
				if (char.IsDigit(c)) { i++; continue; }
				if (c == '.' && !dot && !exp) { dot = true; i++; continue; }
				if ((c == 'e' || c == 'E') && !exp) { exp = true; i++; if (i < d.Length && d[i] is '-' or '+') i++; continue; }
				break;
			}
			return float.Parse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture);
		}

		bool Flag() { SkipSep(); return d[i++] == '1'; }

		void MoveTo(Vector2 p)
		{
			if (open) sink.EndFigure(FigureEnd.Open);
			sink.BeginFigure(p, filled ? FigureBegin.Filled : FigureBegin.Hollow);
			open = true;
			cur = start = p;
		}

		void LineTo(Vector2 p) { sink.AddLine(p); cur = p; }

		public void Run()
		{
			char cmd = ' ';
			while (true)
			{
				SkipSep();
				if (i >= d.Length) break;
				if (char.IsLetter(d[i])) cmd = d[i++];
				else if (cmd == 'M') cmd = 'L';
				else if (cmd == 'm') cmd = 'l';
				bool rel = char.IsLower(cmd);
				Vector2 o = rel ? cur : Vector2.Zero;
				switch (char.ToUpperInvariant(cmd))
				{
					case 'M': MoveTo(o + new Vector2(Num(), Num())); break;
					case 'L': LineTo(o + new Vector2(Num(), Num())); break;
					case 'H': LineTo(new Vector2((rel ? cur.X : 0) + Num(), cur.Y)); break;
					case 'V': LineTo(new Vector2(cur.X, (rel ? cur.Y : 0) + Num())); break;
					case 'C':
					{
						var p1 = o + new Vector2(Num(), Num()); var p2 = o + new Vector2(Num(), Num()); var p3 = o + new Vector2(Num(), Num());
						sink.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
						lastCtrl = p2; cur = p3; break;
					}
					case 'S':
					{
						var p1 = char.ToUpperInvariant(lastCmd) is 'C' or 'S' ? 2 * cur - lastCtrl : cur;
						var p2 = o + new Vector2(Num(), Num()); var p3 = o + new Vector2(Num(), Num());
						sink.AddBezier(new BezierSegment { Point1 = p1, Point2 = p2, Point3 = p3 });
						lastCtrl = p2; cur = p3; break;
					}
					case 'Q':
					{
						var p1 = o + new Vector2(Num(), Num()); var p2 = o + new Vector2(Num(), Num());
						sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = p1, Point2 = p2 });
						lastCtrl = p1; cur = p2; break;
					}
					case 'T':
					{
						var p1 = char.ToUpperInvariant(lastCmd) is 'Q' or 'T' ? 2 * cur - lastCtrl : cur;
						var p2 = o + new Vector2(Num(), Num());
						sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = p1, Point2 = p2 });
						lastCtrl = p1; cur = p2; break;
					}
					case 'A':
					{
						float rx = Num(), ry = Num(), rot = Num();
						bool large = Flag(), sweep = Flag();
						var p = o + new Vector2(Num(), Num());
						if (p != cur)
							sink.AddArc(new ArcSegment
							{
								Point = p, Size = new Vortice.Mathematics.Size(rx, ry), RotationAngle = rot,
								SweepDirection = sweep ? SweepDirection.Clockwise : SweepDirection.CounterClockwise,
								ArcSize = large ? ArcSize.Large : ArcSize.Small,
							});
						cur = p; break;
					}
					case 'Z':
						if (open) { sink.EndFigure(FigureEnd.Closed); open = false; }
						cur = start;
						break;
					default: i++; break;
				}
				lastCmd = cmd;
				if (char.ToUpperInvariant(cmd) == 'Z') continue;
				if (!HasNumber() && i < d.Length && !char.IsLetter(d[i])) i++;
			}
			if (open) sink.EndFigure(FigureEnd.Open);
		}
	}
}
