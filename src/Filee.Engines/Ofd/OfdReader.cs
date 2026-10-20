// OFD (GB/T 33190-2016, the Chinese standard for fixed-layout documents: e-invoices, contracts, official
// documents) → HDocument with no external engine, so OFD → DOCX / HWPX / TXT works out of the box.
//
// An OFD file is a ZIP package of XML pages. Like a PDF it stores positioned text, so boxes are grouped
// into lines and lines into paragraphs (the same idea as the PDF reader, smaller scale): a line is a
// paragraph unless consecutive lines clearly continue one, and every fragment keeps its own size and
// colour. Pictures (the seals of invoices, often JBIG2 or JPEG-2000) are extracted and kept in reading
// order; ImageMagick transcodes formats Word cannot embed. Vector drawings and tables are not detected
// — their text comes out in reading order, as with scanned PDFs.

using System.IO.Compression;
using System.Xml.Linq;
using Filee.Engines.Hwp.Hwpx;
using ImageMagick;

namespace Filee.Engines.Ofd;

/// <summary>Reads an OFD file into the document model (DOCX / HWPX writers, plain text).</summary>
internal static class OfdReader
{
    /// <summary>Millimetres → HWPUNIT (1/7200 inch).</summary>
    private const double MmToUnit = 7200.0 / 25.4;

    /// <summary>Millimetres → font size in 1/100 pt.</summary>
    private const double MmToCentiPt = 100.0 * 72.0 / 25.4;

    /// <summary>Smallest margin the page allows, so text never touches the paper edge (1 mm).</summary>
    private const double MinMarginMm = 1.0;

    /// <summary>Image extensions the writers embed as they are; anything else is transcoded to PNG.</summary>
    private static readonly HashSet<string> DirectImages = ["png", "jpg", "jpeg", "gif", "bmp", "webp"];

    /// <summary>Set by <see cref="Read"/>: the package pictures are unpacked from.</summary>
    private static string? _zipPath;

