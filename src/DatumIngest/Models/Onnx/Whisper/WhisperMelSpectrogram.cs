namespace DatumIngest.Models.Onnx.Whisper;

/// <summary>
/// Computes the log-mel spectrogram Whisper's encoder expects: 80 mel
/// channels × 3000 frames per 30-second audio clip at 16 kHz. Mirrors
/// HuggingFace's <c>WhisperFeatureExtractor</c>: Slaney mel filterbank,
/// Hann window, center-padded STFT with reflective boundaries, log10
/// power, and the Whisper-specific clip + offset to land roughly in
/// [-1, 1].
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single-purpose.</strong> Hardcoded for the Whisper params
/// (16kHz, n_fft=400, hop=160, n_mels=80). Larger Whisper variants
/// (large-v3 uses n_mels=128) need a separate instance with the
/// constructor-supplied <c>nMels</c>.
/// </para>
/// <para>
/// <strong>Output layout.</strong> The encoder expects shape
/// <c>[batch, n_mels, n_frames]</c>. We return a flat <c>float[]</c>
/// of length <c>n_mels * n_frames</c> in mel-major / frame-minor order
/// (i.e. <c>result[mel * n_frames + frame]</c>) — caller wraps in a
/// <c>DenseTensor</c> with the leading batch dim added.
/// </para>
/// </remarks>
internal sealed class WhisperMelSpectrogram
{
    /// <summary>Audio sample rate Whisper expects, in Hz.</summary>
    public const int SampleRate = 16000;

    /// <summary>STFT window length / FFT input length.</summary>
    public const int NFft = 400;

    /// <summary>Hop between adjacent STFT frames, in samples.</summary>
    public const int HopLength = 160;

    /// <summary>Maximum audio length the encoder accepts (30s @ 16kHz).</summary>
    public const int NumSamples = 480000;

    /// <summary>Number of STFT frames the encoder consumes per clip.</summary>
    public const int NumFrames = 3000;

    /// <summary>Number of mel channels (80 for tiny/base/small/medium).</summary>
    public int NumMels { get; }

    private readonly WhisperFft _fft;
    private readonly float[] _hannWindow;

    /// <summary>
    /// Mel filterbank weights, layout <c>[mel, bin]</c> as a flat array
    /// (<c>_filterbank[mel * NumBins + bin]</c>). Computed once at
    /// construction. <c>NumBins = NFft / 2 + 1 = 201</c>.
    /// </summary>
    private readonly float[] _filterbank;

    /// <summary>FFT bins covered by the mel filterbank (<c>NFft/2 + 1</c>).</summary>
    public const int NumBins = NFft / 2 + 1;

    public WhisperMelSpectrogram(int nMels = 80)
    {
        if (nMels <= 0) throw new ArgumentOutOfRangeException(nameof(nMels));
        NumMels = nMels;
        _fft = new WhisperFft(NFft);

        _hannWindow = new float[NFft];
        // periodic Hann (matches torch.hann_window default + librosa fft_window)
        for (int i = 0; i < NFft; i++)
        {
            _hannWindow[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / NFft));
        }

        _filterbank = BuildSlaneyMelFilterbank(SampleRate, NFft, nMels);
    }

    /// <summary>
    /// Audio samples → log-mel features. Input is mono 16kHz float in
    /// [-1, 1]; samples beyond <see cref="NumSamples"/> are truncated,
    /// shorter clips are zero-padded.
    /// </summary>
    public float[] ComputeMel(ReadOnlySpan<float> audioSamples)
    {
        // Pad / truncate to exactly 30s of 16kHz audio.
        float[] padded = new float[NumSamples];
        int copyLen = Math.Min(audioSamples.Length, NumSamples);
        audioSamples[..copyLen].CopyTo(padded);

        int halfFft = NFft / 2;
        float[] melFlat = new float[NumMels * NumFrames];

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
                    sampleIdx = Math.Clamp(sampleIdx, 0, NumSamples - 1);
                }

                frameReal[i] = padded[sampleIdx] * _hannWindow[i];
                frameImag[i] = 0f;
            }

            _fft.Forward(frameReal, frameImag);

            // Power spectrum × filterbank (only the first NumBins bins are real;
            // the rest are conjugate-symmetric and unneeded for power).
            for (int mel = 0; mel < NumMels; mel++)
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
                melFlat[mel * NumFrames + frameIdx] = (float)Math.Log10(Math.Max(sum, 1e-10));
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
    /// Build a Slaney-style mel filterbank — librosa's default and what
    /// HuggingFace's <c>WhisperFeatureExtractor</c> uses. Triangular
    /// filters in the Hz domain, normalised so each filter's area sums
    /// to <c>2 / (right_freq - left_freq)</c>. Returned as flat
    /// row-major <c>[mel * NumBins + bin]</c>.
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

    /// <summary>
    /// Slaney mel scale (htk=False). Linear below 1000 Hz, logarithmic above.
    /// </summary>
    private static double HzToSlaneyMel(double freq)
    {
        const double minLogHz = 1000.0;
        const double minLogMel = 15.0;          // == 3 * 1000 / 200
        double logStep = Math.Log(6.4) / 27.0;  // = ln(6.4) / 27

        if (freq < minLogHz)
        {
            return 3.0 * freq / 200.0;
        }
        return minLogMel + Math.Log(freq / minLogHz) / logStep;
    }

    private static double SlaneyMelToHz(double mel)
    {
        const double minLogMel = 15.0;
        const double minLogHz = 1000.0;
        double logStep = Math.Log(6.4) / 27.0;

        if (mel < minLogMel)
        {
            return mel * 200.0 / 3.0;
        }
        return minLogHz * Math.Exp(logStep * (mel - minLogMel));
    }
}
