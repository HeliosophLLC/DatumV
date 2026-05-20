using System.Collections.Concurrent;

using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions.Scalar.Activation;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Audio;

/// <summary>
/// <c>audio_to_log_mel(samples Float32[], n_mels Int32) → Float32[]</c>.
/// Computes the log-mel spectrogram that Whisper-family encoders expect:
/// <c>n_mels × 3000</c> Float32 features per 30-second 16 kHz mono clip.
/// Hardcoded for Whisper params (sample rate 16 kHz, n_fft 400, hop 160,
/// Slaney mel filterbank, log10, max-8 dB clip, shift+scale to ~[-1, 1]).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Input contract.</strong> <c>samples</c> is mono 16 kHz Float32 in
/// [-1, 1] — produced by <c>audio_samples(16000, audio_to_mono(clip))</c>.
/// Shorter clips are zero-padded; longer clips are truncated to the 30 s
/// encoder context.
/// </para>
/// <para>
/// <strong>Output layout.</strong> Flat row-major <c>[n_mels, 3000]</c>
/// in mel-major / frame-minor order — <c>result[mel * 3000 + frame]</c>.
/// Feed straight into <c>infer('encoder', mel, [1, n_mels, 3000])</c>.
/// </para>
/// <para>
/// <strong>n_mels.</strong> 80 for Whisper tiny / base / small / medium;
/// 128 for large-v3. Cached per-(n_mels) builder instance avoids
/// reconstructing the FFT chirp + filterbank on every call (constant
/// data, only depends on n_mels at the Whisper-fixed sample rate / FFT
/// size).
/// </para>
/// <para>
/// <strong>Algorithm.</strong> Center-padded STFT with reflective
/// boundaries, periodic Hann window, Bluestein FFT (n_fft = 400 isn't
/// power-of-two), Slaney mel filterbank with the librosa / HuggingFace
/// normalisation, log10 floored at 1e-10, then Whisper's max-8 dB clip
/// plus a (v + 4) / 4 shift to land roughly in [-1, 1].
/// </para>
/// </remarks>
public sealed class AudioToLogMelFunction : IFunction, IScalarFunction
{
    private static readonly ConcurrentDictionary<int, MelExtractor> _cache = new();

    /// <inheritdoc />
    public static string Name => "audio_to_log_mel";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Converts mono 16 kHz Float32 audio samples to the Whisper log-mel spectrogram " +
        "(n_mels * 3000 Float32 features, mel-major). Pads / truncates to the 30 s encoder " +
        "context. n_mels = 80 for tiny/base/small/medium, 128 for large-v3.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("samples", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("n_mels",  DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.Float32))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AudioToLogMelFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.Float32));
        }
        if (!args[1].TryToInt32(out int nMels))
        {
            throw new FunctionArgumentException(Name,
                $"n_mels of kind {args[1].Kind} could not be widened to Int32.");
        }
        if (nMels <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"n_mels must be positive; got {nMels}.");
        }

        float[] samples = ActivationOps.ReadFloat32Array(args[0]);
        MelExtractor extractor = _cache.GetOrAdd(nMels, n => new MelExtractor(n));
        float[] mel = extractor.ComputeMel(samples);
        return new ValueTask<ValueRef>(ValueRef.FromPrimitiveArray(mel, DataKind.Float32));
    }
}

/// <summary>
/// Whisper log-mel feature extractor — STFT + Slaney mel filterbank +
/// log10 + Whisper-specific clip / shift. All Whisper-fixed parameters
/// (sample rate 16 kHz, n_fft 400, hop 160) are hardcoded; only n_mels
/// varies between model sizes. One instance per n_mels, cached process-wide
/// by <see cref="AudioToLogMelFunction"/>.
/// </summary>
internal sealed class MelExtractor
{
    /// <summary>Audio sample rate Whisper expects, in Hz.</summary>
    internal const int SampleRate = 16000;

    /// <summary>STFT window length / FFT input length.</summary>
    internal const int NFft = 400;

    /// <summary>Hop between adjacent STFT frames, in samples.</summary>
    internal const int HopLength = 160;

    /// <summary>Maximum audio length the encoder accepts (30 s @ 16 kHz).</summary>
    internal const int NumSamples = 480000;

    /// <summary>Number of STFT frames the encoder consumes per clip.</summary>
    internal const int NumFrames = 3000;

    /// <summary>FFT bins covered by the mel filterbank (NFft / 2 + 1).</summary>
    internal const int NumBins = NFft / 2 + 1;

    private readonly int _nMels;
    private readonly BluesteinFft _fft;
    private readonly float[] _hannWindow;

    /// <summary>
    /// Mel filterbank weights, layout [mel, bin] flat
    /// (<c>_filterbank[mel * NumBins + bin]</c>). Computed once.
    /// </summary>
    private readonly float[] _filterbank;