    public static HDocument Read(string path, string mediaFolder)
    {
        _zipPath = path;
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries.ToDictionary(e => Norm(e.FullName), e => e, StringComparer.OrdinalIgnoreCase);

        var root = LoadXml(entries, "OFD.xml") ?? throw new InvalidDataException("This OFD file has no OFD.xml.");
        var docRoot = root.Descendants().First(d => d.Name.LocalName == "DocBody")
                         .Descendants().First(d => d.Name.LocalName == "DocRoot").Value.Trim();
        var docDir = DirName(docRoot);
        var document = LoadXml(entries, docRoot) ?? throw new InvalidDataException("This OFD file has no document root.");

        // Default page size from Document.xml/CommonData/PageArea (WPS writes it here, pages have no box of
        // their own); a page's own PhysicalBox, when present, overrides it.
        double defaultW = 210, defaultH = 297;
        var commonData = document.Descendants().FirstOrDefault(d => d.Name.LocalName == "CommonData");
        if (PageBox(commonData?.Descendants().FirstOrDefault(d => d.Name.LocalName == "PhysicalBox")?.Value) is { } box)
        {
            defaultW = box.W;
            defaultH = box.H;
        }

        // Fonts and pictures the document itself owns: Res.xml plus the PublicRes and DocumentRes files
        // CommonData names (tax invoices keep their pictures in DocumentRes); pages fall back to these.
        var docRes = LoadRes(entries, docDir, "Res.xml");
        foreach (var name in new[] { "PublicRes", "DocumentRes" })
            if (commonData?.Descendants().FirstOrDefault(d => d.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } file)
                docRes = MergeRes(LoadRes(entries, docDir, file), docRes);

        // Template pages (tax invoices): the fixed layout every page is drawn on top of.
        var templates = new Dictionary<string, (string Path, XElement Root)>();
        foreach (var tpl in document.Descendants().Where(d => d.Name.LocalName == "TemplatePage"))
        {
            if (Attr(tpl, "ID") is not { } id || Attr(tpl, "BaseLoc") is not { Length: > 0 } loc)
                continue;
            var templatePath = loc.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? Join(docDir, loc)
                : Join(docDir, loc, "Content.xml");
            if (LoadXml(entries, templatePath)?.Root is { } templateRoot)
                templates[id] = (templatePath, templateRoot);
        }

        var section = new HSection();
        double pageW = defaultW, pageH = defaultH;
        var firstPage = true;
        var allItems = new List<Item>();
        foreach (var page in document.Descendants().Where(d => d.Name.LocalName == "Page"))
        {
            // BaseLoc is a folder (the standard) or the content file itself (WPS writes Pages/Page_0/Content.xml).
            var loc = Attr(page, "BaseLoc") ?? "";
            var pagePath = loc.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                ? Join(docDir, loc)
                : Join(docDir, loc, Attr(page, "PageFile") ?? "Content.xml");
            var content = LoadXml(entries, pagePath);
            if (content?.Root is null)
                continue;

            // Templates the page is drawn on: their labels (发票号码：, 购买方信息) are read together with
            // the page's own values, so the invoice comes out complete.
            var usedTemplates = new List<(string Dir, XElement Root)>();
            foreach (var use in content.Root.Descendants().Where(d => d.Name.LocalName == "Template"))
                if (Attr(use, "TemplateID") is { } tid && templates.TryGetValue(tid, out var tpl))
                    usedTemplates.Add((DirName(tpl.Path), tpl.Root));

            pageW = defaultW;
            pageH = defaultH;
            if (PageBox(content.Root.Descendants().FirstOrDefault(d => d.Name.LocalName == "PhysicalBox")?.Value) is { } pageBox)
            {
                pageW = pageBox.W;
                pageH = pageBox.H;
            }
            else if (usedTemplates.Select(t => PageBox(t.Root.Descendants().FirstOrDefault(d => d.Name.LocalName == "PhysicalBox")?.Value))
                                   .FirstOrDefault(b => b is not null) is { } templateBox)
            {
                pageW = templateBox.W;
                pageH = templateBox.H;
            }

            var pageDir = DirName(pagePath);
            var res = MergeRes(LoadRes(entries, pageDir, "Res.xml"), docRes);
            var items = new List<Item>();
            foreach (var (tplDir, tplRoot) in usedTemplates)
            {
                var templateRes = MergeRes(LoadRes(entries, tplDir, "Res.xml"), docRes);
                items.AddRange(MergeVerticalLabels(Collect(tplRoot, tplDir, templateRes, entries)));
            }
            items.AddRange(Collect(content.Root, pageDir, res, entries));
            allItems.AddRange(items);

            var firstBlockOfPage = true;
            foreach (var block in Layout(items, mediaFolder))
            {
                if (!firstPage && firstBlockOfPage && block is HParagraph p)
                    p.PageBreakBefore = true;
                firstBlockOfPage = false;
                section.Blocks.Add(block);
            }
            firstPage = false;
        }

        if (section.Blocks.Count == 0)
            section.Blocks.Add(new HParagraph());
        section.Page = PageSetup(allItems, pageW, pageH);

        var doc = new HDocument();
        doc.Sections.Add(section);
        return doc;
    }

    // ───────────────────────── Page items ─────────────────────────

    /// <summary>A text fragment or a picture with its box, in millimetres from the page's top-left.</summary>
    private abstract class Item
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    private sealed class Fragment(string text, double sizeMm, string? color, string? font, bool bold) : Item
    {
        public string Text { get; } = text;
        public double SizeMm { get; } = sizeMm;
        public string? Color { get; } = color;
        public string? Font { get; } = font;
        public bool Bold { get; } = bold;
    }

    private sealed class Picture(string entry) : Item
    {
        public string Entry { get; } = entry;
    }

