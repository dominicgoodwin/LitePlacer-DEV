using System;
using System.Collections.Generic;

namespace LitePlacer
{
    // Finds tape sprocket holes (or any circle of known size) from the circular intensity edge at the rim.
    // Works regardless of what shows through the hole (bright bed, dark bed, textured metal), because it
    // uses only the rim, not the interior brightness. No fixed grey threshold: the edge threshold is
    // found by Otsu's method on the gradient magnitude of each frame.
    //
    // Steps (all on a grey byte image):
    //   1. Gaussian blur (sigma 1) -> Sobel gradient -> edge pixels = magnitude > Otsu(magnitude)
    //   2. Fixed-radius Hough: each edge pixel votes for a centre at +/- r along its gradient direction,
    //      for a few radii around the expected one. Accumulator smoothed, local maxima = candidates.
    //   3. Each candidate: refit on the edge pixels within a narrow band of the circle (Pratt fit, twice),
    //      then measure rim coverage (fraction of 36 angular bins with edge support) and the largest
    //      contiguous gap in the rim.
    //   4. Accept if radius within tolerance AND coverage >= MinCoverage AND gap <= MaxGap.
    //      Coverage rejects arcs; the gap test rejects rounded pocket corners, which are arcs with
    //      one big missing section, while a shadowed real hole loses scattered bins.
    //
    // Prototyped offline (python, same arithmetic) on saved frames of clear, black, white paper and
    // homing-mark holes; one parameter set found every hole on every tape.
    public static class HoleFinder
    {
        public class Hole
        {
            public double X;          // centre, pixels in the image passed in
            public double Y;
            public double Radius;     // pixels
            public double Coverage;   // 0..1 fraction of rim with edge support
            public double MaxGap;     // 0..1 largest contiguous missing fraction of rim
            public double Score;      // Hough vote strength (relative)
            public bool Accepted;
        }

        // Internal working scale: the expected radius is resampled to about this many pixels,
        // so cost does not depend on the camera resolution or measurement zoom.
        const double WorkRadius = 26.0;
        const double Sigma = 1.0;        // blur before gradient (working pixels)
        const double Band = 1.5;         // edge points within this distance of the circle count as rim (working pixels)
        const int RadiusSteps = 7;       // radii voted in the Hough stage
        const double PeakFraction = 0.35;// candidate threshold relative to the strongest peak
        const int MaxCandidates = 12;
        const int AngleBins = 36;