    internal MelExtractor(int nMels)
    {
        _nMels = nMels;
        _fft = new BluesteinFft(NFft);

        _hannWindow = new float[NFft];
        // periodic Hann (matches torch.hann_window default + librosa fft_window).
        for (int i = 0; i < NFft; i++)
        {
            _hannWindow[i] = (float)(0.5 - 0.5 * System.Math.Cos(2.0 * System.Math.PI * i / NFft));
        }

        _filterbank = BuildSlaneyMelFilterbank(SampleRate, NFft, nMels);
    }

    /// <summary>
    /// Audio samples (16 kHz mono, [-1, 1]) → flat log-mel features of
    /// length <c>nMels * 3000</c> in mel-major order.
    /// </summary>
    internal float[] ComputeMel(ReadOnlySpan<float> audioSamples)
    {
        // Pad / truncate to exactly 30 s of 16 kHz audio.
        float[] padded = new float[NumSamples];
        int copyLen = System.Math.Min(audioSamples.Length, NumSamples);
        audioSamples[..copyLen].CopyTo(padded);

        int halfFft = NFft / 2;
        float[] melFlat = new float[_nMels * NumFrames];

        // Per-frame scratch (reused across iterations).
        float[] frameReal = new float[NFft];
        float[] frameImag = new float[NFft];

        for (int frameIdx = 0; frameIdx < NumFrames; frameIdx++)
        {
            int centerSample = frameIdx * HopLength;
            int frameStart = centerSample - halfFft;

            // Window with reflective boundary handling. HF Transformers'
            // spectrogram defaults to center=True + pad_mode='reflect'.
            for (int i = 0; i < NFft; i++)
            {
                int sampleIdx = frameStart + i;
                if (sampleIdx < 0)
                {
                    sampleIdx = -sampleIdx;
                }
                else if (sampleIdx >= NumSamples)
                {
                    sampleIdx = 2 * NumSamples - sampleIdx - 2;
                }
                if ((uint)sampleIdx >= NumSamples)
                {
                    // Defensive clamp for very short clips after reflection.
                    sampleIdx = System.Math.Clamp(sampleIdx, 0, NumSamples - 1);
                }

                frameReal[i] = padded[sampleIdx] * _hannWindow[i];
                frameImag[i] = 0f;
            }

            _fft.Forward(frameReal, frameImag);

            // Power spectrum × filterbank (only the first NumBins bins are real;
            // the rest are conjugate-symmetric and unneeded for power).
            for (int mel = 0; mel < _nMels; mel++)
            {
                float sum = 0f;
                int filterRowOffset = mel * NumBins;
                for (int bin = 0; bin < NumBins; bin++)
                {
                    float weight = _filterbank[filterRowOffset + bin];
                    if (weight == 0f) continue;
                    float re = frameReal[bin];
                    float im = frameImag[bin];
                    float power = re * re + im * im;
                    sum += weight * power;
                }
                // log10 with floor to dodge -inf.
                melFlat[mel * NumFrames + frameIdx] =
                    (float)System.Math.Log10(System.Math.Max(sum, 1e-10));
            }
        }

        // Whisper-specific post: clip at max-8 dB, shift+scale to ~[-1, 1].
        float maxVal = float.NegativeInfinity;
        for (int i = 0; i < melFlat.Length; i++)
        {
            if (melFlat[i] > maxVal) maxVal = melFlat[i];
        }
        float floor = maxVal - 8.0f;
        for (int i = 0; i < melFlat.Length; i++)
        {
            float v = melFlat[i];
            if (v < floor) v = floor;
            melFlat[i] = (v + 4.0f) / 4.0f;
        }

        return melFlat;
    }

    /// <summary>
    /// Slaney-style mel filterbank — librosa's default and what
    /// HuggingFace's WhisperFeatureExtractor uses. Triangular filters in
    /// the Hz domain, normalised so each filter's area sums to
    /// <c>2 / (right_freq - left_freq)</c>.
    /// </summary>
    private static float[] BuildSlaneyMelFilterbank(int sampleRate, int nFft, int nMels)
    {
        int nBins = nFft / 2 + 1;
        double maxFreq = sampleRate / 2.0;

        double melMin = HzToSlaneyMel(0.0);
        double melMax = HzToSlaneyMel(maxFreq);

        // Linspace nMels+2 mel points (including the two band edges).
        double[] melPts = new double[nMels + 2];
        for (int i = 0; i < melPts.Length; i++)
        {
            melPts[i] = melMin + (melMax - melMin) * i / (nMels + 1);
        }

        // Convert to Hz, then to FFT-bin coordinates (continuous; not floored).
        double[] freqPts = new double[melPts.Length];
        double[] binPts = new double[melPts.Length];
        for (int i = 0; i < melPts.Length; i++)
        {
            freqPts[i] = SlaneyMelToHz(melPts[i]);
            binPts[i] = freqPts[i] * nFft / sampleRate;
        }

        float[] fb = new float[nMels * nBins];
        for (int m = 0; m < nMels; m++)
        {
            double left = binPts[m];
            double center = binPts[m + 1];
            double right = binPts[m + 2];
            double slaneyNorm = 2.0 / (freqPts[m + 2] - freqPts[m]);

            for (int k = 0; k < nBins; k++)
            {
                double weight;
                if (k <= left || k >= right)
                {
                    weight = 0;
                }
                else if (k <= center)
                {
                    weight = (k - left) / (center - left);
                }
                else
                {
                    weight = (right - k) / (right - center);
                }
                fb[m * nBins + k] = (float)(weight * slaneyNorm);
            }
        }
        return fb;
    }