    /// <summary>Collects the text fragments and pictures of one page's content tree.</summary>
    private static List<Item> Collect(XElement page, string pageDir, Dictionary<string, string> res,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        var items = new List<Item>();
        foreach (var el in page.Descendants())
        {
            if (el.Name.LocalName == "TextObject")
            {
                var box = BoxWH(Attr(el, "Boundary"));
                double size = Attr(el, "Size") is { } s && double.TryParse(s, out var mm) && mm > 0 ? mm : 3.5;
                string? color = Color(Attr(el, "FillColor")) ?? Color(FillValue(el)) ?? Color(LayerColor(el, res));
                string? font = Attr(el, "Font") is { } id ? res.GetValueOrDefault("font:" + id) : null;
                bool bold = Attr(el, "Weight") is { } weight && weight == "700";
                var originX = box?.X ?? 0;
                var originY = box?.Y ?? 0;
                foreach (var code in el.Descendants().Where(d => d.Name.LocalName == "TextCode"))
                {
                    var text = (code.Value ?? "").Replace("\r\n", "\n");
                    double x = Parse(Attr(code, "X"));
                    double y = Parse(Attr(code, "Y"));   // of the baseline, from the object's box
                    var at = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.Length > 0)
                        {
                            // Column-wide boxes (the 数量 cell holding one digit) must not swallow the gap to
                            // the next column: the text's own width, when smaller, wins. DeltaX advances are
                            // exact; the last character is as wide as the advance before it (half the size for
                            // digits), not the full size.
                            var own = DeltaX(code) is { } advances
                                ? advances.Sum() + (advances.Length > 0 ? advances[^1] : size)
                                : EstimateWidth(trimmed, size);
                            var w = at == 0 && box is { W: > 0 } b ? Math.Min(b.W, own) : own;
                            items.Add(new Fragment(trimmed, size, color, font, bold)
                                .At(originX + x, originY + y - size * 0.86 + at * size * 1.3,
                                    w, Math.Max(box?.H ?? 0, size * 1.25)));
                        }
                        at++;
                    }
                }
            }
            else if (el.Name.LocalName == "ImageObject")
            {
                if (Attr(el, "ResourceID") is not { } id || !res.TryGetValue("media:" + id, out var entry))
                    continue;
                if (BoxWH(Attr(el, "Boundary")) is { } image)
                    items.Add(new Picture(entry).At(image.X, image.Y, image.W, image.H));
            }
        }
        return items;
    }

    /// <summary>Position setter: records give positional equality, so boxes are set after construction.</summary>
    private static T At<T>(this T item, double x, double y, double w, double h) where T : Item
    {
        item.X = x; item.Y = y; item.W = w; item.H = h;
        return item;
    }

    // ───────────────────────── Layout ─────────────────────────

    /// <summary>Boxes → paragraphs in reading order; fragments that line up share one line.</summary>
    private static List<HBlock> Layout(List<Item> items, string mediaFolder)
    {
        var blocks = new List<(double Y, HBlock Block)>();

        // Fragments into lines: same band of the page, ordered left to right.
        var lines = new List<List<Fragment>>();
        foreach (var fragment in items.Where(i => i is Fragment).Cast<Fragment>()
                                      .OrderBy(f => f.Y + f.H / 2).ThenBy(f => f.X))
        {
            var line = lines.LastOrDefault();
            if (line is not null && Math.Abs((fragment.Y + fragment.H / 2) - LineMid(line)) < 0.6 * Math.Max(fragment.H, 1))
                line.Add(fragment);
            else
                lines.Add([fragment]);
        }

        // A wrapped item name continues its row (转印组 / 件): a lone CJK fragment under a line that ends in a
        // number, starting at the same left edge, joins that line — a new row always brings its own columns, so
        // it has several fragments and never matches.
        for (var i = 1; i < lines.Count; i++)
        {
            var previous = lines[i - 1];
            var current = lines[i];
            if (current.Count == 1 && previous.Count > 1
                && Math.Abs(current[0].X - previous.Min(f => f.X)) < 2
                && current[0].Y - previous.Max(f => f.Y) is > 0 and < 8
                && current[0].Text.All(c => c >= 0x2E80)
                && previous.MaxBy(f => f.X) is { Text.Length: > 0 } last && char.IsAsciiDigit(last.Text[^1]))
            {
                previous.Add(current[0]);
                lines.RemoveAt(i);
                i--;
            }
        }

        foreach (var group in lines)
        {
            var paragraph = new HParagraph();
            var line = group.OrderBy(f => f.X).ToList();
            for (var i = 0; i < line.Count; i++)
            {
                if (i > 0)
                {
                    var gap = line[i].X - (line[i - 1].X + line[i - 1].W);
                    if (gap > 0.5 * line[i].SizeMm)
                        paragraph.Inlines.Add(new HText(" ", Format(line[i])));
                }
                paragraph.Inlines.Add(new HText(line[i].Text, Format(line[i])));
            }
            blocks.Add((line.Average(f => f.Y), paragraph));
        }

        // Pictures: one centred paragraph each (the seal of an invoice), in reading order.
        foreach (var picture in items.Where(i => i is Picture).Cast<Picture>().OrderBy(p => p.Y + p.H / 2))
        {
            if (Extract(picture, mediaFolder) is not { } file)
                continue;
            var paragraph = new HParagraph { Format = new(Align: HAlign.Center) };
            paragraph.Inlines.Add(new HImage(file)
            {
                Width = (int)Math.Round(picture.W * MmToUnit),
                Height = (int)Math.Round(picture.H * MmToUnit),
            });
            blocks.Add((picture.Y, paragraph));
        }

        return blocks.OrderBy(b => b.Y).Select(b => b.Block).ToList();
    }

    /// <summary>One-character labels stacked at the same X (购买方信息, 备注 — the vertical block titles of an
    /// invoice) become one fragment so they read as a word. Template content only: the page's own single
    /// characters are table cells that must stay apart (rows sit closer than label characters).</summary>
    private static List<Item> MergeVerticalLabels(List<Item> items)
    {
        var vertical = items.Where(i => i is Fragment { Text.Length: 1 } f && f.Text[0] >= 0x2E80).Cast<Fragment>()
                            .OrderBy(f => f.X).ThenBy(f => f.Y).ToList();
        if (vertical.Count < 2)
            return items;

        var groups = new List<List<Fragment>>();
        foreach (var fragment in vertical)
        {
            var group = groups.LastOrDefault(g => Math.Abs(g[0].X - fragment.X) < 0.75 && fragment.Y - g[^1].Y is > 0 and < 7.5);
            if (group is not null && group.Count < 8)
                group.Add(fragment);
            else
                groups.Add([fragment]);
        }

        var replaced = new HashSet<Fragment>();
        var result = new List<Item>(items.Count);
        foreach (var group in groups.Where(g => g.Count >= 2))
        {
            var first = group[0];
            replaced.UnionWith(group);
            result.Add(new Fragment(string.Concat(group.Select(g => g.Text)), first.SizeMm, first.Color, first.Font, first.Bold)
                .At(first.X, first.Y, first.W, group[^1].Y + group[^1].H - first.Y));
        }
        result.AddRange(items.Where(i => i is not Fragment f || !replaced.Contains(f)));
        return result;
    }

    private static double LineMid(List<Fragment> line) => line.Average(f => f.Y + f.H / 2);

    private static HCharFormat Format(Fragment f) => new()
    {
        Size = (int)Math.Round(f.SizeMm * MmToCentiPt),
        Color = f.Color,
        Font = f.Font,
        EastAsianFont = f.Font,
        Bold = f.Bold ? true : null,
    };

    /// <summary>Unpacks a picture; exotic formats (JBIG2, JPEG-2000) are transcoded to PNG, unreadable ones skipped.</summary>
    private static string? Extract(Picture picture, string mediaFolder)
    {
        var name = "ofd-" + Guid.NewGuid().ToString("N")[..8];
        var ext = Extension(picture.Entry);
        Directory.CreateDirectory(mediaFolder);
        try
        {
            if (DirectImages.Contains(ext))
            {
                var file = Path.Combine(mediaFolder, $"{name}.{ext}");
                File.WriteAllBytes(file, ReadEntry(picture.Entry));
                return file;
            }
            using var image = new MagickImage(ReadEntry(picture.Entry));
            var png = Path.Combine(mediaFolder, $"{name}.png");
            image.Write(png);
            return png;
        }
        catch
        {
            return null;   // a seal Filee cannot decode is left out; the text of the invoice still converts
        }
    }

    // ───────────────────────── Resources ─────────────────────────

    /// <summary>Res file lookup: "media:id" → zip entry, "font:id" → font family name, "draw:id" → the colour
    /// a DrawParam fills with. The Res element's own BaseLoc ("Res") says where its media files live.</summary>
    private static Dictionary<string, string> LoadRes(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string dir, string fileName)
    {
        var res = new Dictionary<string, string>();
        var xml = LoadXml(entries, Join(dir, fileName));
        if (xml?.Root is null)
            return res;
        var mediaDir = Attr(xml.Root, "BaseLoc") is { Length: > 0 } baseLoc ? Join(dir, baseLoc) : dir;
        var drawParams = new List<(string Id, string? Relative, string? Color)>();
        foreach (var el in xml.Root.Descendants())
        {
            if (el.Name.LocalName == "MultiMedia" && Attr(el, "ID") is { } id)
                res["media:" + id] = ResolveEntry(entries, mediaDir, MediaFile(el));
            else if (el.Name.LocalName == "Font" && Attr(el, "ID") is { } fid)
                res["font:" + fid] = Attr(el, "FontName") ?? Attr(el, "FamilyName") ?? "";
            else if (el.Name.LocalName == "DrawParam" && Attr(el, "ID") is { } did)
                drawParams.Add((did, Attr(el, "Relative"), Attr(el, "FillColor") ?? FillValue(el) ?? StrokeValue(el)));
        }
        // A DrawParam without its own FillColor inherits from the one it is Relative to.
        for (var pass = 0; pass < 4 && drawParams.Any(p => !res.ContainsKey("draw:" + p.Id)); pass++)
            foreach (var (id, relative, colour) in drawParams.Where(p => !res.ContainsKey("draw:" + p.Id)))
            {
                if (colour is { Length: > 0 })
                    res["draw:" + id] = colour;
                else if (relative is { } rel && res.TryGetValue("draw:" + rel, out var inherited))
                    res["draw:" + id] = inherited;
            }
        return res;
    }

    private static Dictionary<string, string> MergeRes(Dictionary<string, string> page, Dictionary<string, string> doc)
    {
        foreach (var pair in doc.Where(p => !page.ContainsKey(p.Key)))
            page[pair.Key] = pair.Value;
        return page;
    }

    /// <summary>MediaFile is relative to the page (or document) the Res file belongs to; a few writers differ.</summary>
    private static string ResolveEntry(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string dir, string mediaFile)
    {
        foreach (var candidate in new[] { Join(dir, mediaFile), Join(dir, "Res", mediaFile), mediaFile })
            if (entries.ContainsKey(candidate))
                return candidate;
        return Join(dir, mediaFile);
    }

    // ───────────────────────── Page setup ─────────────────────────

    private static HPage PageSetup(List<Item> items, double pageW, double pageH)
    {
        double left = MinMarginMm, top = MinMarginMm, right = MinMarginMm, bottom = MinMarginMm;
        if (items.Count > 0)
        {
            left = Math.Max(MinMarginMm, items.Min(i => i.X));
            top = Math.Max(MinMarginMm, items.Min(i => i.Y));
            right = Math.Max(MinMarginMm, pageW - items.Max(i => i.X + i.W));
            bottom = Math.Max(MinMarginMm, pageH - items.Max(i => i.Y + i.H));
        }
        return new HPage((int)(pageW * MmToUnit), (int)(pageH * MmToUnit),
            (int)(left * MmToUnit), (int)(right * MmToUnit), (int)(top * MmToUnit), (int)(bottom * MmToUnit), 0, 0, 0);
    }

    // ───────────────────────── Small helpers ─────────────────────────

    private static XDocument? LoadXml(IReadOnlyDictionary<string, ZipArchiveEntry> entries, string path)
    {
        if (!entries.TryGetValue(path, out var entry) || entry.Length == 0)
            return null;
        using var stream = entry.Open();
        return XDocument.Load(stream, LoadOptions.None);
    }

    private static byte[] ReadEntry(string path)
    {
        using var zip = ZipFile.OpenRead(_zipPath!);
        var entry = zip.Entries.FirstOrDefault(e => Norm(e.FullName) == path)
                    ?? throw new InvalidDataException($"The OFD package is missing {path}.");
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string? Attr(XElement el, string name) =>
        el.Attribute(name)?.Value ?? el.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>WPS writes colour as a FillColor child element with a decimal "R G B" Value attribute.</summary>
    private static string? FillValue(XElement el) =>
        el.Elements().FirstOrDefault(e => e.Name.LocalName == "FillColor")?.Attribute("Value")?.Value;

    /// <summary>StrokeColor the same way (a DrawParam may only define one).</summary>
    private static string? StrokeValue(XElement el) =>
        el.Elements().FirstOrDefault(e => e.Name.LocalName == "StrokeColor")?.Attribute("Value")?.Value;

    /// <summary>MediaFile is an attribute (WPS) or a child element (tax invoices).</summary>
    private static string MediaFile(XElement el) =>
        Attr(el, "MediaFile") ?? el.Elements().FirstOrDefault(e => e.Name.LocalName == "MediaFile")?.Value.Trim() ?? "";

    /// <summary>A Layer's DrawParam colours its text (tax invoices: red labels and amounts).</summary>
    private static string? LayerColor(XElement el, Dictionary<string, string> res)
    {
        var layer = el.Ancestors().FirstOrDefault(a => a.Name.LocalName == "Layer");
        return layer is not null && Attr(layer, "DrawParam") is { } id ? res.GetValueOrDefault("draw:" + id) : null;
    }

    /// <summary>TextCode DeltaX: per-character advances in millimetres, when the writer spelled them out.</summary>
    private static double[]? DeltaX(XElement code)
    {
        if (Attr(code, "DeltaX") is not { } value)
            return null;
        var advances = new List<double>();
        foreach (var part in value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (double.TryParse(part, out var advance))
                advances.Add(advance);
        return advances.Count > 0 ? advances.ToArray() : null;
    }

    /// <summary>Width of a text run without DeltaX: full width for CJK, half for digits (the advance tax
    /// invoices use), slightly more for other Latin.</summary>
    private static double EstimateWidth(string text, double size) =>
        text.Sum(c =>
        {
            if (c >= 0x2E80)
                return size;
            if (char.IsAsciiDigit(c) || c == '.')
                return size * 0.5;
            return size * 0.55;
        });

    /// <summary>Boundary "x y w h" in millimetres (TextObject, ImageObject); null when missing.</summary>
    private static (double X, double Y, double W, double H)? BoxWH(string? value)
    {
        var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is not [var x, var y, var w, var h])
            return null;
        return Parse(w) > 0 && Parse(h) > 0 ? (Parse(x), Parse(y), Parse(w), Parse(h)) : null;
    }

    /// <summary>PhysicalBox "xmin ymin xmax ymax" in millimetres (page area); null when missing.</summary>
    private static (double W, double H)? PageBox(string? value)
    {
        var parts = value?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts is not [var x0, var y0, var x1, var y1])
            return null;
        var w = Math.Max(1, Parse(x1) - Parse(x0));
        var h = Math.Max(1, Parse(y1) - Parse(y0));
        return w is <= 0 or > 2000 || h is <= 0 or > 2000 ? null : (w, h);
    }

    private static string? Color(string? value) => value switch
    {
        null or "" => null,
        ['#', .. var hex] when hex.Length >= 6 => "#" + hex[^6..],   // #RRGGBB or #AARRGGBB
        _ when value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [var r, var g, var br]
              && int.TryParse(r, out var red) && int.TryParse(g, out var green) && int.TryParse(br, out var blue)
            => $"#{red:X2}{green:X2}{blue:X2}",   // decimal "R G B" (WPS FillColor Value)
        _ => null,
    };

    private static double Parse(string? value) => double.TryParse(value, out var number) ? number : 0;

    private static string Extension(string entry) => Path.GetExtension(entry).TrimStart('.').ToLowerInvariant();

    /// <summary>Zip entry names use forward slashes; writers differ in case and in leading slashes.</summary>
    private static string Norm(string path) => path.TrimStart('/', '\\').Replace('\\', '/').TrimEnd('/');

    private static string DirName(string path)
    {
        var i = path.LastIndexOf('/');
        return i < 0 ? "" : path[..i];
    }

    private static string Join(string dir, string file) =>
        string.IsNullOrEmpty(dir) ? Norm(file) : Norm(dir) + "/" + Norm(file);

    private static string Join(string dir, string folder, string file) =>
        string.IsNullOrEmpty(folder) ? Join(dir, file) : Join(dir, Norm(folder) + "/" + Norm(file));
}