        /// <summary>
        /// Find circles of radius ExpectedRadius (pixels) in a grey image.
        /// </summary>
        /// <param name="grey">8-bit grey pixels, row major, stride == width</param>
        /// <param name="tolerance">relative radius tolerance, e.g. 0.15</param>
        /// <param name="minCoverage">0..1</param>
        /// <param name="maxGap">0..1</param>
        public static List<Hole> Find(byte[] grey, int width, int height, double ExpectedRadius,
            double tolerance, double minCoverage, double maxGap)
        {
            List<Hole> result = new List<Hole>();
            if (ExpectedRadius < 3.0 || width < 16 || height < 16)
            {
                return result;
            }

            // --- resample to working scale ------------------------------------------------------
            double scale = 1.0;
            float[] img;
            int W = width, H = height;
            if (ExpectedRadius > WorkRadius * 1.25)
            {
                scale = WorkRadius / ExpectedRadius;
                W = Math.Max(16, (int)Math.Round(width * scale));
                H = Math.Max(16, (int)Math.Round(height * scale));
                img = Downsample(grey, width, height, W, H);
            }
            else
            {
                img = new float[width * height];
                for (int i = 0; i < img.Length; i++) img[i] = grey[i];
            }
            double rExp = ExpectedRadius * scale;

            // --- blur, gradient -----------------------------------------------------------------
            img = GaussianBlur(img, W, H, Sigma);
            float[] gx = new float[W * H];
            float[] gy = new float[W * H];
            float[] mag = new float[W * H];
            float magMax = 0;
            for (int y = 1; y < H - 1; y++)
            {
                for (int x = 1; x < W - 1; x++)
                {
                    int i = y * W + x;
                    float sx = (img[i - W + 1] + 2 * img[i + 1] + img[i + W + 1]) - (img[i - W - 1] + 2 * img[i - 1] + img[i + W - 1]);
                    float sy = (img[i + W - 1] + 2 * img[i + W] + img[i + W + 1]) - (img[i - W - 1] + 2 * img[i - W] + img[i - W + 1]);
                    gx[i] = sx; gy[i] = sy;
                    float m = (float)Math.Sqrt(sx * sx + sy * sy);
                    mag[i] = m;
                    if (m > magMax) magMax = m;
                }
            }
            if (magMax <= 0) return result;

            // --- edge pixels by Otsu on gradient magnitude --------------------------------------
            float thr = OtsuThreshold(mag, magMax);
            List<int> ex = new List<int>();
            List<int> ey = new List<int>();
            List<float> nx = new List<float>();
            List<float> ny = new List<float>();
            for (int y = 1; y < H - 1; y++)
            {
                for (int x = 1; x < W - 1; x++)
                {
                    int i = y * W + x;
                    if (mag[i] > thr)
                    {
                        ex.Add(x); ey.Add(y);
                        nx.Add(gx[i] / mag[i]); ny.Add(gy[i] / mag[i]);
                    }
                }
            }
            int n = ex.Count;
            if (n < 8) return result;

            // --- Hough votes for centres ---------------------------------------------------------
            float[] acc = new float[W * H];
            for (int s = 0; s < RadiusSteps; s++)
            {
                double r = rExp * (1 - tolerance) + s * (2 * tolerance * rExp) / (RadiusSteps - 1);
                for (int k = 0; k < n; k++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)   // polarity unknown: vote both ways
                    {
                        int cx = (int)Math.Round(ex[k] + sign * r * nx[k]);
                        int cy = (int)Math.Round(ey[k] + sign * r * ny[k]);
                        if (cx >= 0 && cx < W && cy >= 0 && cy < H)
                        {
                            acc[cy * W + cx] += 1;
                        }
                    }
                }
            }
            acc = GaussianBlur(acc, W, H, 1.5);

