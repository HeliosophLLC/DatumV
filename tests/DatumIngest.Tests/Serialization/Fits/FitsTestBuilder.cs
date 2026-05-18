using System.Text;

namespace DatumIngest.Tests.Serialization.Fits;

/// <summary>
/// Builds tiny well-formed FITS byte streams for tests. Mirrors the on-disk
/// layout exactly: 80-char ASCII cards packed 36-per-block, header
/// terminated by an <c>END</c> card and zero-padded to the next 2880-byte
/// boundary, followed by an optional data section similarly padded.
/// </summary>
internal sealed class FitsTestBuilder
{
    private const int BlockBytes = 2880;
    private const int CardBytes = 80;

    private readonly List<HduRecord> _hdus = [];
    private HduRecord? _current;

    private sealed class HduRecord
    {
        public List<string> Cards { get; } = [];
        public byte[]? Data { get; set; }
        public bool Closed { get; set; }
    }

    public FitsTestBuilder BeginPrimary()
    {
        EnsurePreviousClosed();
        _current = new HduRecord();
        _hdus.Add(_current);
        AddValueCard("SIMPLE", "T");
        return this;
    }

    public FitsTestBuilder BeginExtension(string xtensionValue)
    {
        EnsurePreviousClosed();
        _current = new HduRecord();
        _hdus.Add(_current);
        AddQuotedCard("XTENSION", xtensionValue);
        return this;
    }

    public FitsTestBuilder Int(string keyword, long value)
    {
        AddValueCard(keyword, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    public FitsTestBuilder Double(string keyword, double value)
    {
        AddValueCard(keyword, value.ToString("0.0##############E0", System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    public FitsTestBuilder Bool(string keyword, bool value)
    {
        AddValueCard(keyword, value ? "T" : "F");
        return this;
    }

    public FitsTestBuilder QuotedString(string keyword, string value)
    {
        AddQuotedCard(keyword, value);
        return this;
    }

    public FitsTestBuilder Comment(string text) { RequireCurrent().Cards.Add(PadCard("COMMENT " + text)); return this; }
    public FitsTestBuilder History(string text) { RequireCurrent().Cards.Add(PadCard("HISTORY " + text)); return this; }

    public FitsTestBuilder EndHdu()
    {
        HduRecord hdu = RequireCurrent();
        hdu.Cards.Add(PadCard("END"));
        hdu.Closed = true;
        return this;
    }

    /// <summary>
    /// Appends a raw data section to the most-recently-closed HDU. Must be
    /// called between an <see cref="EndHdu"/> and the next <c>BeginXxx</c>.
    /// </summary>
    public FitsTestBuilder AppendData(byte[] data)
    {
        if (_current is null || !_current.Closed)
        {
            throw new InvalidOperationException(
                "AppendData must follow EndHdu (and precede the next Begin call).");
        }
        if (_current.Data is not null)
        {
            throw new InvalidOperationException("This HDU already has a data section attached.");
        }
        _current.Data = data;
        return this;
    }

    public byte[] Build()
    {
        EnsurePreviousClosed();
        using MemoryStream output = new();
        foreach (HduRecord hdu in _hdus)
        {
            // Header block(s) — pad card count up to a multiple of 36.
            int cardCount = hdu.Cards.Count;
            int blockCount = (cardCount + 35) / 36;
            int totalCards = blockCount * 36;
            for (int c = 0; c < totalCards; c++)
            {
                string card = c < cardCount ? hdu.Cards[c] : new string(' ', CardBytes);
                output.Write(Encoding.ASCII.GetBytes(card));
            }

            // Optional data section, padded to next 2880.
            if (hdu.Data is { } data)
            {
                output.Write(data);
                int padCount = (BlockBytes - (data.Length % BlockBytes)) % BlockBytes;
                if (padCount > 0)
                {
                    output.Write(new byte[padCount]);
                }
            }
        }
        return output.ToArray();
    }

    private HduRecord RequireCurrent() =>
        _current ?? throw new InvalidOperationException("No HDU open. Call BeginPrimary or BeginExtension first.");

    private void EnsurePreviousClosed()
    {
        if (_current is not null && !_current.Closed)
        {
            throw new InvalidOperationException("Previous HDU was not closed with EndHdu().");
        }
    }

    private void AddValueCard(string keyword, string formattedValue)
    {
        string paddedKeyword = keyword.PadRight(8);
        string paddedValue = formattedValue.PadLeft(20);
        string card = $"{paddedKeyword}= {paddedValue}";
        RequireCurrent().Cards.Add(PadCard(card));
    }

    private void AddQuotedCard(string keyword, string value)
    {
        string padded = value.PadRight(8);
        string card = $"{keyword.PadRight(8)}= '{padded}'";
        RequireCurrent().Cards.Add(PadCard(card));
    }

    private static string PadCard(string s)
    {
        if (s.Length > CardBytes)
            throw new InvalidOperationException($"FITS card too long: \"{s}\" ({s.Length} > {CardBytes}).");
        return s.PadRight(CardBytes);
    }
}
