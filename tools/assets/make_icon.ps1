# Makes assets\app-icon.svg, the program's icon, from OpenMoji's phoenix emoji (openmoji-phoenix.svg here):
# one colour, every outline of the emoji cut out as a gap, a thin gap where one coloured part lies over
# another, dark parts as holes, the empty margin cropped, scaled to a 256 x 256 box. The result is a
# single path the program draws at whatever size Windows asks for.
#
# Windows PowerShell 5.1 (it uses WPF, part of .NET Framework 4.8, for the geometry):
#   powershell -NoProfile -STA -ExecutionPolicy Bypass -File tools\assets\make_icon.ps1 tools\assets\openmoji-phoenix.svg assets\app-icon.svg
param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Target)
$ErrorActionPreference = 'Stop'

# Widths in the emoji's own units (its box is 72 wide). Cut: how wide the emoji's outlines are cut out
# (they are drawn 2 wide; 3 keeps the head and neck visible at 16 pixels). Gap: the gap a coloured part
# leaves around itself where it lies over another.
$CutWidth = 3.0
$Gap = 0.9 * 72 / 16

Add-Type -AssemblyName PresentationCore, WindowsBase, System.Xml
Add-Type -ReferencedAssemblies PresentationCore, WindowsBase, System.Xml, System.Xaml -TypeDefinition @'
using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Xml;

public static class IconMaker
{
    static string Inherited(XmlNode n, string name)
    {
        for (XmlNode x = n; x != null && x.Attributes != null; x = x.ParentNode)
            if (x.Attributes[name] != null) return x.Attributes[name].Value;
        return null;
    }

    static bool Dark(string colour)
    {
        if (string.IsNullOrEmpty(colour) || colour == "none") return false;
        Color c = (Color)ColorConverter.ConvertFromString(colour.Length == 4 && colour[0] == '#'
            ? "#" + colour[1] + colour[1] + colour[2] + colour[2] + colour[3] + colour[3] : colour);
        return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0 < 0.25;
    }

    // Every part in drawing order: a light part cuts a gap into what lies under it and fills itself;
    // a dark part (the emoji's outlines) is cut out.
    public static Geometry Glyph(string svgFile, double cutWidth, double gap)
    {
        XmlDocument doc = new XmlDocument();
        doc.Load(svgFile);
        Geometry result = new PathGeometry();
        foreach (XmlNode p in doc.GetElementsByTagName("path"))
        {
            Geometry g = Geometry.Parse((Inherited(p, "fill-rule") == "evenodd" ? "F0 " : "F1 ") + p.Attributes["d"].Value);
            string stroke = Inherited(p, "stroke"), fill = Inherited(p, "fill");
            bool dark;
            if (stroke != null && stroke != "none" && (fill == "none"))
            {
                dark = Dark(stroke);
                string w = Inherited(p, "stroke-width");
                Pen line = new Pen(Brushes.Black, dark ? cutWidth : double.Parse(w ?? "1", CultureInfo.InvariantCulture));
                line.StartLineCap = line.EndLineCap = PenLineCap.Round;
                line.LineJoin = PenLineJoin.Round;
                g = g.GetWidenedPathGeometry(line);
            }
            else dark = Dark(fill ?? "#000");
            if (dark) { result = Geometry.Combine(result, g, GeometryCombineMode.Exclude, null); continue; }
            Pen ring = new Pen(Brushes.Black, gap * 2);
            ring.LineJoin = PenLineJoin.Round;
            result = Geometry.Combine(result, g.GetWidenedPathGeometry(ring), GeometryCombineMode.Exclude, null);
            result = Geometry.Combine(result, g, GeometryCombineMode.Union, null);
        }
        return result;
    }

    static string N(double v) { return Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture); }

    // The glyph cropped to a square around it and scaled to 0..256, as absolute M, L, C and Z commands.
    public static string PathData(Geometry glyph, out string fillRule)
    {
        Rect b = glyph.Bounds;
        double side = Math.Max(b.Width, b.Height), s = 256 / side;
        double ox = b.X - (side - b.Width) / 2, oy = b.Y - (side - b.Height) / 2;
        Func<Point, string> P = pt => N((pt.X - ox) * s) + " " + N((pt.Y - oy) * s);
        PathGeometry pg = PathGeometry.CreateFromGeometry(glyph);
        fillRule = pg.FillRule == FillRule.EvenOdd ? "evenodd" : "nonzero";
        StringBuilder d = new StringBuilder();
        foreach (PathFigure f in pg.Figures)
        {
            d.Append('M').Append(P(f.StartPoint));
            Point last = f.StartPoint;
            foreach (PathSegment seg in f.Segments)
            {
                if (seg is LineSegment) { last = ((LineSegment)seg).Point; d.Append('L').Append(P(last)); }
                else if (seg is PolyLineSegment) foreach (Point pt in ((PolyLineSegment)seg).Points) { last = pt; d.Append('L').Append(P(pt)); }
                else if (seg is BezierSegment)
                {
                    BezierSegment z = (BezierSegment)seg;
                    d.Append('C').Append(P(z.Point1)).Append(' ').Append(P(z.Point2)).Append(' ').Append(P(z.Point3));
                    last = z.Point3;
                }
                else if (seg is PolyBezierSegment)
                {
                    PointCollection pts = ((PolyBezierSegment)seg).Points;
                    for (int i = 0; i + 2 < pts.Count; i += 3)
                    {
                        d.Append('C').Append(P(pts[i])).Append(' ').Append(P(pts[i + 1])).Append(' ').Append(P(pts[i + 2]));
                        last = pts[i + 2];
                    }
                }
                else if (seg is QuadraticBezierSegment || seg is PolyQuadraticBezierSegment)
                {
                    PointCollection pts = seg is QuadraticBezierSegment
                        ? new PointCollection(new[] { ((QuadraticBezierSegment)seg).Point1, ((QuadraticBezierSegment)seg).Point2 })
                        : ((PolyQuadraticBezierSegment)seg).Points;
                    for (int i = 0; i + 1 < pts.Count; i += 2)
                    {
                        Point q = pts[i], end = pts[i + 1];
                        Point c1 = new Point(last.X + 2.0 / 3 * (q.X - last.X), last.Y + 2.0 / 3 * (q.Y - last.Y));
                        Point c2 = new Point(end.X + 2.0 / 3 * (q.X - end.X), end.Y + 2.0 / 3 * (q.Y - end.Y));
                        d.Append('C').Append(P(c1)).Append(' ').Append(P(c2)).Append(' ').Append(P(end));
                        last = end;
                    }
                }
                else throw new InvalidOperationException("unexpected segment " + seg.GetType().Name);
            }
            if (f.IsClosed) d.Append('Z');
        }
        return d.ToString();
    }
}
'@

$glyph = [IconMaker]::Glyph((Resolve-Path $Source).Path, $CutWidth, $Gap)
$rule = ''
$d = [IconMaker]::PathData($glyph, [ref]$rule)
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">
<!-- The program's icon. Adapted from the phoenix emoji (U+1F426 U+200D U+1F525) by Vanessa Stoessinger for
     OpenMoji, the open-source emoji and icon project (https://openmoji.org), licensed under CC BY-SA 4.0
     (https://creativecommons.org/licenses/by-sa/4.0/). Changes: one colour, the outlines cut out, the margin
     cropped. This file is licensed under CC BY-SA 4.0 as well. Made by tools/assets/make_icon.ps1. -->
<path fill-rule="$rule" d="$d"/>
</svg>
"@
[System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Target), $svg.Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding($false)))
"wrote {0}: {1} characters of path data, fill rule {2}" -f $Target, $d.Length, $rule