            // --- peaks ---------------------------------------------------------------------------
            float accMax = 0;
            for (int i = 0; i < acc.Length; i++) if (acc[i] > accMax) accMax = acc[i];
            if (accMax <= 0) return result;
            int win = Math.Max(2, (int)(rExp / 2));
            List<Hole> peaks = new List<Hole>();
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float v = acc[y * W + x];
                    if (v < PeakFraction * accMax) continue;
                    bool isMax = true;
                    for (int dy = -win; dy <= win && isMax; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= H) continue;
                        for (int dx = -win; dx <= win; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= W) continue;
                            if (acc[yy * W + xx] > v) { isMax = false; break; }
                        }
                    }
                    if (isMax)
                    {
                        peaks.Add(new Hole() { X = x, Y = y, Radius = rExp, Score = v / accMax });
                    }
                }
            }
            peaks.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (peaks.Count > MaxCandidates) peaks.RemoveRange(MaxCandidates, peaks.Count - MaxCandidates);

            // --- refine and test each candidate --------------------------------------------------
            List<Hole> cands = new List<Hole>();
            foreach (Hole p in peaks)
            {
                double cx = p.X, cy = p.Y, cr = p.Radius;
                bool ok = true;
                for (int pass = 0; pass < 2 && ok; pass++)
                {
                    ok = PrattFitBand(ex, ey, cx, cy, cr, Band, out cx, out cy, out cr);
                }
                if (!ok) continue;
                RimStats(ex, ey, cx, cy, cr, Band, out double cov, out double gap);
                Hole h = new Hole()
                {
                    X = cx, Y = cy, Radius = cr, Coverage = cov, MaxGap = gap, Score = p.Score,
                    Accepted = (Math.Abs(cr - rExp) <= tolerance * rExp) && (cov >= minCoverage) && (gap <= maxGap)
                };
                cands.Add(h);
            }

            // --- non-maximum suppression: accepted first, then by coverage ----------------------
            cands.Sort((a, b) =>
            {
                if (a.Accepted != b.Accepted) return a.Accepted ? -1 : 1;
                return b.Coverage.CompareTo(a.Coverage);
            });
            foreach (Hole h in cands)
            {
                bool dup = false;
                foreach (Hole k in result)
                {
                    double d = Math.Sqrt((h.X - k.X) * (h.X - k.X) + (h.Y - k.Y) * (h.Y - k.Y));
                    if (d < rExp) { dup = true; break; }
                }
                if (!dup) result.Add(h);
            }

            // --- back to input pixels ----------------------------------------------------------
            foreach (Hole h in result)
            {
                h.X = (h.X + 0.5) / scale - 0.5;
                h.Y = (h.Y + 0.5) / scale - 0.5;
                h.Radius /= scale;
            }
            return result;
        }

        // ------------------------------------------------------------------------------------------
        // helpers

        // Area-average downsample (box filter), W x H output.
        static float[] Downsample(byte[] src, int sw, int sh, int W, int H)
        {
            float[] dst = new float[W * H];
            double fx = (double)sw / W, fy = (double)sh / H;
            for (int y = 0; y < H; y++)
            {
                int y0 = (int)(y * fy), y1 = Math.Min(sh, Math.Max(y0 + 1, (int)((y + 1) * fy)));
                for (int x = 0; x < W; x++)
                {
                    int x0 = (int)(x * fx), x1 = Math.Min(sw, Math.Max(x0 + 1, (int)((x + 1) * fx)));
                    long sum = 0; int cnt = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int row = yy * sw;
                        for (int xx = x0; xx < x1; xx++) { sum += src[row + xx]; cnt++; }
                    }
                    dst[y * W + x] = (float)sum / cnt;
                }
            }
            return dst;
        }

        // Separable Gaussian blur with edge clamping.
        static float[] GaussianBlur(float[] src, int W, int H, double sigma)
        {
            int rad = (int)Math.Ceiling(3 * sigma);
            float[] k = new float[2 * rad + 1];
            float ksum = 0;
            for (int i = -rad; i <= rad; i++) { k[i + rad] = (float)Math.Exp(-i * i / (2 * sigma * sigma)); ksum += k[i + rad]; }
            for (int i = 0; i < k.Length; i++) k[i] /= ksum;

            float[] tmp = new float[W * H];
            float[] dst = new float[W * H];
            for (int y = 0; y < H; y++)
            {
                int row = y * W;
                for (int x = 0; x < W; x++)
                {
                    float s = 0;
                    for (int i = -rad; i <= rad; i++)
                    {
                        int xx = x + i; if (xx < 0) xx = 0; else if (xx >= W) xx = W - 1;
                        s += src[row + xx] * k[i + rad];
                    }
                    tmp[row + x] = s;
                }
            }
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float s = 0;
                    for (int i = -rad; i <= rad; i++)
                    {
                        int yy = y + i; if (yy < 0) yy = 0; else if (yy >= H) yy = H - 1;
                        s += tmp[yy * W + x] * k[i + rad];
                    }
                    dst[y * W + x] = s;
                }
            }
            return dst;
        }

        // Otsu threshold over a 256-bin histogram of the non-zero values in [0, max].
        static float OtsuThreshold(float[] v, float max)
        {
            const int bins = 256;
            long[] hist = new long[bins];
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i] <= 0) continue;
                int b = (int)(v[i] / max * (bins - 1));
                hist[b]++;
            }
            long total = 0; double sumAll = 0;
            for (int b = 0; b < bins; b++) { total += hist[b]; sumAll += (double)b * hist[b]; }
            if (total == 0) return max;
            long w1 = 0; double s1 = 0, best = -1; int bestBin = 0;
            for (int b = 0; b < bins; b++)
            {
                w1 += hist[b]; if (w1 == 0) continue;
                long w2 = total - w1; if (w2 == 0) break;
                s1 += (double)b * hist[b];
                double m1 = s1 / w1, m2 = (sumAll - s1) / w2;
                double between = (double)w1 * w2 * (m1 - m2) * (m1 - m2);
                if (between > best) { best = between; bestBin = b; }
            }
            return (float)bestBin / (bins - 1) * max;
        }

        // Pratt algebraic circle fit on the edge points within 'band' of circle (cx,cy,r).
        static bool PrattFitBand(List<int> ex, List<int> ey, double cx, double cy, double r, double band,
            out double ox, out double oy, out double rOut)
        {
            ox = cx; oy = cy; rOut = r;
            List<double> xs = new List<double>();
            List<double> ys = new List<double>();
            for (int k = 0; k < ex.Count; k++)
            {
                double d = Math.Abs(Math.Sqrt((ex[k] - cx) * (ex[k] - cx) + (ey[k] - cy) * (ey[k] - cy)) - r);
                if (d < band) { xs.Add(ex[k]); ys.Add(ey[k]); }
            }
            int n = xs.Count;
            if (n < 8) return false;
            double xm = 0, ym = 0;
            for (int i = 0; i < n; i++) { xm += xs[i]; ym += ys[i]; }
            xm /= n; ym /= n;
            double Suu = 0, Svv = 0, Suv = 0, Suuu = 0, Svvv = 0, Suvv = 0, Svuu = 0;
            for (int i = 0; i < n; i++)
            {
                double u = xs[i] - xm, v = ys[i] - ym;
                Suu += u * u; Svv += v * v; Suv += u * v;
                Suuu += u * u * u; Svvv += v * v * v; Suvv += u * v * v; Svuu += v * u * u;
            }
            double b1 = 0.5 * (Suuu + Suvv), b2 = 0.5 * (Svvv + Svuu);
            double det = Suu * Svv - Suv * Suv;
            if (Math.Abs(det) < 1e-9) return false;
            double uc = (b1 * Svv - b2 * Suv) / det;
            double vc = (Suu * b2 - Suv * b1) / det;
            ox = uc + xm; oy = vc + ym;
            rOut = Math.Sqrt(uc * uc + vc * vc + (Suu + Svv) / n);
            return !(double.IsNaN(rOut) || rOut <= 0);
        }

        // Fraction of angular bins with rim support, and largest contiguous run of empty bins.
        static void RimStats(List<int> ex, List<int> ey, double cx, double cy, double r, double band,
            out double coverage, out double maxGap)
        {
            bool[] hit = new bool[AngleBins];
            for (int k = 0; k < ex.Count; k++)
            {
                double dx = ex[k] - cx, dy = ey[k] - cy;
                double d = Math.Abs(Math.Sqrt(dx * dx + dy * dy) - r);
                if (d < band)
                {
                    double a = Math.Atan2(dy, dx);                     // -pi..pi
                    int b = (int)((a + Math.PI) / (2 * Math.PI) * AngleBins);
                    if (b >= AngleBins) b = AngleBins - 1;
                    if (b < 0) b = 0;
                    hit[b] = true;
                }
            }
            int filled = 0;
            foreach (bool h in hit) if (h) filled++;
            coverage = (double)filled / AngleBins;
            int best = 0, run = 0;
            for (int i = 0; i < 2 * AngleBins; i++)      // wrap around
            {
                if (!hit[i % AngleBins]) { run++; if (run > best) best = run; }
                else run = 0;
            }
            if (best > AngleBins) best = AngleBins;
            maxGap = (double)best / AngleBins;
        }
    }
}
