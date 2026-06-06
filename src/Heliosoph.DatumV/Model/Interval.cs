using System.Globalization;
using System.Text;

namespace Heliosoph.DatumV.Model;

/// <summary>
/// A PostgreSQL-compatible <c>interval</c>: a calendar-aware time span with
/// three independent components — months, days, and microseconds. The split
/// preserves the semantics that <c>'1 month'</c> resolves to a calendar-month
/// shift at apply time rather than collapsing to a fixed elapsed duration.
/// </summary>
/// <remarks>
/// <para>
/// Carries 16 bytes total and fits inline in a <see cref="DataValue"/>. Backs
/// <see cref="DataKind.Interval"/> end-to-end: factory, accessor, arithmetic,
/// parsing, and the postgres canonical output form.
/// </para>
/// <para>
/// Not totally ordered: a sort or comparison between <c>'30 days'</c> and
/// <c>'1 month'</c> has no well-defined answer outside an anchor date.
/// Totally-ordered sortable semantics stay on
/// <see cref="DataKind.Duration"/>.
/// </para>
/// </remarks>
public readonly struct Interval : IEquatable<Interval>
{
    /// <summary>Microseconds per second.</summary>
    public const long MicrosPerSecond = 1_000_000L;

    /// <summary>Microseconds per minute.</summary>
    public const long MicrosPerMinute = 60L * MicrosPerSecond;

    /// <summary>Microseconds per hour.</summary>
    public const long MicrosPerHour = 60L * MicrosPerMinute;

    /// <summary>Microseconds per (calendar-agnostic) day, used by justify_hours and the canonical-day boundary.</summary>
    public const long MicrosPerDay = 24L * MicrosPerHour;

    /// <summary>Days per (canonical) month, used by justify_days/justify_interval normalisation.</summary>
    public const int DaysPerMonth = 30;

    /// <summary>Months per year, used by output formatting and EXTRACT(year ...).</summary>
    public const int MonthsPerYear = 12;

    /// <summary>The zero interval — no months, no days, no microseconds.</summary>
    public static readonly Interval Zero = new(0, 0, 0);

    /// <summary>Calendar-month component. Variable-length (28–31 days) — applied against the anchor at use time.</summary>
    public int Months { get; }

    /// <summary>Calendar-day component. DST-variable when applied to a timestamptz; otherwise 24 h each.</summary>
    public int Days { get; }

    /// <summary>Sub-day component in microseconds. Matches Postgres' interval precision.</summary>
    public long Microseconds { get; }

    /// <summary>Constructs an interval from its three independent fields.</summary>
    public Interval(int months, int days, long microseconds)
    {
        Months = months;
        Days = days;
        Microseconds = microseconds;
    }

    /// <summary>Constructs an interval from a <see cref="TimeSpan"/> (pure elapsed time → days + microseconds, no months).</summary>
    public static Interval FromTimeSpan(TimeSpan ts)
    {
        long micros = ts.Ticks / 10L; // 1 tick = 100 ns = 0.1 µs
        long days = micros / MicrosPerDay;
        long remainder = micros - days * MicrosPerDay;
        return new Interval(0, checked((int)days), remainder);
    }

    /// <summary>Adds another interval field-wise. Carry is not normalised.</summary>
    public Interval Add(Interval other) =>
        new(unchecked(Months + other.Months),
            unchecked(Days + other.Days),
            unchecked(Microseconds + other.Microseconds));

    /// <summary>Subtracts another interval field-wise. Carry is not normalised.</summary>
    public Interval Subtract(Interval other) =>
        new(unchecked(Months - other.Months),
            unchecked(Days - other.Days),
            unchecked(Microseconds - other.Microseconds));

    /// <summary>Negates every component.</summary>
    public Interval Negate() => new(-Months, -Days, -Microseconds);

    /// <summary>
    /// Scalar multiplication: each component scales independently and any
    /// fractional remainder carries down (months → days using the 30-day
    /// canonical month, days → microseconds using the 24-hour canonical day).
    /// Matches Postgres' <c>interval * double precision</c>.
    /// </summary>
    public Interval Multiply(double factor)
    {
        double monthsScaled = Months * factor;
        int monthsWhole = (int)Math.Truncate(monthsScaled);
        double monthsFrac = monthsScaled - monthsWhole;

        double daysScaled = Days * factor + monthsFrac * DaysPerMonth;
        int daysWhole = (int)Math.Truncate(daysScaled);
        double daysFrac = daysScaled - daysWhole;

        double microsScaled = Microseconds * factor + daysFrac * MicrosPerDay;
        long microsWhole = (long)Math.Round(microsScaled, MidpointRounding.AwayFromZero);

        return new Interval(monthsWhole, daysWhole, microsWhole);
    }

    /// <summary>
    /// PG <c>justify_hours</c>: microseconds spanning multiples of 24 hours
    /// shift into the day component. Days and months are left alone.
    /// </summary>
    public Interval JustifyHours()
    {
        long extraDays = Microseconds / MicrosPerDay;
        long remainder = Microseconds - extraDays * MicrosPerDay;
        return new Interval(Months, checked((int)(Days + extraDays)), remainder);
    }

    /// <summary>
    /// PG <c>justify_days</c>: days spanning multiples of 30 shift into months.
    /// Microseconds are left alone.
    /// </summary>
    public Interval JustifyDays()
    {
        int extraMonths = Days / DaysPerMonth;
        int remainder = Days - extraMonths * DaysPerMonth;
        return new Interval(checked(Months + extraMonths), remainder, Microseconds);
    }

    /// <summary>
    /// PG <c>justify_interval</c>: applies <see cref="JustifyHours"/> then
    /// <see cref="JustifyDays"/>, then aligns component signs so each field
    /// has the same sign as the overall total (Postgres' end-state shape).
    /// </summary>
    public Interval JustifyInterval()
    {
        Interval x = JustifyHours().JustifyDays();

        // Pull negative micros up into negative days; negative days into negative months;
        // and conversely for positive overflow from sign-mismatched fields.
        long micros = x.Microseconds;
        int days = x.Days;
        int months = x.Months;

        while (micros < 0 && days > 0)
        {
            micros += MicrosPerDay;
            days -= 1;
        }
        while (micros > 0 && days < 0)
        {
            micros -= MicrosPerDay;
            days += 1;
        }
        while (days < 0 && months > 0)
        {
            days += DaysPerMonth;
            months -= 1;
        }
        while (days > 0 && months < 0)
        {
            days -= DaysPerMonth;
            months += 1;
        }
        return new Interval(months, days, micros);
    }

    /// <summary>
    /// Adds this interval to <paramref name="source"/> using PG's apply
    /// semantics: month component first (<see cref="DateTime.AddMonths"/>,
    /// calendar-correct end-of-month clamp), then days, then microseconds.
    /// </summary>
    public DateTime AddTo(DateTime source)
    {
        DateTime result = source;
        if (Months != 0) result = result.AddMonths(Months);
        if (Days != 0) result = result.AddDays(Days);
        if (Microseconds != 0) result = result.AddTicks(Microseconds * 10L);
        return result;
    }

    /// <summary>
    /// <see cref="DateTimeOffset"/> overload of <see cref="AddTo(DateTime)"/>.
    /// Months and days apply to the wall-clock face; microseconds apply to the
    /// UTC instant. Mirrors PG's <c>timestamptz + interval</c> semantics
    /// against the session TZ (here UTC, until session TZ lands).
    /// </summary>
    public DateTimeOffset AddTo(DateTimeOffset source)
    {
        DateTimeOffset result = source;
        if (Months != 0) result = result.AddMonths(Months);
        if (Days != 0) result = result.AddDays(Days);
        if (Microseconds != 0) result = result.AddTicks(Microseconds * 10L);
        return result;
    }

    /// <summary>
    /// Adds this interval to a bare <see cref="DateOnly"/> by promoting it
    /// to midnight <see cref="DateTime"/> and applying the full interval; the
    /// caller decides whether to demote back to <c>Date</c>. PG semantics:
    /// <c>date + interval</c> returns <c>timestamp</c>.
    /// </summary>
    public DateTime AddTo(DateOnly source) => AddTo(source.ToDateTime(TimeOnly.MinValue));

    /// <summary>
    /// Produces a <see cref="DateTime"/>-shape difference: months and days are
    /// zero (Postgres' <c>timestamp - timestamp</c> never emits a months
    /// component), microseconds carry the full elapsed-ticks payload split
    /// into a days component and a sub-day microseconds component.
    /// </summary>
    public static Interval FromTimestampDifference(DateTime later, DateTime earlier)
    {
        long ticks = later.Ticks - earlier.Ticks;
        long micros = ticks / 10L;
        long days = micros / MicrosPerDay;
        long remainder = micros - days * MicrosPerDay;
        return new Interval(0, checked((int)days), remainder);
    }

    /// <summary>
    /// <see cref="DateTimeOffset"/> overload of <see cref="FromTimestampDifference(DateTime, DateTime)"/>.
    /// Diff is computed in UTC ticks; offsets are already baked in.
    /// </summary>
    public static Interval FromTimestampDifference(DateTimeOffset later, DateTimeOffset earlier)
    {
        long ticks = later.UtcTicks - earlier.UtcTicks;
        long micros = ticks / 10L;
        long days = micros / MicrosPerDay;
        long remainder = micros - days * MicrosPerDay;
        return new Interval(0, checked((int)days), remainder);
    }

    /// <summary>
    /// PG <c>age(later, earlier)</c> — calendar-aware difference returning a
    /// "human-readable" Interval with years / months / days / sub-day all
    /// counted independently. Distinct from <see cref="FromTimestampDifference(DateTime, DateTime)"/>
    /// which returns a pure-elapsed-time Interval (days + micros only).
    /// </summary>
    /// <remarks>
    /// Reproduces PG's calendar-walk: year diff is the integer year span,
    /// month diff is the residual months (with borrow when the later
    /// day-of-month is smaller than earlier's), day diff is the residual,
    /// and the time-of-day diff fills the microseconds. Borrows propagate
    /// across the month / year boundary so the resulting Interval, when
    /// applied to <paramref name="earlier"/>, reproduces
    /// <paramref name="later"/> exactly.
    /// </remarks>
    public static Interval Age(DateTime later, DateTime earlier)
    {
        int sign = 1;
        if (later < earlier)
        {
            (later, earlier) = (earlier, later);
            sign = -1;
        }

        int years = later.Year - earlier.Year;
        int months = later.Month - earlier.Month;
        int days = later.Day - earlier.Day;
        long timeMicros = (later.TimeOfDay.Ticks - earlier.TimeOfDay.Ticks) / 10L;

        // Borrow from coarser fields when finer fields go negative.
        if (timeMicros < 0)
        {
            timeMicros += MicrosPerDay;
            days -= 1;
        }
        if (days < 0)
        {
            // Borrow days from the previous calendar month (variable length).
            DateTime prevMonth = new DateTime(later.Year, later.Month, 1).AddDays(-1);
            days += prevMonth.Day;
            months -= 1;
        }
        if (months < 0)
        {
            months += MonthsPerYear;
            years -= 1;
        }

        long totalMonths = sign * (years * (long)MonthsPerYear + months);
        long totalDays = sign * days;
        long totalMicros = sign * timeMicros;
        return new Interval(checked((int)totalMonths), checked((int)totalDays), totalMicros);
    }

    /// <summary>
    /// <see cref="DateTimeOffset"/> overload of <see cref="Age(DateTime, DateTime)"/>.
    /// Computed in UTC so the result reflects the elapsed wall-clock span,
    /// not display-offset arithmetic.
    /// </summary>
    public static Interval Age(DateTimeOffset later, DateTimeOffset earlier) =>
        Age(later.UtcDateTime, earlier.UtcDateTime);

    /// <inheritdoc/>
    public bool Equals(Interval other) =>
        Months == other.Months && Days == other.Days && Microseconds == other.Microseconds;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Interval other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Months, Days, Microseconds);

    /// <summary>Equality operator. Field-wise; no anchor-relative normalisation.</summary>
    public static bool operator ==(Interval left, Interval right) => left.Equals(right);

    /// <summary>Inequality operator. Field-wise; no anchor-relative normalisation.</summary>
    public static bool operator !=(Interval left, Interval right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => Format();

    /// <summary>
    /// Postgres-canonical format. Examples:
    /// <c>"1 year 2 mons 3 days 04:05:06"</c>,
    /// <c>"-5 days"</c>,
    /// <c>"1 day -01:00:00"</c>.
    /// Mirrors <c>IntervalStyle = postgres</c>; an empty interval renders as <c>"00:00:00"</c>.
    /// </summary>
    public string Format()
    {
        StringBuilder sb = new();

        int totalMonths = Months;
        int years = totalMonths / MonthsPerYear;
        int leftoverMonths = totalMonths - years * MonthsPerYear;

        if (years != 0)
        {
            sb.Append(years).Append(years == 1 || years == -1 ? " year" : " years");
        }
        if (leftoverMonths != 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(leftoverMonths).Append(leftoverMonths == 1 || leftoverMonths == -1 ? " mon" : " mons");
        }
        if (Days != 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Days).Append(Days == 1 || Days == -1 ? " day" : " days");
        }

        bool hasTime = Microseconds != 0;
        bool hasOther = sb.Length > 0;

        if (hasTime || !hasOther)
        {
            if (hasOther) sb.Append(' ');
            AppendTimeComponent(sb, Microseconds);
        }

        return sb.ToString();
    }

    private static void AppendTimeComponent(StringBuilder sb, long micros)
    {
        bool negative = micros < 0;
        if (negative)
        {
            sb.Append('-');
            micros = -micros;
        }

        long hours = micros / MicrosPerHour;
        micros -= hours * MicrosPerHour;
        long mins = micros / MicrosPerMinute;
        micros -= mins * MicrosPerMinute;
        long secs = micros / MicrosPerSecond;
        long fracMicros = micros - secs * MicrosPerSecond;

        sb.Append(hours.ToString("D2", CultureInfo.InvariantCulture));
        sb.Append(':');
        sb.Append(mins.ToString("D2", CultureInfo.InvariantCulture));
        sb.Append(':');
        sb.Append(secs.ToString("D2", CultureInfo.InvariantCulture));
        if (fracMicros != 0)
        {
            sb.Append('.');
            // Six-digit microsecond precision, trailing zeros trimmed.
            string frac = fracMicros.ToString("D6", CultureInfo.InvariantCulture);
            int trim = frac.Length;
            while (trim > 0 && frac[trim - 1] == '0') trim--;
            sb.Append(frac, 0, trim);
        }
    }

    /// <summary>
    /// Parses any Postgres-accepted interval literal. Verbose
    /// (<c>"1 year 2 months 3 days 04:05:06"</c>), ISO 8601
    /// (<c>"P1Y2M3DT4H5M6S"</c>), and SQL-standard (<c>"1-2"</c> year-month,
    /// <c>"3 04:05:06"</c> day-time) forms are all recognised.
    /// </summary>
    /// <exception cref="FormatException">The input is empty or malformed.</exception>
    public static Interval Parse(string text)
    {
        if (TryParse(text, out Interval result))
        {
            return result;
        }
        throw new FormatException($"Invalid interval literal: '{text}'.");
    }

    /// <summary>
    /// Try-pattern variant of <see cref="Parse(string)"/>. Returns
    /// <see langword="false"/> for malformed input; never throws.
    /// </summary>
    public static bool TryParse(string text, out Interval result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        ReadOnlySpan<char> input = text.AsSpan().Trim();
        if (input.Length == 0) return false;

        // ISO 8601 forms start with P / -P / +P. Examples: 'P1Y2M3DT4H5M6S',
        // 'PT30M', 'P0001-02-03T04:05:06'. Verbose / SQL-standard never
        // begin with P, so a simple first-char gate routes deterministically.
        int isoStart = 0;
        int isoSign = 1;
        if (input.Length > 0 && (input[0] == '+' || input[0] == '-'))
        {
            isoSign = input[0] == '-' ? -1 : 1;
            isoStart = 1;
        }
        if (isoStart < input.Length && (input[isoStart] == 'P' || input[isoStart] == 'p'))
        {
            return TryParseIso8601(input[isoStart..], isoSign, out result);
        }

        // SQL-standard year-month: an optionally-signed digit run, then '-',
        // then another digit run, possibly trailed by whitespace + day-time.
        // We sniff for the '-' before falling into the generic verbose loop.
        if (TryParseSqlStandard(input, out result))
        {
            return true;
        }

        long months = 0;
        long days = 0;
        long micros = 0;

        int i = 0;
        while (i < input.Length)
        {
            // Skip whitespace + commas.
            while (i < input.Length && (char.IsWhiteSpace(input[i]) || input[i] == ',')) i++;
            if (i >= input.Length) break;

            int sign = 1;
            if (input[i] == '+') { sign = 1; i++; }
            else if (input[i] == '-') { sign = -1; i++; }
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            if (i >= input.Length) return false;

            // Parse a number (may be decimal). If we instead see a colon-form
            // time-of-day directly, route to the time parser below.
            int numStart = i;
            bool sawDigit = false;
            bool sawDot = false;
            while (i < input.Length)
            {
                char c = input[i];
                if (c >= '0' && c <= '9') { sawDigit = true; i++; continue; }
                if (c == '.' && !sawDot) { sawDot = true; i++; continue; }
                break;
            }
            if (!sawDigit) return false;

            // Time-of-day shorthand: digits followed by ':' → parse the rest as HH:MM:SS[.ffffff].
            if (i < input.Length && input[i] == ':')
            {
                ReadOnlySpan<char> timeSlice = input[numStart..];
                int consumed = ParseTimeOfDay(timeSlice, sign, out long todMicros);
                if (consumed < 0) return false;
                micros = checked(micros + todMicros);
                i = numStart + consumed;
                continue;
            }

            string numText = input[numStart..i].ToString();
            if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return false;
            }
            value *= sign;

            // Skip whitespace, then read the unit word.
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            int unitStart = i;
            while (i < input.Length && IsUnitChar(input[i])) i++;
            if (i == unitStart) return false;

            string unit = input[unitStart..i].ToString();
            if (!ApplyUnit(unit, value, ref months, ref days, ref micros)) return false;
        }

        if (months > int.MaxValue || months < int.MinValue) return false;
        if (days > int.MaxValue || days < int.MinValue) return false;

        result = new Interval((int)months, (int)days, micros);
        return true;
    }

    private static bool IsUnitChar(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == 'µ';

    /// <summary>
    /// Parses an ISO 8601 interval literal: the standard
    /// <c>P[nY][nM][nW][nD][T[nH][nM][n[.n]S]]</c> shape, and the alternate
    /// fixed-width form <c>P0001-02-03T04:05:06</c>. The leading <c>P</c> is
    /// assumed already consumed by the caller; <paramref name="sign"/> carries
    /// any outer <c>±</c> seen before the <c>P</c>. Returns
    /// <see langword="false"/> for malformed input.
    /// </summary>
    private static bool TryParseIso8601(ReadOnlySpan<char> input, int sign, out Interval result)
    {
        result = default;
        if (input.Length < 2) return false;
        if (input[0] != 'P' && input[0] != 'p') return false;

        ReadOnlySpan<char> body = input[1..];
        if (body.Length == 0) return false;

        // Alternate form is signalled by either a date-shape ('-' before any
        // unit letter) or a time-shape (':' anywhere). Both are unambiguous
        // departures from the standard letter-suffixed form, which never
        // contains either character.
        int dash = body.IndexOf('-');
        int colon = body.IndexOf(':');
        int t = body.IndexOfAny('T', 't');
        bool isAlt = colon >= 0 || (dash >= 0 && (t < 0 || dash < t));
        if (isAlt)
        {
            return TryParseIso8601Alt(body, sign, out result);
        }

        long months = 0;
        long days = 0;
        long micros = 0;
        bool inTime = false;
        int i = 0;

        while (i < body.Length)
        {
            char c = body[i];
            if (c == 'T' || c == 't')
            {
                if (inTime) return false;
                inTime = true;
                i++;
                continue;
            }

            int sStart = i;
            int signFactor = 1;
            if (c == '+') { i++; }
            else if (c == '-') { signFactor = -1; i++; }
            int numStart = i;
            bool sawDigit = false;
            bool sawDot = false;
            while (i < body.Length)
            {
                char d = body[i];
                if (d >= '0' && d <= '9') { sawDigit = true; i++; continue; }
                if (d == '.' && !sawDot) { sawDot = true; i++; continue; }
                break;
            }
            if (!sawDigit) return false;
            if (i >= body.Length) return false;
            char unit = body[i];
            i++;

            string numText = body[numStart..(i - 1)].ToString();
            if (!double.TryParse(numText, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return false;
            }
            value *= signFactor;

            if (!inTime)
            {
                switch (unit)
                {
                    case 'Y': case 'y':
                        AddMonths(value * MonthsPerYear, ref months, ref days, ref micros);
                        break;
                    case 'M': case 'm':
                        AddMonths(value, ref months, ref days, ref micros);
                        break;
                    case 'W': case 'w':
                        AddDays(value * 7.0, ref days, ref micros);
                        break;
                    case 'D': case 'd':
                        AddDays(value, ref days, ref micros);
                        break;
                    default:
                        return false;
                }
            }
            else
            {
                switch (unit)
                {
                    case 'H': case 'h':
                        micros = checked(micros + (long)Math.Round(value * MicrosPerHour, MidpointRounding.AwayFromZero));
                        break;
                    case 'M': case 'm':
                        micros = checked(micros + (long)Math.Round(value * MicrosPerMinute, MidpointRounding.AwayFromZero));
                        break;
                    case 'S': case 's':
                        micros = checked(micros + (long)Math.Round(value * MicrosPerSecond, MidpointRounding.AwayFromZero));
                        break;
                    default:
                        return false;
                }
            }
            _ = sStart;
        }

        if (months > int.MaxValue || months < int.MinValue) return false;
        if (days > int.MaxValue || days < int.MinValue) return false;

        if (sign < 0)
        {
            months = -months;
            days = -days;
            micros = -micros;
        }
        result = new Interval((int)months, (int)days, micros);
        return true;
    }

    /// <summary>
    /// Alternate ISO 8601 interval form: <c>P[YYYY-MM-DD][THH:MM:SS]</c>.
    /// Both halves are optional but at least one must appear. Numeric
    /// fields are fixed-width but we treat the digit runs flexibly to
    /// match Postgres' generous reader.
    /// </summary>
    private static bool TryParseIso8601Alt(ReadOnlySpan<char> body, int sign, out Interval result)
    {
        result = default;
        long months = 0;
        long days = 0;
        long micros = 0;

        int i = 0;
        // Year-month-day half: digits '-' digits '-' digits
        if (i < body.Length && body[i] != 'T' && body[i] != 't')
        {
            if (!ConsumeNumber(body, ref i, out long y)) return false;
            if (i >= body.Length || body[i] != '-') return false;
            i++;
            if (!ConsumeNumber(body, ref i, out long mo)) return false;
            if (i >= body.Length || body[i] != '-') return false;
            i++;
            if (!ConsumeNumber(body, ref i, out long d)) return false;
            months = y * MonthsPerYear + mo;
            days = d;
        }
        // Time half: T HH:MM:SS[.f]
        if (i < body.Length && (body[i] == 'T' || body[i] == 't'))
        {
            i++;
            int consumed = ParseTimeOfDay(body[i..], sign: 1, out long todMicros);
            if (consumed < 0) return false;
            i += consumed;
            micros = todMicros;
        }
        if (i != body.Length) return false;

        if (sign < 0)
        {
            months = -months;
            days = -days;
            micros = -micros;
        }
        if (months > int.MaxValue || months < int.MinValue) return false;
        if (days > int.MaxValue || days < int.MinValue) return false;
        result = new Interval((int)months, (int)days, micros);
        return true;
    }

    /// <summary>
    /// SQL-standard interval forms: <c>"1-2"</c> (year-month, optionally
    /// followed by a day-time tail) and <c>"3 04:05:06"</c> (day-time-only).
    /// Returns <see langword="false"/> when the input doesn't look like
    /// either shape so the caller can fall through to the verbose form.
    /// </summary>
    private static bool TryParseSqlStandard(ReadOnlySpan<char> input, out Interval result)
    {
        result = default;

        // Both forms permit a leading +/-.
        int i = 0;
        int sign = 1;
        if (i < input.Length && (input[i] == '+' || input[i] == '-'))
        {
            sign = input[i] == '-' ? -1 : 1;
            i++;
        }

        int digitsStart = i;
        while (i < input.Length && input[i] >= '0' && input[i] <= '9') i++;
        if (i == digitsStart) return false;
        long firstNum = long.Parse(input[digitsStart..i], CultureInfo.InvariantCulture);

        // Year-month: 'N-M' (digits, '-', digits). Must not be the verbose
        // negative-shorthand (which would be '-N <unit>', already consumed
        // its sign before the digits).
        if (i < input.Length && input[i] == '-')
        {
            int hyphen = i;
            i++;
            int mStart = i;
            while (i < input.Length && input[i] >= '0' && input[i] <= '9') i++;
            if (i == mStart) return false;
            long second = long.Parse(input[mStart..i], CultureInfo.InvariantCulture);

            long totalMonths = sign * (firstNum * MonthsPerYear + second);
            long days = 0;
            long micros = 0;

            // Optional trailing day-time component (whitespace + 'D HH:MM:SS').
            if (i < input.Length)
            {
                while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
                if (i >= input.Length) return false;
                if (!TryConsumeSqlStandardDayTime(input, ref i, sign, out long extraDays, out long extraMicros))
                {
                    return false;
                }
                days = extraDays;
                micros = extraMicros;
            }
            _ = hyphen;
            if (totalMonths > int.MaxValue || totalMonths < int.MinValue) return false;
            if (days > int.MaxValue || days < int.MinValue) return false;
            result = new Interval((int)totalMonths, (int)days, micros);
            return true;
        }

        // Day-time: 'N HH:MM:SS[.f]'. Must have at least one whitespace then a
        // time-of-day shape.
        if (i < input.Length && char.IsWhiteSpace(input[i]))
        {
            int saved = i;
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            // The next slice must be a pure time-of-day (digits colon digits ...).
            // Reject if the next char isn't a digit — verbose form would handle
            // 'N unit ...' shapes.
            if (i >= input.Length || input[i] < '0' || input[i] > '9')
            {
                return false;
            }
            // Look ahead to confirm a ':' follows the digit run; otherwise this
            // is 'N unit' verbose, not SQL-standard day-time.
            int peek = i;
            while (peek < input.Length && input[peek] >= '0' && input[peek] <= '9') peek++;
            if (peek >= input.Length || input[peek] != ':')
            {
                // Restore and let verbose handle it.
                _ = saved;
                return false;
            }
            int consumed = ParseTimeOfDay(input[i..], sign, out long todMicros);
            if (consumed < 0) return false;
            i += consumed;
            if (i != input.Length) return false;

            long days = sign * firstNum;
            if (days > int.MaxValue || days < int.MinValue) return false;
            result = new Interval(0, (int)days, todMicros);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Consumes a trailing day-time slice after a SQL-standard year-month
    /// prefix: <c>"D HH:MM:SS[.f]"</c>. Used by <see cref="TryParseSqlStandard"/>
    /// so a full year-month-day-time string parses in one pass.
    /// </summary>
    private static bool TryConsumeSqlStandardDayTime(
        ReadOnlySpan<char> input, ref int i, int sign,
        out long days, out long micros)
    {
        days = 0;
        micros = 0;
        if (i >= input.Length || input[i] < '0' || input[i] > '9') return false;

        int dStart = i;
        while (i < input.Length && input[i] >= '0' && input[i] <= '9') i++;
        if (!long.TryParse(input[dStart..i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long dValue))
        {
            return false;
        }

        // Optional whitespace + time-of-day tail.
        if (i < input.Length && char.IsWhiteSpace(input[i]))
        {
            while (i < input.Length && char.IsWhiteSpace(input[i])) i++;
            int consumed = ParseTimeOfDay(input[i..], sign, out long todMicros);
            if (consumed < 0) return false;
            i += consumed;
            micros = todMicros;
        }
        days = sign * dValue;
        return i == input.Length;
    }

    /// <summary>
    /// SQL-standard / Postgres interval qualifier — the trailing
    /// <c>YEAR</c>, <c>YEAR TO MONTH</c>, <c>DAY TO SECOND</c>, etc.,
    /// that disambiguates a bare-number literal (<c>'1'</c>) and may
    /// truncate finer-grained components from the result.
    /// </summary>
    public enum Qualifier
    {
        /// <summary>No qualifier — verbose / ISO / SQL-standard forms self-describe.</summary>
        None = 0,
        /// <summary>Year precision; sub-year fields are dropped.</summary>
        Year,
        /// <summary>Month precision; sub-month fields are dropped.</summary>
        Month,
        /// <summary>Day precision; sub-day fields are dropped.</summary>
        Day,
        /// <summary>Hour precision; sub-hour fields are dropped.</summary>
        Hour,
        /// <summary>Minute precision; sub-minute fields are dropped.</summary>
        Minute,
        /// <summary>Second precision; sub-second fields are kept (microsecond resolution).</summary>
        Second,
        /// <summary>Year-to-month span.</summary>
        YearToMonth,
        /// <summary>Day-to-hour span; minutes / seconds are dropped.</summary>
        DayToHour,
        /// <summary>Day-to-minute span; seconds are dropped.</summary>
        DayToMinute,
        /// <summary>Day-to-second span; full sub-day precision retained.</summary>
        DayToSecond,
        /// <summary>Hour-to-minute span; seconds are dropped.</summary>
        HourToMinute,
        /// <summary>Hour-to-second span; full sub-hour precision retained.</summary>
        HourToSecond,
        /// <summary>Minute-to-second span.</summary>
        MinuteToSecond,
    }

    /// <summary>
    /// Parses an interval literal with a Postgres trailing qualifier.
    /// The qualifier resolves an otherwise-ambiguous bare-number literal
    /// (<c>'1'</c> + <c>HOUR</c> → <c>01:00:00</c>) and truncates
    /// finer-grained components present in the parsed value
    /// (<c>'1 day 1 hour' + DAY</c> → <c>'1 day'</c>).
    /// </summary>
    public static bool TryParseWithQualifier(string text, Qualifier qualifier, out Interval result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        ReadOnlySpan<char> trimmed = text.AsSpan().Trim();

        // Bare numeric literal: '1', '-2.5', '0.5'. Apply the qualifier's
        // unit directly; reject when no qualifier is supplied.
        if (IsBareSignedNumeric(trimmed))
        {
            if (qualifier == Qualifier.None) return false;
            double value = double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture);
            result = FromBareNumberAndQualifier(value, qualifier);
            return true;
        }

        if (!TryParse(text, out Interval parsed)) return false;
        result = qualifier == Qualifier.None ? parsed : Truncate(parsed, qualifier);
        return true;
    }

    private static bool IsBareSignedNumeric(ReadOnlySpan<char> s)
    {
        int i = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        if (i >= s.Length) return false;
        bool sawDigit = false;
        bool sawDot = false;
        while (i < s.Length)
        {
            char c = s[i];
            if (c >= '0' && c <= '9') { sawDigit = true; i++; continue; }
            if (c == '.' && !sawDot) { sawDot = true; i++; continue; }
            return false;
        }
        return sawDigit;
    }

    private static Interval FromBareNumberAndQualifier(double value, Qualifier qualifier)
    {
        long months = 0;
        long days = 0;
        long micros = 0;
        switch (qualifier)
        {
            case Qualifier.Year:
            case Qualifier.YearToMonth:
                AddMonths(value * MonthsPerYear, ref months, ref days, ref micros);
                break;
            case Qualifier.Month:
                AddMonths(value, ref months, ref days, ref micros);
                break;
            case Qualifier.Day:
            case Qualifier.DayToHour:
            case Qualifier.DayToMinute:
            case Qualifier.DayToSecond:
                AddDays(value, ref days, ref micros);
                break;
            case Qualifier.Hour:
            case Qualifier.HourToMinute:
            case Qualifier.HourToSecond:
                micros = (long)Math.Round(value * MicrosPerHour, MidpointRounding.AwayFromZero);
                break;
            case Qualifier.Minute:
            case Qualifier.MinuteToSecond:
                micros = (long)Math.Round(value * MicrosPerMinute, MidpointRounding.AwayFromZero);
                break;
            case Qualifier.Second:
                micros = (long)Math.Round(value * MicrosPerSecond, MidpointRounding.AwayFromZero);
                break;
        }
        return new Interval((int)months, (int)days, micros);
    }

    /// <summary>
    /// Truncates a parsed interval to the precision span allowed by
    /// <paramref name="qualifier"/>. Components finer than the qualifier's
    /// lower bound are zeroed; coarser components are left intact.
    /// </summary>
    private static Interval Truncate(Interval iv, Qualifier qualifier)
    {
        bool keepMonths;
        bool keepDays;
        // Sub-day precision: how many of the four time fields (hour, minute,
        // second, microsecond) we keep.
        int subDayMicroMask = 0;
        switch (qualifier)
        {
            case Qualifier.Year:
                // Only the year portion of months survives.
                int years = iv.Months / MonthsPerYear;
                return new Interval(years * MonthsPerYear, 0, 0);
            case Qualifier.Month:
            case Qualifier.YearToMonth:
                keepMonths = true; keepDays = false; subDayMicroMask = 0; break;
            case Qualifier.Day:
                keepMonths = true; keepDays = true; subDayMicroMask = 0; break;
            case Qualifier.Hour:
            case Qualifier.DayToHour:
                keepMonths = true; keepDays = true; subDayMicroMask = 1; break;
            case Qualifier.Minute:
            case Qualifier.DayToMinute:
            case Qualifier.HourToMinute:
                keepMonths = true; keepDays = true; subDayMicroMask = 2; break;
            case Qualifier.Second:
            case Qualifier.DayToSecond:
            case Qualifier.HourToSecond:
            case Qualifier.MinuteToSecond:
                keepMonths = true; keepDays = true; subDayMicroMask = 3; break;
            default:
                return iv;
        }

        long months = keepMonths ? iv.Months : 0;
        long days = keepDays ? iv.Days : 0;
        long micros = subDayMicroMask switch
        {
            // hour-precision: drop minutes / seconds / micros below hour.
            1 => iv.Microseconds / MicrosPerHour * MicrosPerHour,
            2 => iv.Microseconds / MicrosPerMinute * MicrosPerMinute,
            3 => iv.Microseconds,
            _ => 0,
        };
        return new Interval((int)months, (int)days, micros);
    }

    /// <summary>
    /// Parses an <c>HH:MM:SS[.fffffff]</c> slice and returns the count of
    /// characters consumed, or <c>-1</c> on malformed input. Sign is applied
    /// to the resulting microseconds.
    /// </summary>
    private static int ParseTimeOfDay(ReadOnlySpan<char> slice, int sign, out long microsOut)
    {
        microsOut = 0;
        int j = 0;
        if (!ConsumeNumber(slice, ref j, out long hours)) return -1;
        if (j >= slice.Length || slice[j] != ':') return -1;
        j++;
        if (!ConsumeNumber(slice, ref j, out long minutes)) return -1;
        long seconds = 0;
        long fracMicros = 0;
        if (j < slice.Length && slice[j] == ':')
        {
            j++;
            if (!ConsumeNumber(slice, ref j, out seconds)) return -1;
            if (j < slice.Length && slice[j] == '.')
            {
                j++;
                int fracStart = j;
                while (j < slice.Length && slice[j] >= '0' && slice[j] <= '9') j++;
                int fracLen = j - fracStart;
                if (fracLen == 0) return -1;
                string fracText = slice[fracStart..j].ToString();
                if (!long.TryParse(fracText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long fracDigits))
                {
                    return -1;
                }
                // Pad / truncate to six digits to land in microseconds.
                if (fracLen < 6)
                {
                    for (int p = fracLen; p < 6; p++) fracDigits *= 10;
                }
                else if (fracLen > 6)
                {
                    for (int p = 6; p < fracLen; p++) fracDigits /= 10;
                }
                fracMicros = fracDigits;
            }
        }

        long totalMicros = checked(
            hours * MicrosPerHour
            + minutes * MicrosPerMinute
            + seconds * MicrosPerSecond
            + fracMicros);
        microsOut = sign * totalMicros;
        return j;
    }

    private static bool ConsumeNumber(ReadOnlySpan<char> slice, ref int j, out long value)
    {
        int start = j;
        while (j < slice.Length && slice[j] >= '0' && slice[j] <= '9') j++;
        if (j == start) { value = 0; return false; }
        string num = slice[start..j].ToString();
        return long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Applies a (signed) <paramref name="value"/> denominated in
    /// <paramref name="unit"/> to the three accumulators. Returns
    /// <see langword="false"/> for an unrecognised unit. Fractional
    /// values are carried down to the next-finer field (e.g.
    /// <c>1.5 days</c> = 1 day + 12 hours).
    /// </summary>
    private static bool ApplyUnit(
        string unit, double value, ref long months, ref long days, ref long micros)
    {
        switch (unit.ToLowerInvariant())
        {
            case "year":
            case "years":
            case "y":
                AddMonths(value * MonthsPerYear, ref months, ref days, ref micros);
                return true;
            case "month":
            case "months":
            case "mon":
            case "mons":
            case "mo":
                AddMonths(value, ref months, ref days, ref micros);
                return true;
            case "week":
            case "weeks":
            case "w":
                AddDays(value * 7.0, ref days, ref micros);
                return true;
            case "day":
            case "days":
            case "d":
                AddDays(value, ref days, ref micros);
                return true;
            case "hour":
            case "hours":
            case "hr":
            case "hrs":
            case "h":
                micros = checked(micros + (long)Math.Round(value * MicrosPerHour, MidpointRounding.AwayFromZero));
                return true;
            case "minute":
            case "minutes":
            case "min":
            case "mins":
            case "m":
                micros = checked(micros + (long)Math.Round(value * MicrosPerMinute, MidpointRounding.AwayFromZero));
                return true;
            case "second":
            case "seconds":
            case "sec":
            case "secs":
            case "s":
                micros = checked(micros + (long)Math.Round(value * MicrosPerSecond, MidpointRounding.AwayFromZero));
                return true;
            case "millisecond":
            case "milliseconds":
            case "ms":
                micros = checked(micros + (long)Math.Round(value * 1000.0, MidpointRounding.AwayFromZero));
                return true;
            case "microsecond":
            case "microseconds":
            case "us":
            case "µs":
                micros = checked(micros + (long)Math.Round(value, MidpointRounding.AwayFromZero));
                return true;
            default:
                return false;
        }
    }

    private static void AddMonths(double monthsValue, ref long months, ref long days, ref long micros)
    {
        double monthsWhole = Math.Truncate(monthsValue);
        double monthsFrac = monthsValue - monthsWhole;
        months = checked(months + (long)monthsWhole);
        if (monthsFrac != 0.0)
        {
            AddDays(monthsFrac * DaysPerMonth, ref days, ref micros);
        }
    }

    private static void AddDays(double daysValue, ref long days, ref long micros)
    {
        double daysWhole = Math.Truncate(daysValue);
        double daysFrac = daysValue - daysWhole;
        days = checked(days + (long)daysWhole);
        if (daysFrac != 0.0)
        {
            micros = checked(micros + (long)Math.Round(daysFrac * MicrosPerDay, MidpointRounding.AwayFromZero));
        }
    }
}
