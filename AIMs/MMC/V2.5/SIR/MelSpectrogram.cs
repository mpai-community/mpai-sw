using System;
using System.Collections.Generic;

namespace Mpai.Mmc.Sir;

// ---------------------------------------------------------------------------
//  MelSpectrogram - the log-Mel filterbank ("fbank") front-end that ECAPA
//  expects. The 3D-Speaker ecapa-tdnn.onnx input is [1, T, 80]: T frames of
//  80-dim log-Mel features.
//
//  Spec (from the ECAPA / WeSpeaker / 3D-Speaker lineage, Kaldi-style fbank):
//    16 kHz mono, 25 ms window (400 samples), 10 ms hop (160 samples),
//    80 mel bins, fmin 20 Hz, fmax 7600 Hz, natural-log energies,
//    per-utterance mean normalisation.
//
//  Some conventions (window shape, mel formula, dithering, pre-emphasis) vary
//  by toolkit and are only verifiable by whether the resulting embeddings
//  DISCRIMINATE (same-speaker high, different-speaker low). This implements the
//  common Kaldi defaults; the discrimination test tells us if they are right.
// ---------------------------------------------------------------------------
public sealed class MelSpectrogram
{
    private const int   SampleRate = 16000;
    private const int   WinLength  = 400;    // 25 ms
    private const int   HopLength  = 160;    // 10 ms
    private const int   NumMel     = 80;
    private const double FMin       = 20.0;
    private const double FMax       = 7600.0;

    private readonly int _nFft;              // next pow2 >= WinLength
    private readonly double[] _window;       // Povey window (Kaldi default)
    private readonly double[][] _melBank;    // [NumMel][nFft/2+1]

    public MelSpectrogram()
    {
        _nFft = 1; while (_nFft < WinLength) _nFft <<= 1;   // 512
        _window = PoveyWindow(WinLength);
        _melBank = BuildMelBank(NumMel, _nFft, SampleRate, FMin, FMax);
    }

    // samples: mono float PCM in [-1,1] at 16 kHz. Returns [frames][80] log-Mel,
    // mean-normalised per utterance.
    public float[][] Compute(float[] samples)
    {
        int numFrames = samples.Length < WinLength
            ? 0
            : 1 + (samples.Length - WinLength) / HopLength;
        if (numFrames <= 0) return Array.Empty<float[]>();

        int bins = _nFft / 2 + 1;
        var feats = new float[numFrames][];
        var re = new double[_nFft];
        var im = new double[_nFft];

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * HopLength;

            // Windowed frame, with DC removal + pre-emphasis (Kaldi defaults:
            // remove_dc_offset=true, preemphasis=0.97).
            double mean = 0.0;
            for (int i = 0; i < WinLength; i++) mean += samples[start + i];
            mean /= WinLength;

            // DC-removed frame first.
            var dc = new double[WinLength];
            for (int i = 0; i < WinLength; i++) dc[i] = samples[start + i] - mean;

            // Pre-emphasis: y[i] = x[i] - 0.97*x[i-1]; y[0] = x[0] - 0.97*x[0].
            var frame = new double[_nFft];
            for (int i = 0; i < WinLength; i++)
            {
                double emph = i == 0 ? dc[0] - 0.97 * dc[0] : dc[i] - 0.97 * dc[i - 1];
                frame[i] = emph * _window[i];
            }

            Array.Copy(frame, re, _nFft);
            Array.Clear(im, 0, _nFft);
            Fft(re, im);

            var mel = new float[NumMel];
            for (int m = 0; m < NumMel; m++)
            {
                double e = 0.0;
                var filt = _melBank[m];
                for (int k = 0; k < bins; k++)
                {
                    double power = re[k] * re[k] + im[k] * im[k];
                    e += power * filt[k];
                }
                mel[m] = (float)Math.Log(Math.Max(e, 1e-10));
            }
            feats[f] = mel;
        }

        MeanNormalise(feats);
        return feats;
    }

    // Pad or truncate to exactly `target` frames (ECAPA input fixes T=360).
    public static float[][] FixFrames(float[][] feats, int target)
    {
        if (feats.Length == target) return feats;
        var outp = new float[target][];
        for (int t = 0; t < target; t++)
        {
            if (t < feats.Length) outp[t] = feats[t];
            else outp[t] = new float[feats.Length > 0 ? feats[0].Length : 80]; // zero-pad
        }
        return outp;
    }

    private static void MeanNormalise(float[][] feats)
    {
        if (feats.Length == 0) return;
        int d = feats[0].Length;
        var mean = new double[d];
        foreach (var fr in feats) for (int i = 0; i < d; i++) mean[i] += fr[i];
        for (int i = 0; i < d; i++) mean[i] /= feats.Length;
        foreach (var fr in feats) for (int i = 0; i < d; i++) fr[i] -= (float)mean[i];
    }

    // Povey window: (0.5 - 0.5*cos(2*pi*n/(N-1)))^0.85  (Kaldi default).
    private static double[] PoveyWindow(int n)
    {
        var w = new double[n];
        for (int i = 0; i < n; i++)
            w[i] = Math.Pow(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1)), 0.85);
        return w;
    }

    private static double HzToMel(double hz) => 1127.0 * Math.Log(1.0 + hz / 700.0);
    private static double MelToHz(double mel) => 700.0 * (Math.Exp(mel / 1127.0) - 1.0);

    private static double[][] BuildMelBank(int numMel, int nFft, int sr, double fmin, double fmax)
    {
        int bins = nFft / 2 + 1;
        var bank = new double[numMel][];
        double melMin = HzToMel(fmin), melMax = HzToMel(fmax);
        var points = new double[numMel + 2];
        for (int i = 0; i < points.Length; i++)
        {
            double mel = melMin + (melMax - melMin) * i / (numMel + 1);
            points[i] = MelToHz(mel);
        }
        double binWidth = (double)sr / nFft;
        for (int m = 0; m < numMel; m++)
        {
            bank[m] = new double[bins];
            double left = points[m], centre = points[m + 1], right = points[m + 2];
            for (int k = 0; k < bins; k++)
            {
                double freq = k * binWidth;
                double val = 0.0;
                if (freq >= left && freq <= centre) val = (freq - left) / (centre - left);
                else if (freq > centre && freq <= right) val = (right - freq) / (right - centre);
                bank[m][k] = Math.Max(0.0, val);
            }
        }
        return bank;
    }

    // In-place iterative radix-2 Cooley-Tukey FFT (nFft is a power of two).
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2.0 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cwr = 1.0, cwi = 0.0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * cwr - im[b] * cwi;
                    double ti = re[b] * cwi + im[b] * cwr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr;        im[a] += ti;
                    double ncwr = cwr * wr - cwi * wi;
                    cwi = cwr * wi + cwi * wr; cwr = ncwr;
                }
            }
        }
    }
}
