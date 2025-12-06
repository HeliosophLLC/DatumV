namespace DatumIngest.Models.Onnx.Whisper;

/// <summary>
/// Self-contained FFT for the Whisper audio preprocessor. Whisper's STFT
/// uses <c>n_fft = 400</c>, which is not a power of two, so radix-2 alone
/// won't work. We implement Bluestein's algorithm on top of a radix-2 base
/// to handle arbitrary FFT sizes — at the cost of one zero-padded length-M
/// radix-2 FFT (M ≥ 2N-1, rounded up to a power of two) per frame.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope.</strong> Designed for the single hot path Whisper needs
/// (N=400 → M=1024 internal). Precomputes the Bluestein chirp + the FFT of
/// the chirp once at construction so every per-frame call is just two
/// length-M FFTs plus pointwise multiplies. ~50 µs per frame on warm
/// caches; 3000 frames per 30s audio clip = ~150ms overhead vs the encoder
/// dispatch, which is the right scale.
/// </para>
/// <para>
/// <strong>Real-input optimisation.</strong> Audio frames are real-valued
/// after windowing. We don't bother with a real-FFT specialisation — the
/// imaginary part is zero on input, the savings are ~25%, and the code
/// stays simpler this way.
/// </para>
/// </remarks>
internal sealed class WhisperFft
{
    /// <summary>The FFT length the caller requested (e.g. 400).</summary>
    public int N { get; }

    /// <summary>Internal Bluestein length (next power of 2 ≥ 2N-1).</summary>
    private readonly int _m;

    /// <summary>Per-bin chirp coefficients <c>w[k] = exp(-iπk²/N)</c>, length N.</summary>
    private readonly float[] _chirpReal;
    private readonly float[] _chirpImag;

    /// <summary>FFT of the conjugate-chirp sequence b[k], length M (precomputed).</summary>
    private readonly float[] _bFftReal;
    private readonly float[] _bFftImag;

    /// <summary>Twiddle tables for the length-M radix-2 FFT (cos / sin per stage).</summary>
    private readonly float[] _twiddleCos;
    private readonly float[] _twiddleSin;

    /// <summary>Bit-reversal permutation table for length-M.</summary>
    private readonly int[] _bitReverse;

    /// <summary>
    /// Constructs an FFT engine for length <paramref name="n"/>. Precomputes
    /// the chirp sequences and twiddle tables. Constructing once and reusing
    /// across frames is intentional — none of the precomputed data depends
    /// on the input.
    /// </summary>
    public WhisperFft(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        N = n;
        // M = smallest power of 2 ≥ 2N - 1. For N=400, M=1024.
        int m = 1;
        while (m < 2 * n - 1) m <<= 1;
        _m = m;

        _chirpReal = new float[n];
        _chirpImag = new float[n];
        for (int k = 0; k < n; k++)
        {
            // w[k] = exp(-i * π * k² / N)
            double phase = -Math.PI * (double)k * k / n;
            _chirpReal[k] = (float)Math.Cos(phase);
            _chirpImag[k] = (float)Math.Sin(phase);
        }

        // Build the b sequence (zero-padded to M, with both ends mirroring
        // the conjugate of the chirp).
        float[] bReal = new float[m];
        float[] bImag = new float[m];
        bReal[0] = _chirpReal[0];
        bImag[0] = -_chirpImag[0];
        for (int k = 1; k < n; k++)
        {
            bReal[k] = _chirpReal[k];
            bImag[k] = -_chirpImag[k];
            bReal[m - k] = _chirpReal[k];
            bImag[m - k] = -_chirpImag[k];
        }

        // Twiddle tables and bit-reverse for the length-M FFT.
        _twiddleCos = new float[m / 2];
        _twiddleSin = new float[m / 2];
        for (int k = 0; k < m / 2; k++)
        {
            double phase = -2.0 * Math.PI * k / m;
            _twiddleCos[k] = (float)Math.Cos(phase);
            _twiddleSin[k] = (float)Math.Sin(phase);
        }
        _bitReverse = BuildBitReverseTable(m);

        // Precompute FFT(b) — invariant of input, used in every call.
        Radix2FftInPlace(bReal, bImag, forward: true);
        _bFftReal = bReal;
        _bFftImag = bImag;
    }

