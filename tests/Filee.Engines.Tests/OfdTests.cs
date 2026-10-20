// OFD (GB/T 33190, the Chinese fixed-layout standard: e-invoices, official documents) → DOCX / TXT with the
// built-in reader. The sample packages are written in the test: the standard's own structure (invoice with a
// seal) and the WPS variant (page size in Document.xml, colour as a decimal FillColor child element).

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Filee.Core.Conversion;
using Filee.Core.Presets;

namespace Filee.Engines.Tests;

public class OfdTests(EngineFixture fx) : IClassFixture<EngineFixture>
{
    /// <summary>A one-page invoice-shaped OFD in the standard's structure, with a seal picture.</summary>
    private static string MakeStandardOfd(string folder)
    {
        var path = Path.Combine(folder, "invoice.ofd");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Text(zip, "OFD.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:OFD xmlns:ofd="http://www.ofdspec.org" Version="1.0"><ofd:DocBody>
            <ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>
            """);
        Text(zip, "Doc_0/Document.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Document xmlns:ofd="http://www.ofdspec.org"><ofd:Pages>
            <ofd:Page ID="1" BaseLoc="Pages/Page_0/"/></ofd:Pages></ofd:Document>
            """);
        Text(zip, "Doc_0/Pages/Page_0/Content.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Page xmlns:ofd="http://www.ofdspec.org"><ofd:Content><ofd:Layer>
            <ofd:TextObject ID="1" Font="1" Size="7" Boundary="62 12 116 9" FillColor="#C00000">
            <ofd:TextCode X="0" Y="7.5">四川增值税电子普通发票</ofd:TextCode></ofd:TextObject>
            <ofd:TextObject ID="2" Font="1" Size="3.5" Boundary="18 38 12 5">
            <ofd:TextCode X="0" Y="3.8">购买方</ofd:TextCode></ofd:TextObject>
            <ofd:TextObject ID="3" Font="1" Size="3.5" Boundary="30 38 80 5">
            <ofd:TextCode X="0" Y="3.8">名称：成都双流科技有限公司</ofd:TextCode></ofd:TextObject>
            <ofd:ImageObject ID="4" ResourceID="10" Boundary="160 55 40 40"/>
            </ofd:Layer></ofd:Content></ofd:Page>
            """);
        Text(zip, "Doc_0/Pages/Page_0/Res.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Res xmlns:ofd="http://www.ofdspec.org"><ofd:Fonts>
            <ofd:Font ID="1" FontName="宋体"/></ofd:Fonts><ofd:MultiMedias>
            <ofd:MultiMedia ID="10" Type="Image" MediaFile="seal.png"/></ofd:MultiMedias></ofd:Res>
            """);
        // A 4×4 red PNG: the seal of the invoice.
        var png = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x04, 0x08, 0x02, 0x00, 0x00, 0x00, 0x26, 0x93, 0x11,
            0xE3, 0x00, 0x00, 0x00, 0x18, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x62, 0x18, 0xEE, 0xE3, 0xE3,
            0xE3, 0x83, 0x83, 0x38, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x19, 0x26, 0x03, 0xD3, 0x00, 0x7B, 0x00,
            0x51, 0x84, 0x19, 0x47, 0x8E, 0xB8, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42,
            0x60, 0x82,
        };
        Entry(zip, "Doc_0/Pages/Page_0/Res/seal.png", png);
        return path;
    }

    /// <summary>A two-page document in the WPS variant: the page size lives in Document.xml, BaseLoc names the
    /// content file itself and the colour is a decimal FillColor child element.</summary>
    private static string MakeWpsOfd(string folder)
    {
        var path = Path.Combine(folder, "letter.ofd");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Text(zip, "OFD.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:OFD xmlns:ofd="http://www.ofdspec.org/2016" Version="1.1"><ofd:DocBody>
            <ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>
            """);
        Text(zip, "Doc_0/Document.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Document xmlns:ofd="http://www.ofdspec.org/2016"><ofd:CommonData>
            <ofd:PageArea><ofd:PhysicalBox>0 0 210 297</ofd:PhysicalBox></ofd:PageArea>
            </ofd:CommonData><ofd:Pages>
            <ofd:Page ID="1" BaseLoc="Pages/Page_0/Content.xml"/>
            <ofd:Page ID="2" BaseLoc="Pages/Page_1/Content.xml"/></ofd:Pages></ofd:Document>
            """);
        Text(zip, "Doc_0/Pages/Page_0/Content.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Page xmlns:ofd="http://www.ofdspec.org/2016"><ofd:Content><ofd:Layer>
            <ofd:TextObject ID="1" Font="3" Size="4" Boundary="20 30 170 5" Weight="700"><ofd:FillColor Value="5 7 59"/>
            <ofd:TextCode X="0" Y="4.3">医院信息化建设方案</ofd:TextCode></ofd:TextObject>
            <ofd:TextObject ID="2" Font="3" Size="3.5" Boundary="20 40 170 5">
            <ofd:TextCode X="0" Y="3.8" DeltaX="3.5 3.5 3.5">第一章 总体规划</ofd:TextCode></ofd:TextObject>
            </ofd:Layer></ofd:Content></ofd:Page>
            """);
        Text(zip, "Doc_0/Pages/Page_1/Content.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Page xmlns:ofd="http://www.ofdspec.org/2016"><ofd:Content><ofd:Layer>
            <ofd:TextObject ID="1" Font="3" Size="3.5" Boundary="20 30 170 5">
            <ofd:TextCode X="0" Y="3.8">第二章 实施步骤</ofd:TextCode></ofd:TextObject>
            </ofd:Layer></ofd:Content></ofd:Page>
            """);
        Text(zip, "Doc_0/Res.xml",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <ofd:Res xmlns:ofd="http://www.ofdspec.org/2016"><ofd:Fonts>
            <ofd:Font ID="3" FontName="仿宋"/></ofd:Fonts></ofd:Res>
            """);
        return path;
    }

    [Fact]
    public async Task Standard_ofd_becomes_docx_and_txt()
    {
        var job = await fx.ConvertAsync([MakeStandardOfd(fx.NewFolder())], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Completed, job.State);
        var docx = job.Outputs.Single();

        // The text arrives (title red, buyer block on one line) and the seal picture is embedded.
        using (var zip = ZipFile.OpenRead(docx))
        {
            var document = XDocument.Load(zip.Entries.Single(e => e.FullName == "word/document.xml").Open());
            var text = string.Concat(document.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value));
            Assert.Contains("四川增值税电子普通发票", text);
            Assert.Contains("名称：成都双流科技有限公司", text);
            Assert.Contains("C00000", document.ToString());   // the title keeps its colour
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("word/media/"));
        }

        job = await fx.ConvertAsync([MakeStandardOfd(fx.NewFolder())], new Preset { TargetFormat = "txt" });
        Assert.Equal(JobState.Completed, job.State);
        var lines = (await File.ReadAllTextAsync(job.Outputs.Single(), TestContext.Current.CancellationToken)).Replace("\r\n", "\n").Split('\n');
        Assert.Contains("四川增值税电子普通发票", lines);
    }

    [Fact]
    public async Task Wps_ofd_keeps_pages_bold_and_colour()
    {
        var job = await fx.ConvertAsync([MakeWpsOfd(fx.NewFolder())], new Preset { TargetFormat = "docx" });
        Assert.Equal(JobState.Completed, job.State);

        using var zip = ZipFile.OpenRead(job.Outputs.Single());
        var document = XDocument.Load(zip.Entries.Single(e => e.FullName == "word/document.xml").Open());
        var xml = document.ToString();
        Assert.Contains("医院信息化建设方案", xml);
        Assert.Contains("05073B", xml);                       // decimal "5 7 59" → #05073B
        Assert.Contains("仿宋", xml);
        Assert.Equal(1, Regex.Count(xml, "pageBreakBefore"));  // the second page starts on a new page
    }

    // ───────────────────────── Package helpers ─────────────────────────

    private static void Text(ZipArchive zip, string name, string content) =>
        Entry(zip, name, Encoding.UTF8.GetBytes(content));

    private static void Entry(ZipArchive zip, string name, byte[] bytes)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }
}
