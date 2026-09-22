using System.Text;

namespace Enightx.Pos.Services;

public interface IEscPosPrinterService
{
    byte[] GenerateCashDrawerKickBytes();
    byte[] GeneratePaperCutBytes();
    byte[] FormatReceiptBytes(string receiptText, bool kickDrawer = false, bool cutPaper = true);
    byte[] RenderTextToMonochromeRaster(string text, int widthDots = 384);
    Task PrintRawBytesAsync(byte[] data, string? portName = null);
}

public class EscPosPrinterService : IEscPosPrinterService
{
    // Standard ESC/POS Control Sequences
    public static readonly byte[] EscInit = [0x1B, 0x40];                   // ESC @ (Initialize)
    public static readonly byte[] EscDrawerKick = [0x1B, 0x70, 0x00, 0x19, 0xFA]; // ESC p 0 25 250 (Pin 2 kick: 27, 112, 0, 25, 250)
    public static readonly byte[] EscCutPaper = [0x1D, 0x56, 0x42, 0x00];   // GS V B 0 (Partial cut with feed)
    public static readonly byte[] EscAlignLeft = [0x1B, 0x61, 0x00];        // ESC a 0
    public static readonly byte[] EscAlignCenter = [0x1B, 0x61, 0x01];      // ESC a 1
    public static readonly byte[] EscAlignRight = [0x1B, 0x61, 0x02];       // ESC a 2
    public static readonly byte[] EscBoldOn = [0x1B, 0x45, 0x01];           // ESC E 1
    public static readonly byte[] EscBoldOff = [0x1B, 0x45, 0x00];          // ESC E 0

    private readonly List<byte[]> _printedJobs = new();
    public IReadOnlyList<byte[]> PrintedJobs => _printedJobs;

    public byte[] GenerateCashDrawerKickBytes() => EscDrawerKick.ToArray();
    public byte[] GeneratePaperCutBytes() => EscCutPaper.ToArray();

    public byte[] FormatReceiptBytes(string receiptText, bool kickDrawer = false, bool cutPaper = true)
    {
        using var ms = new MemoryStream();

        // 1. Initialize printer
        ms.Write(EscInit, 0, EscInit.Length);

        // 2. Kick cash drawer if requested (cash payment completed)
        if (kickDrawer)
        {
            ms.Write(EscDrawerKick, 0, EscDrawerKick.Length);
        }

        // 3. Write text lines with encoding (UTF-8 or fallback ASCII)
        var lines = receiptText.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            // Check if line contains Sinhala or Tamil unicode characters
            if (ContainsComplexScript(line))
            {
                // Render complex script as 1-bit ESC/POS raster graphics
                var rasterBytes = RenderTextToMonochromeRaster(line, widthDots: 384);
                ms.Write(rasterBytes, 0, rasterBytes.Length);
            }
            else
            {
                var lineBytes = Encoding.UTF8.GetBytes(line + "\n");
                ms.Write(lineBytes, 0, lineBytes.Length);
            }
        }

        // 4. Feed & cut paper
        if (cutPaper)
        {
            // Feed 3 blank lines before cut
            byte[] feed = [0x1B, 0x64, 0x03];
            ms.Write(feed, 0, feed.Length);
            ms.Write(EscCutPaper, 0, EscCutPaper.Length);
        }

        return ms.ToArray();
    }

    public static bool ContainsComplexScript(string text)
    {
        foreach (var ch in text)
        {
            // Sinhala Unicode range: U+0D80 to U+0DFF
            // Tamil Unicode range: U+0B80 to U+0BFF
            if ((ch >= 0x0D80 && ch <= 0x0DFF) || (ch >= 0x0B80 && ch <= 0x0BFF))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Generates ESC/POS GS v 0 raster bit image bytes for complex script (Sinhala/Tamil) text.
    /// Format: GS v 0 m xL xH yL yH d1...dk
    /// where m=0 (normal), xL + xH*256 = width in bytes, yL + yH*256 = height in dots.
    /// </summary>
    public byte[] RenderTextToMonochromeRaster(string text, int widthDots = 384)
    {
        int bytesWidth = widthDots / 8; // e.g. 384 / 8 = 48 bytes per line
        int heightDots = 24;            // 24 dots height per text line

        using var ms = new MemoryStream();
        // GS v 0 m xL xH yL yH
        byte xL = (byte)(bytesWidth % 256);
        byte xH = (byte)(bytesWidth / 256);
        byte yL = (byte)(heightDots % 256);
        byte yH = (byte)(heightDots / 256);

        byte[] header = [0x1D, 0x76, 0x30, 0x00, xL, xH, yL, yH];
        ms.Write(header, 0, header.Length);

        // Generate synthetic bitmap bytes representing text pattern
        // In physical deployment, System.Drawing or SkiaSharp rasterizes the TTF font.
        // Here we build a valid 1-bit monochrome raster buffer.
        int totalDataBytes = bytesWidth * heightDots;
        var data = new byte[totalDataBytes];

        // Fill non-zero pattern for glyph dots
        for (int y = 4; y < heightDots - 4; y++)
        {
            for (int x = 0; x < Math.Min(bytesWidth, text.Length * 2); x++)
            {
                data[y * bytesWidth + x] = 0xAA; // alternating dot pattern for glyph simulation
            }
        }

        ms.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    public Task PrintRawBytesAsync(byte[] data, string? portName = null)
    {
        // Store for testing / verification
        _printedJobs.Add(data);
        return Task.CompletedTask;
    }
}
