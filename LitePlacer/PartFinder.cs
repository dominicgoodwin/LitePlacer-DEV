using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LitePlacer
{
    // Finds a chip component (a rectangle of known size with bright terminations: 0402..2512 chips, SOD/SMA
    // diodes, chip inductors, tantalums) on the nozzle, in an up-camera frame. The nozzle is not masked:
    // it rotates freely, so there is nothing stable to mask. Instead:
    //
    //   1. Threshold sweep 60..240 on the grey image, no operator threshold. At each level, connected
    //      components inside a region around the frame centre; specks and blobs too big to be inside
    //      the part are dropped.
    //   2. From every blob, grow a cluster greedily: add the nearest blob while the minimum-area rectangle
    //      of the cluster's convex hull stays within Length x Width (1 + tolerance). Every intermediate
    //      cluster is a candidate, so a stray speck absorbed late does not hide the part.
    //   3. Candidates from all thresholds are grouped (same centre, angle, size). A real part survives
    //      many thresholds; nozzle clutter one or two.
    //   4. Evidence per group: Edge = fraction of the rectangle outline with a bright->dark gradient across
    //      it; Symmetry = overlap of the bright mask inside the rectangle with its own 180-degree-rotated
    //      or mirrored copy; Outside = bright material just outside the rectangle (a smaller rectangle
    //      inside a bigger part, or clutter). Score = size fit + threshold stability + edge + symmetry,
    //      halved when any gate fails.
    //   5. The best group above the acceptance score is refined: each side moves to the strongest gradient
    //      along its normal.
    //
    // Prototyped offline (python, same arithmetic) on saved up-cam frames: every 0603/0805/1206 found,
    // all six empty nozzles rejected, wrong nominal size rejected, synthetic rotations recovered to 1 degree.
    public static class PartFinder
    {
        public class Part
        {
            public double X;          // centre, pixels in the image passed in
            public double Y;
            public double Length;     // pixels, long side
            public double Width;      // pixels, short side
            public double Angle;      // degrees, long side relative to image x axis, -90..90
            public double Score;      // 0..1
            public int Levels;        // thresholds at which this rectangle was found
            public double Edge;       // 0..1
            public double Symmetry;   // 0..1
            public double Outside;    // 0..1
            public bool Accepted;
        }

        // Working scale: the part's length is resampled to about this many pixels.
        const double WorkLength = 90.0;
        const int ThrFirst = 60;
        const int ThrLast = 240;
        const int ThrStep = 10;
        const int Levels = (ThrLast - ThrFirst) / ThrStep + 1;

        // ------------------------------------------------------------------------------------------
        // grey: 8-bit, stride == width. LengthPx/WidthPx: nominal part size in pixels of this image.
        // Tolerance: fraction (0.15). MaxRotation: degrees from 0/90. MaxCentreDistPx: how far from the
        // frame centre the part centre may be (pixels, beyond the half-length).
        // Returns the best candidate (Accepted says whether it passed), or null if nothing at all was found.
        public static Part Find(byte[] grey, int width, int height, double LengthPx, double WidthPx,
            double Tolerance, double MaxRotation, double MaxCentreDistPx, double AcceptScore, StringBuilder log)
        {
            if (WidthPx > LengthPx)
            {
                double t = LengthPx; LengthPx = WidthPx; WidthPx = t;
            }
            int f = (int)Math.Max(1, Math.Floor(LengthPx / WorkLength));
            int W = width / f;
            int H = height / f;
            float[] img = Downsample(grey, width, height, f, W, H);
            double L = LengthPx / f;
            double Wd = WidthPx / f;
            double Lmax = L * (1 + Tolerance), Wmax = Wd * (1 + Tolerance);
            double Lmin = L * (1 - Tolerance), Wmin = Wd * (1 - Tolerance);
            double cx0 = W / 2.0, cy0 = H / 2.0;
            double maxDist = MaxCentreDistPx / f + L / 2;
            double roiR = maxDist + Lmax;

            // ---- 1 + 2: candidates at every threshold
            List<Cand> cands = new List<Cand>();
            int[] labels = new int[W * H];
            for (int thr = ThrFirst; thr <= ThrLast; thr += ThrStep)
            {
                List<Blob> blobs = LabelBlobs(img, W, H, thr, cx0, cy0, roiR, labels);
                // filter
                List<Blob> keep = new List<Blob>();
                foreach (Blob b in blobs)
                {
                    if (b.Area < 3) continue;
                    if (Math.Max(b.MaxX - b.MinX + 1, b.MaxY - b.MinY + 1) > Lmax * 1.05) continue;
                    if (Hypot(b.Cx - cx0, b.Cy - cy0) > maxDist + Lmax / 2) continue;
                    b.Hull = ConvexHull(b.Points);
                    keep.Add(b);
                }
                keep.Sort((a, b) => b.Area.CompareTo(a.Area));
                HashSet<string> seen = new HashSet<string>();
                for (int si = 0; si < keep.Count; si++)
                {
                    List<int> members = new List<int>();
                    members.Add(si);
                    List<double[]> hull = keep[si].Hull;
                    Rect rect = MinAreaRect(hull);
                    Emit(members, rect, keep, thr, Lmin, Lmax, Wmin, Wmax, L, Wd, Tolerance, MaxRotation, cx0, cy0, maxDist, seen, cands);
                    bool changed = true;
                    while (changed)
                    {
                        changed = false;
                        // nearest non-member blob first
                        List<KeyValuePair<double, int>> order = new List<KeyValuePair<double, int>>();
                        for (int bi = 0; bi < keep.Count; bi++)
                        {
                            if (members.Contains(bi)) continue;
                            double d = Hypot(keep[bi].Cx - rect.Cx, keep[bi].Cy - rect.Cy);
                            if (d > Lmax) continue;
                            order.Add(new KeyValuePair<double, int>(d, bi));
                        }
                        order.Sort((a, b) => a.Key.CompareTo(b.Key));
                        foreach (KeyValuePair<double, int> kv in order)
                        {
                            List<double[]> pts = new List<double[]>(hull);
                            pts.AddRange(keep[kv.Value].Hull);
                            List<double[]> newHull = ConvexHull(pts);
                            Rect r = MinAreaRect(newHull);
                            if (r.L <= Lmax && r.W <= Wmax)
                            {
                                members.Add(kv.Value);
                                hull = newHull;
                                rect = r;
                                changed = true;
                                Emit(members, rect, keep, thr, Lmin, Lmax, Wmin, Wmax, L, Wd, Tolerance, MaxRotation, cx0, cy0, maxDist, seen, cands);
                                break;
                            }
                        }
                    }
                }
            }
            if (cands.Count == 0)
            {
                if (log != null) log.Append("Find part: no rectangle of that size at any threshold");
                return null;
            }

            // ---- 3: group across thresholds
            List<Group> groups = GroupCandidates(cands, Math.Max(3.0, 0.06 * L));

            // ---- 4: evidence and score
            float[] blur = GaussianBlur(img, W, H, 1.2);
            float[] gx, gy;
            Sobel(blur, W, H, out gx, out gy);
            Group best = null;
            foreach (Group g in groups)
            {
                if (g.NLevels < 3 || g.Geom < 0.2) continue;
                g.Edge = EdgeSupport(gx, gy, W, H, g.Cx, g.Cy, g.L, g.W, g.Angle);
                float otsu;
                Symmetry(img, W, H, g.Cx, g.Cy, g.L, g.W, g.Angle, out g.Sym180, out g.SymL, out g.SymW, out otsu);
                g.Outside = OutsideBrightness(img, W, H, g.Cx, g.Cy, g.L, g.W, g.Angle, otsu);
                double stab = Math.Min(1.0, g.NLevels / 8.0);
                double sym = Math.Max(g.Sym180, Math.Min(g.SymL, g.SymW));
                g.Symmetry = sym;
                double score = 0.25 * Math.Max(0.0, g.Geom) + 0.25 * stab + 0.3 * g.Edge + 0.2 * sym;
                if (g.Edge < 0.7 || sym < 0.55) score *= 0.5;
                if (g.Outside > 0.25) score *= 0.5;
                g.Score = score;
                if (best == null || score > best.Score) best = g;
            }
            if (best == null)
            {
                if (log != null) log.Append("Find part: " + cands.Count.ToString(CultureInfo.InvariantCulture)
                    + " candidates, none stable across thresholds");
                return null;
            }

            // ---- 5: refine
            double rcx = best.Cx, rcy = best.Cy, rL = best.L, rW = best.W;
            bool accepted = best.Score >= AcceptScore;
            if (accepted)
            {
                RefineSides(blur, gx, gy, W, H, best.Angle, ref rcx, ref rcy, ref rL, ref rW);
            }
            Part p = new Part();
            p.X = (rcx + 0.5) * f - 0.5;
            p.Y = (rcy + 0.5) * f - 0.5;
            p.Length = rL * f;
            p.Width = rW * f;
            p.Angle = best.Angle;
            p.Score = best.Score;
            p.Levels = best.NLevels;
            p.Edge = best.Edge;
            p.Symmetry = best.Symmetry;
            p.Outside = best.Outside;
            p.Accepted = accepted;
            if (log != null)
            {
                log.Append("Find part: " + cands.Count.ToString(CultureInfo.InvariantCulture) + " candidates in "
                    + groups.Count.ToString(CultureInfo.InvariantCulture) + " groups; best ("
                    + p.X.ToString("0.0", CultureInfo.InvariantCulture) + ", " + p.Y.ToString("0.0", CultureInfo.InvariantCulture) + ") "
                    + p.Length.ToString("0.0", CultureInfo.InvariantCulture) + "x" + p.Width.ToString("0.0", CultureInfo.InvariantCulture)
                    + " px, angle " + p.Angle.ToString("0.0", CultureInfo.InvariantCulture)
                    + ", levels " + p.Levels.ToString(CultureInfo.InvariantCulture)
                    + ", edge " + p.Edge.ToString("0.00", CultureInfo.InvariantCulture)
                    + ", sym " + p.Symmetry.ToString("0.00", CultureInfo.InvariantCulture)
                    + ", outside " + p.Outside.ToString("0.00", CultureInfo.InvariantCulture)
                    + ", score " + p.Score.ToString("0.00", CultureInfo.InvariantCulture)
                    + (accepted ? " OK" : " rejected"));
            }
            return p;
        }

        // ------------------------------------------------------------------------------------------
        class Blob
        {
            public int Area;
            public int MinX, MinY, MaxX, MaxY;
            public double Cx, Cy;
            public List<double[]> Points = new List<double[]>();
            public List<double[]> Hull;
        }

        class Rect
        {
            public double Cx, Cy, L, W, Angle;   // Angle: long side vs x axis, degrees
        }

        class Cand
        {
            public double Cx, Cy, L, W, Angle, Geom;
            public int Thr;
        }

        class Group
        {
            public List<Cand> Members = new List<Cand>();
            public double Cx, Cy, L, W, Angle, Geom, Score;
            public int NLevels;
            public double Edge, Sym180, SymL, SymW, Symmetry, Outside;
        }

        static double Hypot(double a, double b)
        {
            return Math.Sqrt(a * a + b * b);
        }

        static double NormAngle(double a)
        {
            a = a % 180.0;
            if (a > 90.0) a -= 180.0;
            if (a <= -90.0) a += 180.0;
            return a;
        }

        static void Emit(List<int> members, Rect rect, List<Blob> blobs, int thr,
            double Lmin, double Lmax, double Wmin, double Wmax, double L, double Wd, double tol, double maxRot,
            double cx0, double cy0, double maxDist, HashSet<string> seen, List<Cand> cands)
        {
            List<int> sorted = new List<int>(members);
            sorted.Sort();
            StringBuilder key = new StringBuilder();
            foreach (int m in sorted) key.Append(m).Append(',');
            if (!seen.Add(key.ToString())) return;
            if (rect.L < Lmin || rect.W < Wmin || rect.L > Lmax || rect.W > Wmax) return;
            double ang = NormAngle(rect.Angle);
            double angErr = Math.Min(Math.Abs(NormAngle(ang)), Math.Abs(NormAngle(ang - 90)));
            if (angErr > maxRot) return;
            double dist = Hypot(rect.Cx - cx0, rect.Cy - cy0);
            if (dist > maxDist) return;
            double sizeErr = (Math.Abs(rect.L - L) / (L * tol) + Math.Abs(rect.W - Wd) / (Wd * tol)) / 2;
            double geom = 1.0 - 0.6 * sizeErr - 0.2 * (dist / maxDist) - 0.2 * (angErr / Math.Max(maxRot, 1.0));
            Cand c = new Cand();
            c.Cx = rect.Cx; c.Cy = rect.Cy; c.L = rect.L; c.W = rect.W; c.Angle = ang; c.Geom = geom; c.Thr = thr;
            cands.Add(c);
        }

        static List<Group> GroupCandidates(List<Cand> cands, double posTol)
        {
            cands.Sort((a, b) => a.Thr.CompareTo(b.Thr));
            List<Group> groups = new List<Group>();
            foreach (Cand c in cands)
            {
                Group found = null;
                foreach (Group g in groups)
                {
                    Cand r = g.Members[0];
                    if (Hypot(c.Cx - r.Cx, c.Cy - r.Cy) <= posTol
                        && Math.Abs(NormAngle(c.Angle - r.Angle)) <= 6.0
                        && Math.Abs(c.L - r.L) <= 0.10 * r.L && Math.Abs(c.W - r.W) <= 0.10 * r.W)
                    {
                        found = g;
                        break;
                    }
                }
                if (found == null)
                {
                    found = new Group();
                    groups.Add(found);
                }
                found.Members.Add(c);
            }
            foreach (Group g in groups)
            {
                HashSet<int> thrs = new HashSet<int>();
                List<double> xs = new List<double>(), ys = new List<double>(), ls = new List<double>(), ws = new List<double>(), angs = new List<double>();
                double geom = 0;
                foreach (Cand c in g.Members)
                {
                    thrs.Add(c.Thr);
                    xs.Add(c.Cx); ys.Add(c.Cy); ls.Add(c.L); ws.Add(c.W); angs.Add(c.Angle); geom += c.Geom;
                }
                g.NLevels = thrs.Count;
                g.Cx = Median(xs); g.Cy = Median(ys); g.L = Median(ls); g.W = Median(ws); g.Angle = Median(angs);
                g.Geom = geom / g.Members.Count;
            }
            return groups;
        }

        static double Median(List<double> v)
        {
            List<double> s = new List<double>(v);
            s.Sort();
            int n = s.Count;
            return (n % 2 == 1) ? s[n / 2] : 0.5 * (s[n / 2 - 1] + s[n / 2]);
        }

        // ------------------------------------------------------------------------------------------
        // Connected components (8-connected) of img > thr inside a disc of radius roiR about (cx0, cy0).
        static List<Blob> LabelBlobs(float[] img, int W, int H, int thr, double cx0, double cy0, double roiR, int[] labels)
        {
            Array.Clear(labels, 0, labels.Length);
            List<int> parent = new List<int>();
            parent.Add(0);
            double roiR2 = roiR * roiR;
            // first pass
            for (int y = 0; y < H; y++)
            {
                double dy = y - cy0;
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (img[i] <= thr) continue;
                    double dx = x - cx0;
                    if (dx * dx + dy * dy > roiR2) continue;
                    int best = 0;
                    // neighbours already labelled: W, NW, N, NE
                    int[] nb = new int[4];
                    int nn = 0;
                    if (x > 0 && labels[i - 1] > 0) nb[nn++] = labels[i - 1];
                    if (y > 0)
                    {
                        if (x > 0 && labels[i - W - 1] > 0) nb[nn++] = labels[i - W - 1];
                        if (labels[i - W] > 0) nb[nn++] = labels[i - W];
                        if (x < W - 1 && labels[i - W + 1] > 0) nb[nn++] = labels[i - W + 1];
                    }
                    if (nn == 0)
                    {
                        parent.Add(parent.Count);
                        best = parent.Count - 1;
                    }
                    else
                    {
                        best = nb[0];
                        for (int k = 1; k < nn; k++) if (nb[k] < best) best = nb[k];
                        for (int k = 0; k < nn; k++) Union(parent, best, nb[k]);
                    }
                    labels[i] = best;
                }
            }
            // second pass: resolve and collect
            Dictionary<int, Blob> blobs = new Dictionary<int, Blob>();
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (labels[i] == 0) continue;
                    int root = FindRoot(parent, labels[i]);
                    labels[i] = root;
                    Blob b;
                    if (!blobs.TryGetValue(root, out b))
                    {
                        b = new Blob();
                        b.MinX = b.MaxX = x; b.MinY = b.MaxY = y;
                        blobs[root] = b;
                    }
                    b.Area++;
                    if (x < b.MinX) b.MinX = x;
                    if (x > b.MaxX) b.MaxX = x;
                    if (y < b.MinY) b.MinY = y;
                    if (y > b.MaxY) b.MaxY = y;
                    b.Cx += x; b.Cy += y;
                    // outline pixels only (a 4-neighbour outside the blob): enough for the hull, far fewer points
                    bool edge = (x == 0 || y == 0 || x == W - 1 || y == H - 1)
                        || FindRoot(parent, labels[i - 1]) != root
                        || FindRoot(parent, labels[i - W]) != root
                        || FindRoot(parent, labels[i + 1]) != root
                        || FindRoot(parent, labels[i + W]) != root;
                    if (edge) b.Points.Add(new double[] { x, y });
                }
            }
            List<Blob> list = new List<Blob>();
            foreach (Blob b in blobs.Values)
            {
                b.Cx /= b.Area; b.Cy /= b.Area;
                list.Add(b);
            }
            return list;
        }

        static int FindRoot(List<int> parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]];
                i = parent[i];
            }
            return i;
        }

        static void Union(List<int> parent, int a, int b)
        {
            int ra = FindRoot(parent, a), rb = FindRoot(parent, b);
            if (ra == rb) return;
            if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
        }

        // ------------------------------------------------------------------------------------------
        // Convex hull, Andrew's monotone chain. Points as {x, y}. Returns hull in counter-clockwise order.
        static List<double[]> ConvexHull(List<double[]> pts)
        {
            List<double[]> p = new List<double[]>(pts);
            if (p.Count < 3) return p;
            p.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));
            List<double[]> lower = new List<double[]>();
            foreach (double[] q in p)
            {
                while (lower.Count >= 2 && Cross(lower[lower.Count - 2], lower[lower.Count - 1], q) <= 0) lower.RemoveAt(lower.Count - 1);
                lower.Add(q);
            }
            List<double[]> upper = new List<double[]>();
            for (int i = p.Count - 1; i >= 0; i--)
            {
                double[] q = p[i];
                while (upper.Count >= 2 && Cross(upper[upper.Count - 2], upper[upper.Count - 1], q) <= 0) upper.RemoveAt(upper.Count - 1);
                upper.Add(q);
            }
            lower.RemoveAt(lower.Count - 1);
            upper.RemoveAt(upper.Count - 1);
            lower.AddRange(upper);
            return lower;
        }

        static double Cross(double[] o, double[] a, double[] b)
        {
            return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
        }

        // Minimum-area enclosing rectangle of a convex hull (rotating calipers over hull edge directions).
        // Pixels are treated as unit squares: half a pixel is added on each side.
        static Rect MinAreaRect(List<double[]> hull)
        {
            Rect best = new Rect();
            if (hull.Count == 0) return best;
            if (hull.Count == 1)
            {
                best.Cx = hull[0][0]; best.Cy = hull[0][1]; best.L = 1; best.W = 1; best.Angle = 0;
                return best;
            }
            double bestArea = double.MaxValue;
            int n = hull.Count;
            for (int i = 0; i < n; i++)
            {
                double[] a = hull[i], b = hull[(i + 1) % n];
                double ex = b[0] - a[0], ey = b[1] - a[1];
                double len = Hypot(ex, ey);
                if (len < 1e-9) continue;
                ex /= len; ey /= len;
                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (double[] q in hull)
                {
                    double u = q[0] * ex + q[1] * ey;
                    double v = -q[0] * ey + q[1] * ex;
                    if (u < minU) minU = u;
                    if (u > maxU) maxU = u;
                    if (v < minV) minV = v;
                    if (v > maxV) maxV = v;
                }
                double du = maxU - minU + 1.0, dv = maxV - minV + 1.0;
                double area = du * dv;
                if (area < bestArea)
                {
                    bestArea = area;
                    double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
                    best.Cx = cu * ex - cv * ey;
                    best.Cy = cu * ey + cv * ex;
                    if (du >= dv)
                    {
                        best.L = du; best.W = dv; best.Angle = Math.Atan2(ey, ex) * 180.0 / Math.PI;
                    }
                    else
                    {
                        best.L = dv; best.W = du; best.Angle = Math.Atan2(ex, -ey) * 180.0 / Math.PI;
                    }
                }
            }
            best.Angle = NormAngle(best.Angle);
            return best;
        }

        // ------------------------------------------------------------------------------------------
        static float[] Downsample(byte[] src, int sw, int sh, int f, int W, int H)
        {
            float[] dst = new float[W * H];
            if (f == 1)
            {
                for (int i = 0; i < W * H; i++) dst[i] = src[i];
                return dst;
            }
            float inv = 1.0f / (f * f);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int sum = 0;
                    for (int yy = 0; yy < f; yy++)
                    {
                        int row = (y * f + yy) * sw + x * f;
                        for (int xx = 0; xx < f; xx++) sum += src[row + xx];
                    }
                    dst[y * W + x] = sum * inv;
                }
            }
            return dst;
        }

        static float[] GaussianBlur(float[] src, int W, int H, double sigma)
        {
            int r = (int)Math.Ceiling(3 * sigma);
            float[] k = new float[2 * r + 1];
            float ks = 0;
            for (int i = -r; i <= r; i++)
            {
                k[i + r] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
                ks += k[i + r];
            }
            for (int i = 0; i < k.Length; i++) k[i] /= ks;
            float[] tmp = new float[W * H];
            float[] dst = new float[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float s = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int xx = x + i;
                        if (xx < 0) xx = 0; else if (xx >= W) xx = W - 1;
                        s += src[y * W + xx] * k[i + r];
                    }
                    tmp[y * W + x] = s;
                }
            }
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    float s = 0;
                    for (int i = -r; i <= r; i++)
                    {
                        int yy = y + i;
                        if (yy < 0) yy = 0; else if (yy >= H) yy = H - 1;
                        s += tmp[yy * W + x] * k[i + r];
                    }
                    dst[y * W + x] = s;
                }
            }
            return dst;
        }

        static void Sobel(float[] src, int W, int H, out float[] gx, out float[] gy)
        {
            gx = new float[W * H];
            gy = new float[W * H];
            for (int y = 1; y < H - 1; y++)
            {
                for (int x = 1; x < W - 1; x++)
                {
                    int i = y * W + x;
                    gx[i] = (src[i - W + 1] + 2 * src[i + 1] + src[i + W + 1]) - (src[i - W - 1] + 2 * src[i - 1] + src[i + W - 1]);
                    gy[i] = (src[i + W - 1] + 2 * src[i + W] + src[i + W + 1]) - (src[i - W - 1] + 2 * src[i - W] + src[i - W + 1]);
                }
            }
        }

        static float Sample(float[] img, int W, int H, double x, double y)
        {
            int xi = (int)Math.Round(x), yi = (int)Math.Round(y);
            if (xi < 0 || yi < 0 || xi >= W || yi >= H) return 0;
            return img[yi * W + xi];
        }

        // ------------------------------------------------------------------------------------------
        // Fraction of the rectangle outline where the gradient points inward (bright inside, dark outside),
        // searching +/- 3 px along the outward normal.
        static double EdgeSupport(float[] gx, float[] gy, int W, int H, double cx, double cy, double L, double Wd, double angDeg)
        {
            double a = angDeg * Math.PI / 180.0;
            double ux = Math.Cos(a), uy = Math.Sin(a), vx = -uy, vy = ux;
            int sup = 0, tot = 0;
            int nL = Math.Max(8, (int)(L / 2)), nW = Math.Max(6, (int)(Wd / 2));
            for (int i = 0; i < nL; i++)
            {
                double t = -0.5 + i / (double)(nL - 1);
                for (int s = -1; s <= 1; s += 2)
                {
                    double px = cx + t * L * ux + s * Wd / 2 * vx, py = cy + t * L * uy + s * Wd / 2 * vy;
                    if (EdgeAt(gx, gy, W, H, px, py, s * vx, s * vy)) sup++;
                    tot++;
                }
            }
            for (int i = 0; i < nW; i++)
            {
                double t = -0.5 + i / (double)(nW - 1);
                for (int s = -1; s <= 1; s += 2)
                {
                    double px = cx + s * L / 2 * ux + t * Wd * vx, py = cy + s * L / 2 * uy + t * Wd * vy;
                    if (EdgeAt(gx, gy, W, H, px, py, s * ux, s * uy)) sup++;
                    tot++;
                }
            }
            return tot == 0 ? 0 : sup / (double)tot;
        }

        static bool EdgeAt(float[] gx, float[] gy, int W, int H, double px, double py, double nx, double ny)
        {
            double best = 0;
            for (int d = -3; d <= 3; d++)
            {
                double v = -(Sample(gx, W, H, px + d * nx, py + d * ny) * nx + Sample(gy, W, H, px + d * nx, py + d * ny) * ny);
                if (v > best) best = v;
            }
            return best > 60;
        }

        static bool InRect(double x, double y, double cx, double cy, double L, double Wd, double ux, double uy)
        {
            double dx = x - cx, dy = y - cy;
            double u = dx * ux + dy * uy, v = -dx * uy + dy * ux;
            return Math.Abs(u) <= L / 2 && Math.Abs(v) <= Wd / 2;
        }

        // Symmetry of the bright (Otsu inside the rectangle) mask under 180-degree rotation and the two mirrors,
        // as intersection-over-union of the mask with its transformed self.
        static void Symmetry(float[] img, int W, int H, double cx, double cy, double L, double Wd, double angDeg,
            out double sym180, out double symL, out double symW, out float otsu)
        {
            double a = angDeg * Math.PI / 180.0;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            double L1 = L * 1.1, W1 = Wd * 1.1;
            double R = Hypot(L1, W1) / 2 + 1;
            int x0 = Math.Max(0, (int)(cx - R)), x1 = Math.Min(W - 1, (int)(cx + R) + 1);
            int y0 = Math.Max(0, (int)(cy - R)), y1 = Math.Min(H - 1, (int)(cy + R) + 1);
            // histogram inside the rect
            int[] hist = new int[256];
            int count = 0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (InRect(x, y, cx, cy, L1, W1, ux, uy))
                    {
                        int v = (int)Math.Round(img[y * W + x]);
                        if (v < 0) v = 0; else if (v > 255) v = 255;
                        hist[v]++;
                        count++;
                    }
            otsu = Otsu(hist, count);
            int nA = 0, i180 = 0, iL = 0, iW = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!InRect(x, y, cx, cy, L1, W1, ux, uy)) continue;
                    if (img[y * W + x] <= otsu) continue;
                    nA++;
                    double dx = x - cx, dy = y - cy;
                    double u = dx * ux + dy * uy, v = -dx * uy + dy * ux;
                    // 180 degrees: (-u, -v); mirror about long axis: (u, -v); mirror about short axis: (-u, v)
                    if (BrightAt(img, W, H, cx, cy, -u, -v, ux, uy, otsu)) i180++;
                    if (BrightAt(img, W, H, cx, cy, u, -v, ux, uy, otsu)) iL++;
                    if (BrightAt(img, W, H, cx, cy, -u, v, ux, uy, otsu)) iW++;
                }
            }
            sym180 = nA == 0 ? 0 : i180 / (double)(2 * nA - i180);
            symL = nA == 0 ? 0 : iL / (double)(2 * nA - iL);
            symW = nA == 0 ? 0 : iW / (double)(2 * nA - iW);
        }

        static bool BrightAt(float[] img, int W, int H, double cx, double cy, double u, double v, double ux, double uy, float thr)
        {
            double x = cx + u * ux - v * uy, y = cy + u * uy + v * ux;
            int xi = (int)Math.Round(x), yi = (int)Math.Round(y);
            if (xi < 0 || yi < 0 || xi >= W || yi >= H) return false;
            return img[yi * W + xi] > thr;
        }

        static float Otsu(int[] hist, int total)
        {
            if (total == 0) return 127;
            double sum = 0;
            for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
            double sumB = 0, wB = 0, maxVar = -1;
            int thr = 127;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                double wF = total - wB;
                if (wF == 0) break;
                sumB += t * (double)hist[t];
                double mB = sumB / wB, mF = (sum - sumB) / wF;
                double var = wB * wF * (mB - mF) * (mB - mF);
                if (var > maxVar)
                {
                    maxVar = var;
                    thr = t;
                }
            }
            return thr;
        }

        // Fraction of pixels in a band 3..10 px outside the rectangle that are above the Otsu threshold.
        static double OutsideBrightness(float[] img, int W, int H, double cx, double cy, double L, double Wd, double angDeg, float thr)
        {
            double a = angDeg * Math.PI / 180.0;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            double R = Hypot(L + 20, Wd + 20) / 2 + 1;
            int x0 = Math.Max(0, (int)(cx - R)), x1 = Math.Min(W - 1, (int)(cx + R) + 1);
            int y0 = Math.Max(0, (int)(cy - R)), y1 = Math.Min(H - 1, (int)(cy + R) + 1);
            int n = 0, bright = 0;
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    if (!InRect(x, y, cx, cy, L + 20, Wd + 20, ux, uy)) continue;
                    if (InRect(x, y, cx, cy, L + 6, Wd + 6, ux, uy)) continue;
                    n++;
                    if (img[y * W + x] > thr) bright++;
                }
            }
            return n == 0 ? 0 : bright / (double)n;
        }

        // Move each side to the median position of the strongest inward gradient along its normal (+/- 8 px).
        static void RefineSides(float[] blur, float[] gx, float[] gy, int W, int H, double angDeg,
            ref double cx, ref double cy, ref double L, ref double Wd)
        {
            double a = angDeg * Math.PI / 180.0;
            double ux = Math.Cos(a), uy = Math.Sin(a), vx = -uy, vy = ux;
            double oTop = SideOffset(gx, gy, W, H, ux, uy, L / 2, -vx, -vy, cx - Wd / 2 * vx, cy - Wd / 2 * vy);
            double oBot = SideOffset(gx, gy, W, H, ux, uy, L / 2, vx, vy, cx + Wd / 2 * vx, cy + Wd / 2 * vy);
            double oLef = SideOffset(gx, gy, W, H, vx, vy, Wd / 2, -ux, -uy, cx - L / 2 * ux, cy - L / 2 * uy);
            double oRig = SideOffset(gx, gy, W, H, vx, vy, Wd / 2, ux, uy, cx + L / 2 * ux, cy + L / 2 * uy);
            double W2 = Wd + oTop + oBot, L2 = L + oLef + oRig;
            cx = cx + (oRig - oLef) / 2 * ux + (oBot - oTop) / 2 * vx;
            cy = cy + (oRig - oLef) / 2 * uy + (oBot - oTop) / 2 * vy;
            L = L2; Wd = W2;
        }

        static double SideOffset(float[] gx, float[] gy, int W, int H, double alongX, double alongY, double halfLen,
            double nx, double ny, double px0, double py0)
        {
            List<double> offs = new List<double>();
            for (int i = 0; i < 15; i++)
            {
                double t = -0.4 + 0.8 * i / 14.0;
                double px = px0 + t * 2 * halfLen * alongX, py = py0 + t * 2 * halfLen * alongY;
                double best = 0, bo = 0;
                bool have = false;
                for (double d = -8; d <= 8; d += 0.5)
                {
                    double x = px + d * nx, y = py + d * ny;
                    double v = -(Sample(gx, W, H, x, y) * nx + Sample(gy, W, H, x, y) * ny);
                    if (v > best)
                    {
                        best = v; bo = d; have = true;
                    }
                }
                if (have && best > 40) offs.Add(bo);
            }
            return offs.Count >= 5 ? Median(offs) : 0.0;
        }
    }
}
