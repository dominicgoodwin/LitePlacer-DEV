using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace LitePlacer
{
    // Camera and nozzle calibration, Setup Cameras tab. Three steps, each a button, each reporting its
    // numbers in the results box and asking before it changes a setting:
    //
    //   Target: three holes (tape holes stuck to perspex, or similar) lying on the up camera at nozzle
    //   tip height. Nothing else is needed.
    //
    //   1. Up cam: find target.   The up camera finds the three holes (pixels). Nothing moves.
    //   2. Down cam: measure.     The down camera is driven to each hole and centred on it, so the hole
    //                             positions are known in machine coordinates. The two sets of three
    //                             points give the up cam image centre in machine coordinates, the
    //                             camera rotation and a scale check.
    //   3. Nozzle axis.           The (empty) nozzle is taken to the up cam and its bore found at
    //                             A = 0, 90, 180, 270. The centre of the four points is the rotation
    //                             axis, their spread the runout. The axis mapped through step 2's result
    //                             gives the nozzle offset (gantry position minus axis position).
    //   4. Placed part error.     After placing a part with the job, select it on the CAD grid: the down
    //                             cam goes to its target and measures where it landed. The mean error
    //                             over the parts measured corrects the nozzle offset. This closes the
    //                             loop on whatever the first three steps can't see (e.g. a camera that
    //                             is tilted, so the image centre at the target height is not the image
    //                             centre at nozzle tip height).
    //
    // Coordinates: a camera result (Xr, Yr) is "camera mm", what Camera.Measure() returns with no
    // rotation: X to the right and Y up in the image, in mm. For the down cam that is the machine
    // offset of the feature from the camera centre. For the up cam the machine offset of a feature is
    // Rot(rotation) * (-Xr, Yr): the image is a mirror (we look at the part from below), and the camera
    // can be turned a little in its mount. UpCam_Rotation is that rotation, the same value Measure()
    // takes out of every up cam result.
    public partial class FormMain : Form
    {
        // Results of step 1 and 2, kept for the session:
        private List<double> CalTargetXr = new List<double>();    // up cam, camera mm
        private List<double> CalTargetYr = new List<double>();
        private List<double> CalTargetMX = new List<double>();    // down cam, machine coordinates
        private List<double> CalTargetMY = new List<double>();
        private bool CalCombosFilling = false;

        private List<double> CalPlaceErrX = new List<double>();   // step 4: landed-part errors since the last apply
        private List<double> CalPlaceErrY = new List<double>();

        private const double CalCentreTolerance = 0.015;  // mm: down cam centring is done when the residual is below this
        private const int CalCentreIterations = 4;
        private const int CalSettleMs = 300;              // wait after a move before measuring
        private const int CalFrames = 3;                  // frames averaged per measurement

        // ==========================================================================================
        // UI helpers

        private void CalReport(string line)
        {
            CalResults_textBox.AppendText(line + Environment.NewLine);
            DisplayText(line);
        }

        private void CalError(string line)
        {
            CalResults_textBox.AppendText("*** " + line + Environment.NewLine);
            DisplayText(line, KnownColor.DarkRed, true);
        }

        private static string F3(double v)
        {
            return v.ToString("0.000", CultureInfo.InvariantCulture);
        }

        private static string F2(double v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Called after the algorithms are loaded and whenever one is added, removed or renamed
        // (from RefreshUpCamAlgCombos).
        public void FillCalAlgCombos()
        {
            CalCombosFilling = true;
            CalTargetAlg_comboBox.Items.Clear();
            CalNozzleAlg_comboBox.Items.Clear();
            CalTargetAlg_comboBox.Items.Add("--");
            CalNozzleAlg_comboBox.Items.Add("--");
            foreach (VideoAlgorithmsCollection.FullAlgorithmDescription Alg in VideoAlgorithms.AllAlgorithms)
            {
                CalTargetAlg_comboBox.Items.Add(Alg.Name);
                CalNozzleAlg_comboBox.Items.Add(Alg.Name);
            }
            CalTargetAlg_comboBox.SelectedItem = CalTargetAlg_comboBox.Items.Contains(Setting.Cal_TargetAlgorithm) ? Setting.Cal_TargetAlgorithm : "--";
            CalNozzleAlg_comboBox.SelectedItem = CalNozzleAlg_comboBox.Items.Contains(Setting.Cal_NozzleAlgorithm) ? Setting.Cal_NozzleAlgorithm : "--";
            CalCombosFilling = false;
        }

        private void CalTargetAlg_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (CalCombosFilling) return;
            Setting.Cal_TargetAlgorithm = CalTargetAlg_comboBox.SelectedItem.ToString();
        }

        private void CalNozzleAlg_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (CalCombosFilling) return;
            Setting.Cal_NozzleAlgorithm = CalNozzleAlg_comboBox.SelectedItem.ToString();
        }

        // ==========================================================================================
        // Camera helpers

        // Loads a named algorithm as the camera's measurement algorithm (as the run loop does).
        private bool CalLoadAlgorithm_m(Camera Cam, string AlgName, string Purpose)
        {
            if (string.IsNullOrEmpty(AlgName) || (AlgName == "--"))
            {
                CalError("Select the " + Purpose + " algorithm first.");
                return false;
            }
            VideoAlgorithmsCollection.FullAlgorithmDescription Alg = new VideoAlgorithmsCollection.FullAlgorithmDescription();
            if (!VideoAlgorithms.FindAlgorithm(AlgName, out Alg))
            {
                CalError(Purpose + " algorithm \"" + AlgName + "\" not found.");
                return false;
            }
            Cam.BuildMeasurementFunctionsList(Alg.FunctionList);
            Cam.MeasurementParameters = Alg.MeasurementParameters;
            return true;
        }

        // Switches to the camera and checks it runs.
        private bool CalSelectCamera_m(Camera Cam)
        {
            SelectCamera(Cam);
            if (!Cam.IsRunning())
            {
                CalError(Cam.Name + " is not running.");
                return false;
            }
            return true;
        }

        // Runs the measurement algorithm on a fresh frame and returns the candidates in camera mm.
        // A few tries, as the run loop does, to get past a frame that is still settling.
        private bool CalGetCandidates_m(Camera Cam, out List<double> Xr, out List<double> Yr)
        {
            Xr = new List<double>();
            Yr = new List<double>();
            List<Shapes.Shape> Found = null;
            bool ok = false;
            for (int tries = 0; tries < 5; tries++)
            {
                if (Cam.MeasureCandidates(out Found, true))
                {
                    ok = true;
                    break;
                }
                Thread.Sleep(150);
            }
            if (!ok)
            {
                CalError(Cam.Name + ": nothing found.");
                return false;
            }
            double zoom = Cam.GetMeasurementZoom();
            double XmmPpix = Cam.XmmPerPixel / zoom;
            double YmmPpix = Cam.YmmPerPixel / zoom;
            int cx = Cam.CameraResolution.X / 2;
            int cy = Cam.CameraResolution.Y / 2;
            foreach (Shapes.Shape s in Found)
            {
                Xr.Add((s.Center.X - cx) * XmmPpix);
                Yr.Add((cy - s.Center.Y) * YmmPpix);
            }
            return true;
        }

        // The candidate nearest the image centre
        private static int CalNearestToCentre(List<double> Xr, List<double> Yr)
        {
            int best = 0;
            double bestD = double.MaxValue;
            for (int i = 0; i < Xr.Count; i++)
            {
                double d = Xr[i] * Xr[i] + Yr[i] * Yr[i];
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        // Settles, then measures CalFrames frames and averages the candidate nearest the image centre.
        private bool CalMeasureNearest_m(Camera Cam, out double X, out double Y, out int Seen)
        {
            X = 0.0;
            Y = 0.0;
            Seen = 0;
            Thread.Sleep(CalSettleMs);
            int got = 0;
            for (int f = 0; f < CalFrames; f++)
            {
                List<double> Xr, Yr;
                if (!CalGetCandidates_m(Cam, out Xr, out Yr))
                {
                    continue;
                }
                int n = CalNearestToCentre(Xr, Yr);
                X += Xr[n];
                Y += Yr[n];
                Seen = Math.Max(Seen, Xr.Count);
                got++;
            }
            if (got == 0)
            {
                return false;
            }
            X /= got;
            Y /= got;
            return true;
        }

        // ==========================================================================================
        // Geometry

        // Up cam centre in machine coordinates: from the calibration if there is one, else from the
        // up cam (nozzle) position and the nozzle offset.
        private void CalUpCamCentre(out double X, out double Y, out string Source)
        {
            if ((Math.Abs(Setting.UpCam_MachineX) > 0.001) || (Math.Abs(Setting.UpCam_MachineY) > 0.001))
            {
                X = Setting.UpCam_MachineX;
                Y = Setting.UpCam_MachineY;
                Source = "calibrated";
            }
            else
            {
                X = Setting.UpCam_PositionX - Setting.DownCam_NozzleOffsetX;
                Y = Setting.UpCam_PositionY - Setting.DownCam_NozzleOffsetY;
                Source = "up cam position minus nozzle offset";
            }
        }

        // Up cam camera mm -> machine coordinates, using the current settings.
        private void CalUpCamToMachine(double Xr, double Yr, out double X, out double Y)
        {
            double cx, cy;
            string src;
            CalUpCamCentre(out cx, out cy, out src);
            double r = Setting.UpCam_Rotation * Math.PI / 180.0;
            double c = Math.Cos(r), s = Math.Sin(r);
            double dx = -Xr, dy = Yr;        // mirror: we look from below
            X = cx + c * dx - s * dy;
            Y = cy + s * dx + c * dy;
        }

        // Solves M = C + B * r from three point pairs (exact). Returns false if the points are in a line.
        private static bool CalSolveAffine(List<double> Xr, List<double> Yr, List<double> MX, List<double> MY,
            out double b00, out double b01, out double b10, out double b11, out double cx, out double cy)
        {
            b00 = b01 = b10 = b11 = cx = cy = 0.0;
            // Rows [Xr, Yr, 1] * (b.0, b.1, c) = M
            double a0 = Xr[0], a1 = Yr[0], a2 = 1.0;
            double d0 = Xr[1], d1 = Yr[1], d2 = 1.0;
            double g0 = Xr[2], g1 = Yr[2], g2 = 1.0;
            double det = a0 * (d1 * g2 - d2 * g1) - a1 * (d0 * g2 - d2 * g0) + a2 * (d0 * g1 - d1 * g0);
            if (Math.Abs(det) < 1e-6)
            {
                return false;
            }
            // inverse via cofactors
            double i00 = (d1 * g2 - d2 * g1) / det, i01 = (a2 * g1 - a1 * g2) / det, i02 = (a1 * d2 - a2 * d1) / det;
            double i10 = (d2 * g0 - d0 * g2) / det, i11 = (a0 * g2 - a2 * g0) / det, i12 = (a2 * d0 - a0 * d2) / det;
            double i20 = (d0 * g1 - d1 * g0) / det, i21 = (a1 * g0 - a0 * g1) / det, i22 = (a0 * d1 - a1 * d0) / det;
            b00 = i00 * MX[0] + i01 * MX[1] + i02 * MX[2];
            b01 = i10 * MX[0] + i11 * MX[1] + i12 * MX[2];
            cx = i20 * MX[0] + i21 * MX[1] + i22 * MX[2];
            b10 = i00 * MY[0] + i01 * MY[1] + i02 * MY[2];
            b11 = i10 * MY[0] + i11 * MY[1] + i12 * MY[2];
            cy = i20 * MY[0] + i21 * MY[1] + i22 * MY[2];
            return true;
        }

        // ==========================================================================================
        // Step 1: up cam finds the target holes

        private void CalStep1_button_Click(object sender, EventArgs e)
        {
            CalResults_textBox.Clear();
            CalTargetXr.Clear();
            CalTargetYr.Clear();
            CalTargetMX.Clear();
            CalTargetMY.Clear();
            CalReport("Step 1: up cam, target algorithm \"" + Setting.Cal_TargetAlgorithm + "\"");
            if (!CalSelectCamera_m(UpCamera)) return;
            if (!CalLoadAlgorithm_m(UpCamera, Setting.Cal_TargetAlgorithm, "target")) return;
            Thread.Sleep(CalSettleMs);
            List<double> Xr, Yr;
            if (!CalGetCandidates_m(UpCamera, out Xr, out Yr)) return;
            if (Xr.Count != 3)
            {
                CalError("Found " + Xr.Count.ToString() + " circles, need exactly 3. Clear the view (nozzle away), check the target and the algorithm's size limits.");
                return;
            }
            for (int i = 0; i < 3; i++)
            {
                double X, Y;
                CalUpCamToMachine(Xr[i], Yr[i], out X, out Y);
                CalReport("Hole " + (i + 1).ToString() + ": up cam " + F3(Xr[i]) + ", " + F3(Yr[i])
                    + " mm from centre; expected near machine " + F3(X) + ", " + F3(Y));
                CalTargetXr.Add(Xr[i]);
                CalTargetYr.Add(Yr[i]);
            }
            CalReport("Now step 2 (the machine will move the down cam to each hole).");
        }

        // ==========================================================================================
        // Step 2: down cam measures the target holes, solve the up cam transform

        private void CalStep2_button_Click(object sender, EventArgs e)
        {
            if (CalTargetXr.Count != 3)
            {
                CalError("Do step 1 first.");
                return;
            }
            if (!CheckPositionConfidence()) return;
            if (MovementIsBusy_m()) return;
            CalTargetMX.Clear();
            CalTargetMY.Clear();
            CalReport("Step 2: down cam, target algorithm \"" + Setting.Cal_TargetAlgorithm + "\"");
            if (!CalSelectCamera_m(DownCamera)) return;
            if (!CalLoadAlgorithm_m(DownCamera, Setting.Cal_TargetAlgorithm, "target")) return;
            if (!CNC_Z_m(0)) return;

            for (int i = 0; i < 3; i++)
            {
                double X, Y;
                CalUpCamToMachine(CalTargetXr[i], CalTargetYr[i], out X, out Y);
                if (!CNC_XYA_m(X, Y, Cnc.CurrentA)) return;
                double dx = 0.0, dy = 0.0;
                bool centred = false;
                for (int iter = 0; iter < CalCentreIterations; iter++)
                {
                    int seen;
                    if (!CalMeasureNearest_m(DownCamera, out dx, out dy, out seen))
                    {
                        CalError("Hole " + (i + 1).ToString() + " not seen by the down cam at " + F3(Cnc.CurrentX) + ", " + F3(Cnc.CurrentY) + ".");
                        return;
                    }
                    if ((Math.Abs(dx) < CalCentreTolerance) && (Math.Abs(dy) < CalCentreTolerance))
                    {
                        centred = true;
                        break;
                    }
                    if (iter < CalCentreIterations - 1)
                    {
                        if (!CNC_XYA_m(Cnc.CurrentX + dx, Cnc.CurrentY + dy, Cnc.CurrentA)) return;
                    }
                }
                X = Cnc.CurrentX + dx;
                Y = Cnc.CurrentY + dy;
                CalTargetMX.Add(X);
                CalTargetMY.Add(Y);
                CalReport("Hole " + (i + 1).ToString() + ": machine " + F3(X) + ", " + F3(Y)
                    + (centred ? "" : " (residual " + F3(dx) + ", " + F3(dy) + " after " + CalCentreIterations.ToString() + " tries)"));
            }

            double b00, b01, b10, b11, cx, cy;
            if (!CalSolveAffine(CalTargetXr, CalTargetYr, CalTargetMX, CalTargetMY, out b00, out b01, out b10, out b11, out cx, out cy))
            {
                CalError("The three holes are in a line; the transform can't be solved. Move one.");
                return;
            }
            // Machine offset = Rot(rho) * (-Xr, Yr)  =>  Rot(rho) = B * diag(-1, 1)
            double rotU = Math.Atan2(-b10, -b00) * 180.0 / Math.PI;     // from the image X axis
            double rotV = Math.Atan2(-b01, b11) * 180.0 / Math.PI;      // from the image Y axis
            double rot = (rotU + rotV) / 2.0;
            double scaleU = Math.Sqrt(b00 * b00 + b10 * b10);            // 1.0 = mm/pixel settings are right at this height
            double scaleV = Math.Sqrt(b01 * b01 + b11 * b11);
            CalReport("Up cam centre in machine coordinates: " + F3(cx) + ", " + F3(cy)
                + " (was " + F3(Setting.UpCam_MachineX) + ", " + F3(Setting.UpCam_MachineY) + ")");
            CalReport("Rotation: " + F2(rot) + " deg (X axis " + F2(rotU) + ", Y axis " + F2(rotV)
                + "; the two should agree within ~0.5 deg; setting is " + F2(Setting.UpCam_Rotation) + ")");
            CalReport("Scale check: X " + scaleU.ToString("0.000", CultureInfo.InvariantCulture)
                + ", Y " + scaleV.ToString("0.000", CultureInfo.InvariantCulture)
                + " times the mm/pixel settings (a target above or below the tip height reads a few % off; not applied)");
            if (Math.Abs(rotU - rotV) > 1.5)
            {
                CalError("The two axis rotations disagree by more than 1.5 deg: a hole was probably mis-centred. Check and repeat.");
            }

            // Show it: down cam over the up cam centre
            if (!CNC_XYA_m(cx, cy, Cnc.CurrentA)) return;

            DialogResult dr = ShowMessageBox(
                "Set up cam centre to " + F3(cx) + ", " + F3(cy) + " and up cam rotation to " + F2(rot) + " deg?",
                "Calibration step 2", MessageBoxButtons.YesNo);
            if (dr == DialogResult.Yes)
            {
                Setting.UpCam_MachineX = cx;
                Setting.UpCam_MachineY = cy;
                UpCameraRotation_textBox.Text = F2(rot);     // the handler stores the setting and tells the camera
                CalReport("Applied. Now step 3 with an empty nozzle.");
            }
            else
            {
                CalReport("Not applied. Step 3 would use the old values.");
            }
        }

        // ==========================================================================================
        // Step 3: nozzle axis, runout and offset

        private void CalStep3_button_Click(object sender, EventArgs e)
        {
            if (!CheckPositionConfidence()) return;
            if (MovementIsBusy_m()) return;
            double cx, cy;
            string src;
            CalUpCamCentre(out cx, out cy, out src);
            CalReport("Step 3: nozzle axis, nozzle algorithm \"" + Setting.Cal_NozzleAlgorithm + "\", up cam centre " + F3(cx) + ", " + F3(cy) + " (" + src + ")");
            if (src != "calibrated")
            {
                CalError("Up cam centre is not calibrated (steps 1 and 2); the nozzle offset below assumes the current settings are right.");
            }
            if (!CalSelectCamera_m(UpCamera)) return;
            if (!CalLoadAlgorithm_m(UpCamera, Setting.Cal_NozzleAlgorithm, "nozzle")) return;

            // Nozzle to the up cam, down to measuring height (as the run loop and nozzle calibration do)
            if (!CNC_Z_m(0)) return;
            if (!CNC_XYA_m(Setting.UpCam_PositionX, Setting.UpCam_PositionY, 0.0)) return;
            if (!CNC_Z_m(Setting.General_Z0toPCB - 0.5)) return;

            double[] Angles = { 0.0, 90.0, 180.0, 270.0 };
            List<double> Xs = new List<double>();
            List<double> Ys = new List<double>();
            foreach (double A in Angles)
            {
                if (!CNC_A_m(A)) return;
                double bx, by;
                int seen;
                if (!CalMeasureNearest_m(UpCamera, out bx, out by, out seen))
                {
                    CalError("Nozzle not seen at A = " + F2(A) + ".");
                    CNC_Z_m(0);
                    return;
                }
                Xs.Add(bx);
                Ys.Add(by);
                CalReport("A = " + A.ToString("0", CultureInfo.InvariantCulture) + ": bore at " + F3(bx) + ", " + F3(by) + " mm from image centre"
                    + ((seen > 1) ? " (" + seen.ToString() + " circles seen, nearest taken)" : ""));
            }
            if (!CNC_A_m(0.0)) return;
            if (!CNC_Z_m(0)) return;

            double ax = (Xs[0] + Xs[1] + Xs[2] + Xs[3]) / 4.0;
            double ay = (Ys[0] + Ys[1] + Ys[2] + Ys[3]) / 4.0;
            double runout = 0.0;
            for (int i = 0; i < 4; i++)
            {
                double d = Math.Sqrt((Xs[i] - ax) * (Xs[i] - ax) + (Ys[i] - ay) * (Ys[i] - ay));
                if (d > runout) runout = d;
            }
            // the two diagonals should give the same centre
            double diag = Math.Sqrt(Math.Pow((Xs[0] + Xs[2]) / 2.0 - (Xs[1] + Xs[3]) / 2.0, 2) + Math.Pow((Ys[0] + Ys[2]) / 2.0 - (Ys[1] + Ys[3]) / 2.0, 2));
            double axX, axY;
            CalUpCamToMachine(ax, ay, out axX, out axY);
            double offX = Setting.UpCam_PositionX - axX;
            double offY = Setting.UpCam_PositionY - axY;
            CalReport("Rotation axis: " + F3(ax) + ", " + F3(ay) + " mm from image centre; runout " + F3(runout) + " mm"
                + (diag > 0.02 ? "; diagonals disagree by " + F3(diag) + " mm, check the A axis" : ""));
            CalReport("Axis in machine coordinates: " + F3(axX) + ", " + F3(axY));
            CalReport("Nozzle offset: " + F3(offX) + ", " + F3(offY)
                + " (setting " + F3(Setting.DownCam_NozzleOffsetX) + ", " + F3(Setting.DownCam_NozzleOffsetY) + ")");

            double newPosX = cx + offX;
            double newPosY = cy + offY;
            DialogResult dr = ShowMessageBox(
                "Set nozzle offset to " + F3(offX) + ", " + F3(offY) + " (was " + F3(Setting.DownCam_NozzleOffsetX) + ", " + F3(Setting.DownCam_NozzleOffsetY) + ")" + Environment.NewLine +
                "and up cam position to " + F3(newPosX) + ", " + F3(newPosY) + " (was " + F3(Setting.UpCam_PositionX) + ", " + F3(Setting.UpCam_PositionY) + "), so the nozzle sits in the middle of the image?",
                "Calibration step 3", MessageBoxButtons.YesNo);
            if (dr == DialogResult.Yes)
            {
                NozzleOffsetX_textBox.Text = F3(offX);       // handlers store the settings
                NozzleOffsetY_textBox.Text = F3(offY);
                UpcamPositionX_textBox.Text = F3(newPosX);
                UpcamPositionY_textBox.Text = F3(newPosY);
                CalPlaceErrX.Clear();
                CalPlaceErrY.Clear();
                CalReport("Applied. Place a part or two and check them with step 4.");
            }
            else
            {
                CalReport("Not applied.");
            }
        }

        // ==========================================================================================
        // Step 4: where did a placed part land? Down cam at the selected component's target position,
        // measured with the algorithm selected on the Video processing tab.

        private void CalStep4_button_Click(object sender, EventArgs e)
        {
            if (!CheckPositionConfidence()) return;
            if (MovementIsBusy_m()) return;
            double X, Y;
            if (!PrepareSingleComponentOperation(out X, out Y))
            {
                CalError("Step 4: select the placed component on the CAD data grid (Job tab) first.");
                return;
            }
            string Comp = CadData_GridView.CurrentCell.OwningRow.Cells["CADdataComponentColumn"].Value.ToString();
            if (VideoAlgorithms.CurrentAlgorithm == null)
            {
                CalError("Step 4: select the part's down cam algorithm on the Video processing tab first.");
                return;
            }
            string AlgName = VideoAlgorithms.CurrentAlgorithm.Name;
            if (!CalSelectCamera_m(DownCamera)) return;
            if (!CNC_Z_m(0)) return;
            if (!CNC_XYA_m(X, Y, Cnc.CurrentA)) return;
            DownCamera.BuildMeasurementFunctionsList(VideoAlgorithms.CurrentAlgorithm.FunctionList);
            DownCamera.MeasurementParameters = VideoAlgorithms.CurrentAlgorithm.MeasurementParameters;
            Thread.Sleep(CalSettleMs);
            double ex = 0.0, ey = 0.0, ea = 0.0;
            bool ok = false;
            for (int tries = 0; tries < 5; tries++)
            {
                if (DownCamera.Measure(out ex, out ey, out ea, true))
                {
                    ok = true;
                    break;
                }
                Thread.Sleep(150);
            }
            if (!ok)
            {
                CalError("Step 4: " + Comp + " not found at its target with algorithm \"" + AlgName + "\".");
                return;
            }
            CalPlaceErrX.Add(ex);
            CalPlaceErrY.Add(ey);
            double mx = 0.0, my = 0.0;
            for (int i = 0; i < CalPlaceErrX.Count; i++)
            {
                mx += CalPlaceErrX[i];
                my += CalPlaceErrY[i];
            }
            mx /= CalPlaceErrX.Count;
            my /= CalPlaceErrY.Count;
            CalReport("Step 4: " + Comp + " landed " + F3(ex) + ", " + F3(ey) + " mm from its target (\"" + AlgName + "\", angle " + F2(ea) + ")");
            CalReport("Mean of " + CalPlaceErrX.Count.ToString() + " part(s): " + F3(mx) + ", " + F3(my));

            // The part lands at target + e, so the nozzle axis is e away from where the offset says:
            // true offset = offset - e. The nozzle was seen at the image centre in step 3, so the up cam
            // position stays; the camera centre at tip height is position - offset.
            double offX = Setting.DownCam_NozzleOffsetX - mx;
            double offY = Setting.DownCam_NozzleOffsetY - my;
            DialogResult dr = ShowMessageBox(
                "Correct the nozzle offset by the mean error, to " + F3(offX) + ", " + F3(offY)
                + " (was " + F3(Setting.DownCam_NozzleOffsetX) + ", " + F3(Setting.DownCam_NozzleOffsetY) + ")?" + Environment.NewLine
                + "No = keep the sample and measure more parts first.",
                "Calibration step 4", MessageBoxButtons.YesNo);
            if (dr == DialogResult.Yes)
            {
                NozzleOffsetX_textBox.Text = F3(offX);
                NozzleOffsetY_textBox.Text = F3(offY);
                Setting.UpCam_MachineX = Setting.UpCam_PositionX - offX;
                Setting.UpCam_MachineY = Setting.UpCam_PositionY - offY;
                CalPlaceErrX.Clear();
                CalPlaceErrY.Clear();
                CalReport("Applied; up cam centre is now " + F3(Setting.UpCam_MachineX) + ", " + F3(Setting.UpCam_MachineY) + ". Place another part to confirm.");
            }
            else
            {
                CalReport("Not applied; sample kept.");
            }
        }

        // ==========================================================================================

        private void CalGoUpCam_button_Click(object sender, EventArgs e)
        {
            if (!CheckPositionConfidence()) return;
            if (MovementIsBusy_m()) return;
            double cx, cy;
            string src;
            CalUpCamCentre(out cx, out cy, out src);
            CalReport("Down cam to up cam centre " + F3(cx) + ", " + F3(cy) + " (" + src + ")");
            if (!CNC_Z_m(0)) return;
            CNC_XYA_m(cx, cy, Cnc.CurrentA);
        }
    }
}