    /// <summary>
    /// Computes the length-N FFT of <paramref name="real"/>+<paramref name="imag"/>
    /// in-place via Bluestein's algorithm. Output is the standard DFT
    /// ordering — bin 0 is DC, bin N/2 is Nyquist.
    /// </summary>
    public void Forward(Span<float> real, Span<float> imag)
    {
        if (real.Length != N || imag.Length != N)
        {
            throw new ArgumentException($"Input lengths must equal N ({N}).");
        }

        int m = _m;
        // Step 1: a[k] = x[k] * w[k], zero-padded to length M.
        float[] aReal = new float[m];
        float[] aImag = new float[m];
        for (int k = 0; k < N; k++)
        {
            float xr = real[k], xi = imag[k];
            float wr = _chirpReal[k], wi = _chirpImag[k];
            aReal[k] = xr * wr - xi * wi;
            aImag[k] = xr * wi + xi * wr;
        }

        // Step 2: FFT(a), pointwise multiply with FFT(b), inverse FFT.
        Radix2FftInPlace(aReal, aImag, forward: true);
        for (int k = 0; k < m; k++)
        {
            float ar = aReal[k], ai = aImag[k];
            float br = _bFftReal[k], bi = _bFftImag[k];
            aReal[k] = ar * br - ai * bi;
            aImag[k] = ar * bi + ai * br;
        }
        Radix2FftInPlace(aReal, aImag, forward: false);

        // Step 3: multiply by w[k] again, take first N samples.
        for (int k = 0; k < N; k++)
        {
            float cr = aReal[k], ci = aImag[k];
            float wr = _chirpReal[k], wi = _chirpImag[k];
            real[k] = cr * wr - ci * wi;
            imag[k] = cr * wi + ci * wr;
        }
    }

    /// <summary>
    /// In-place Cooley-Tukey radix-2 FFT for length-<see cref="_m"/> arrays.
    /// <paramref name="forward"/> picks DFT vs IDFT (the latter normalises
    /// by 1/M and conjugates the twiddles).
    /// </summary>
    private void Radix2FftInPlace(float[] real, float[] imag, bool forward)
    {
        int m = _m;

        // Bit-reverse permutation.
        for (int i = 0; i < m; i++)
        {
            int j = _bitReverse[i];
            if (j > i)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imag[i], imag[j]) = (imag[j], imag[i]);
            }
        }

        // Butterfly stages. Stage size doubles each pass: 2, 4, 8, ..., M.
        for (int size = 2; size <= m; size <<= 1)
        {
            int half = size >> 1;
            int twiddleStride = m / size;

            for (int start = 0; start < m; start += size)
            {
                for (int k = 0; k < half; k++)
                {
                    int twiddleIdx = k * twiddleStride;
                    float wr = _twiddleCos[twiddleIdx];
                    float wi = forward ? _twiddleSin[twiddleIdx] : -_twiddleSin[twiddleIdx];

                    int evenIdx = start + k;
                    int oddIdx = evenIdx + half;

                    float er = real[evenIdx], ei = imag[evenIdx];
                    float or_ = real[oddIdx], oi = imag[oddIdx];

                    // t = w * odd
                    float tr = wr * or_ - wi * oi;
                    float ti = wr * oi + wi * or_;

                    real[evenIdx] = er + tr;
                    imag[evenIdx] = ei + ti;
                    real[oddIdx] = er - tr;
                    imag[oddIdx] = ei - ti;
                }
            }
        }

        // Inverse FFT normalisation.
        if (!forward)
        {
            float inv = 1f / m;
            for (int i = 0; i < m; i++)
            {
                real[i] *= inv;
                imag[i] *= inv;
            }
        }
    }

    private static int[] BuildBitReverseTable(int m)
    {
        int bits = 0;
        while ((1 << bits) < m) bits++;
        int[] table = new int[m];
        for (int i = 0; i < m; i++)
        {
            int rev = 0;
            int v = i;
            for (int b = 0; b < bits; b++)
            {
                rev = (rev << 1) | (v & 1);
                v >>= 1;
            }
            table[i] = rev;
        }
        return table;
    }
}