    /// <summary>Slaney mel scale (htk=False). Linear below 1000 Hz, log above.</summary>
    private static double HzToSlaneyMel(double freq)
    {
        const double minLogHz = 1000.0;
        const double minLogMel = 15.0;          // == 3 * 1000 / 200
        double logStep = System.Math.Log(6.4) / 27.0;

        if (freq < minLogHz)
        {
            return 3.0 * freq / 200.0;
        }
        return minLogMel + System.Math.Log(freq / minLogHz) / logStep;
    }

    private static double SlaneyMelToHz(double mel)
    {
        const double minLogMel = 15.0;
        const double minLogHz = 1000.0;
        double logStep = System.Math.Log(6.4) / 27.0;

        if (mel < minLogMel)
        {
            return mel * 200.0 / 3.0;
        }
        return minLogHz * System.Math.Exp(logStep * (mel - minLogMel));
    }
}

/// <summary>
/// Bluestein's algorithm on top of a radix-2 base. Handles arbitrary FFT
/// sizes — required because Whisper's n_fft = 400 isn't a power of two.
/// Precomputes the chirp + FFT(b) sequence once at construction so every
/// per-frame call is just two length-M FFTs plus pointwise multiplies.
/// </summary>
internal sealed class BluesteinFft
{
    /// <summary>The FFT length the caller requested (e.g. 400).</summary>
    private readonly int _n;

    /// <summary>Internal Bluestein length (next power of 2 ≥ 2N-1).</summary>
    private readonly int _m;

    private readonly float[] _chirpReal;
    private readonly float[] _chirpImag;
    private readonly float[] _bFftReal;
    private readonly float[] _bFftImag;
    private readonly float[] _twiddleCos;
    private readonly float[] _twiddleSin;
    private readonly int[] _bitReverse;

    internal BluesteinFft(int n)
    {
        if (n <= 0) throw new ArgumentOutOfRangeException(nameof(n));
        _n = n;
        // M = smallest power of 2 ≥ 2N - 1. For N = 400, M = 1024.
        int m = 1;
        while (m < 2 * n - 1) m <<= 1;
        _m = m;

        _chirpReal = new float[n];
        _chirpImag = new float[n];
        for (int k = 0; k < n; k++)
        {
            // w[k] = exp(-i * π * k² / N)
            double phase = -System.Math.PI * (double)k * k / n;
            _chirpReal[k] = (float)System.Math.Cos(phase);
            _chirpImag[k] = (float)System.Math.Sin(phase);
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

        _twiddleCos = new float[m / 2];
        _twiddleSin = new float[m / 2];
        for (int k = 0; k < m / 2; k++)
        {
            double phase = -2.0 * System.Math.PI * k / m;
            _twiddleCos[k] = (float)System.Math.Cos(phase);
            _twiddleSin[k] = (float)System.Math.Sin(phase);
        }
        _bitReverse = BuildBitReverseTable(m);

        // Precompute FFT(b) — invariant of input, used in every call.
        Radix2FftInPlace(bReal, bImag, forward: true);
        _bFftReal = bReal;
        _bFftImag = bImag;
    }

    /// <summary>
    /// Length-N FFT of (real + i·imag), in-place. Output is standard DFT
    /// ordering: bin 0 is DC, bin N/2 is Nyquist.
    /// </summary>
    internal void Forward(Span<float> real, Span<float> imag)
    {
        if (real.Length != _n || imag.Length != _n)
        {
            throw new ArgumentException($"Input lengths must equal N ({_n}).");
        }

        int m = _m;
        // Step 1: a[k] = x[k] * w[k], zero-padded to length M.
        float[] aReal = new float[m];
        float[] aImag = new float[m];
        for (int k = 0; k < _n; k++)
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
        for (int k = 0; k < _n; k++)
        {
            float cr = aReal[k], ci = aImag[k];
            float wr = _chirpReal[k], wi = _chirpImag[k];
            real[k] = cr * wr - ci * wi;
            imag[k] = cr * wi + ci * wr;
        }
    }

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

        // Butterfly stages.
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
