using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace LitePlacer
{
    // Job check ("dry run", issue #4): goes through the job without placing anything and reports, per row,
    // what would stop or spoil the run. Two levels:
    //
    //   Geometry (instant, no movement): feeder exists and its algorithms exist, nozzle number is valid,
    //   enough parts in the feeder (Capacity), and - the one LitePlacer never checked - that the machine can
    //   actually get there. Every move is guarded against the gantry limits, but the nozzle sits a long way
    //   from the down camera (the nozzle offset): the gantry goes to part + offset to pick, so a part the
    //   camera can see near the far X / far Y edge is out of the nozzle's reach. Pickup positions of every part the
    //   row will use and every placement position are checked for the nozzle (and the holes for the camera).
    //
    //   Measured (moves the machine, optional): fiducials if not yet measured, then for each tape feeder the
    //   first and last hole of the parts the row needs, as the fast placement does: measured vs nominal
    //   pitch, tape angle, and the short-tape walk-back if the last hole is missing.
    //
    // The geometry level also runs as a pre-flight at the start of Place All and refuses to start on an
    // error, naming the row.
    public partial class FormMain : Form
    {
        private const double JobCheckMargin = 1.0;   // mm inside the machine limits

        private enum JobCheckLevel { OK, Warning, Error }

        private class JobCheckItem
        {
            public int Row;
            public JobCheckLevel Level;
            public string Text;
        }

        private List<JobCheckItem> JobCheckItems = new List<JobCheckItem>();

        private void JobCheckAdd(int Row, JobCheckLevel Level, string Text)
        {
            JobCheckItems.Add(new JobCheckItem() { Row = Row, Level = Level, Text = Text });
            KnownColor c = (Level == JobCheckLevel.Error) ? KnownColor.DarkRed : ((Level == JobCheckLevel.Warning) ? KnownColor.DarkOrange : KnownColor.Black);
            DisplayText("Job check, row " + (Row + 1).ToString(CultureInfo.InvariantCulture) + ": " + Text, c);
        }

        private string JobRowName(int Row)
        {
            object v = JobData_GridView.Rows[Row].Cells["JobDataValueColumn"].Value;
            object f = JobData_GridView.Rows[Row].Cells["JobDataFootprintColumn"].Value;
            return ((v == null) ? "" : v.ToString()) + ", " + ((f == null) ? "" : f.ToString());
        }

        // ==========================================================================================
        // Reach

        private bool CameraCanReach(double X, double Y)
        {
            return (X >= -Math.Abs(Setting.General_NegativeX) + JobCheckMargin) && (X <= Setting.General_MachineSizeX - JobCheckMargin)
                && (Y >= -Math.Abs(Setting.General_NegativeY) + JobCheckMargin) && (Y <= Setting.General_MachineSizeY - JobCheckMargin);
        }

        // Nozzle.Move_m(X, Y) sends the gantry to (X + offset, Y + offset): the nozzle sits at gantry minus the
        // offset, so parts near the low X/Y edge are fine and the far edges are where the nozzle runs out.
        private bool NozzleCanReach(double X, double Y)
        {
            return CameraCanReach(X + Setting.DownCam_NozzleOffsetX, Y + Setting.DownCam_NozzleOffsetY);
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // ==========================================================================================
        // Geometry checks for one row. Returns false on an error (warnings don't count).

        // Parts already claimed from each feeder by earlier rows of the same check (two rows on one feeder)
        private Dictionary<string, int> JobCheckConsumed = new Dictionary<string, int>(StringComparer.Ordinal);
        private Dictionary<string, int> JobCheckMeasured = new Dictionary<string, int>(StringComparer.Ordinal);

        private bool JobCheckRowGeometry(int Row)
        {
            bool ok = true;
            if (JobRowSkipped(Row)) return true;
            string Method = JobRowMethod(Row);
            if ((Method != "Place") && (Method != "Place Fast") && (Method != "Place Assisted"))
            {
                return true;    // fiducials, process steps
            }
            string TapeID = JobRowTapeId(Row);
            int TapeNum;
            if (!Tapes.IdValidates_m(TapeID, out TapeNum))
            {
                JobCheckAdd(Row, JobCheckLevel.Error, "feeder \"" + TapeID + "\" not found");
                return false;
            }

            // Algorithms
            VideoAlgorithmsCollection.FullAlgorithmDescription Alg;
            string DownAlg = TapeCellString(TapeNum, "Type_Column");
            if (!UseCoordinatesDirectly(TapeNum) && !VideoAlgorithms.FindAlgorithm(DownAlg, out Alg))
            {
                JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": down cam algorithm \"" + DownAlg + "\" does not exist");
                ok = false;
            }
            if (JobRowUseUpCam(Row))
            {
                string UpAlg = JobRowUpCamAlgorithm(Row);
                if (!VideoAlgorithms.FindAlgorithm(UpAlg, out Alg))
                {
                    JobCheckAdd(Row, JobCheckLevel.Error, "up cam algorithm \"" + UpAlg + "\" does not exist");
                    ok = false;
                }
            }

            // Nozzle
            if (Setting.Nozzles_Enabled)
            {
                int n = JobRowNozzle(Row);
                if (n == 0) n = Setting.Nozzles_default;
                if ((n < 1) || (n > Setting.Nozzles_count))
                {
                    JobCheckAdd(Row, JobCheckLevel.Error, "nozzle " + n.ToString(CultureInfo.InvariantCulture) + " does not exist (machine has "
                        + Setting.Nozzles_count.ToString(CultureInfo.InvariantCulture) + ")");
                    ok = false;
                }
            }

            // Parts needed: components of the row not yet placed
            List<string> Components = new List<string>();
            object cv = JobData_GridView.Rows[Row].Cells["JobdataComponentsColumn"].Value;
            if ((cv != null) && (cv.ToString() != "--") && (cv.ToString() != ""))
            {
                foreach (string c in cv.ToString().Split(','))
                {
                    if ((CurrentPanel != null) && CurrentPanel.IsSkipped(c.Trim())) continue;
                    bool placed = false;
                    if (!AlreadyPlaced_m(c.Trim(), ref placed)) return false;
                    if (!placed) Components.Add(c.Trim());
                }
            }
            int Needed = Components.Count;
            if (Needed == 0)
            {
                JobCheckAdd(Row, JobCheckLevel.OK, "all placed already");
                return true;
            }

            // Stock
            int Next;
            if (!int.TryParse(TapeCellString(TapeNum, "NextPart_Column"), out Next))
            {
                JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": Next is not a number");
                return false;
            }
            int Earlier;
            if (JobCheckConsumed.TryGetValue(TapeID, out Earlier))
            {
                Next += Earlier;    // an earlier row of the job takes these parts first
            }
            JobCheckConsumed[TapeID] = Earlier + Needed;
            int Capacity;
            // Capacity: parts in the feeder when full; blank or 1 (the old hidden default) = unknown
            if (int.TryParse(TapeCellString(TapeNum, "Capacity_Column"), out Capacity) && (Capacity > 1))
            {
                int Left = Capacity - Next + 1;
                if (Left < Needed)
                {
                    JobCheckAdd(Row, JobCheckLevel.Warning, "feeder " + TapeID + ": " + Needed.ToString(CultureInfo.InvariantCulture)
                        + " parts needed, " + Math.Max(Left, 0).ToString(CultureInfo.InvariantCulture) + " left of capacity "
                        + Capacity.ToString(CultureInfo.InvariantCulture));
                }
            }

            // Pickup reach, every part the row will use
            int FirstBad = -1;
            double BadX = 0.0, BadY = 0.0;
            bool CameraBad = false;
            for (int p = Next; p < Next + Needed; p++)
            {
                double X, Y, A;
                if (UseCoordinatesDirectly(TapeNum))
                {
                    bool inc;
                    if (!FindPartWithDirectCoordinates_m(TapeNum, p, out X, out Y, out A, out inc))
                    {
                        JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": bad coordinate data");
                        return false;
                    }
                    bool reach = UseNozzleCoordinates(TapeNum) ? CameraCanReach(X, Y) : NozzleCanReach(X, Y);
                    if (!reach)
                    {
                        FirstBad = p; BadX = X; BadY = Y;
                        break;
                    }
                }
                else
                {
                    double HX, HY;
                    if (!Tapes.GetPartHoleNominal_m(TapeNum, p, out HX, out HY))
                    {
                        JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": bad tape data");
                        return false;
                    }
                    if (!CameraCanReach(HX, HY))
                    {
                        FirstBad = p; BadX = HX; BadY = HY; CameraBad = true;
                        break;
                    }
                    if (!Tapes.GetPartLocationFromHolePosition_m(TapeNum, HX, HY, p, out X, out Y, out A))
                    {
                        JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": bad tape data");
                        return false;
                    }
                    if (!NozzleCanReach(X, Y))
                    {
                        FirstBad = p; BadX = X; BadY = Y;
                        break;
                    }
                }
            }
            if (FirstBad >= 0)
            {
                JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": part " + FirstBad.ToString(CultureInfo.InvariantCulture)
                    + " at " + Fmt(BadX) + ", " + Fmt(BadY) + " is out of reach of the " + (CameraBad ? "camera" : "nozzle")
                    + (CameraBad ? "" : " (gantry would be at " + Fmt(BadX + Setting.DownCam_NozzleOffsetX) + ", " + Fmt(BadY + Setting.DownCam_NozzleOffsetY) + ")"));
                ok = false;
            }

            // Placement reach
            bool Nominal = !ValidMeasurement_checkBox.Checked;
            int BadPlace = 0;
            string BadName = "";
            foreach (string c in Components)
            {
                double X, Y;
                if (!JobCheckPlacementPosition(c, Nominal, out X, out Y))
                {
                    JobCheckAdd(Row, JobCheckLevel.Error, "component " + c + " has no usable coordinates");
                    ok = false;
                    continue;
                }
                if (!NozzleCanReach(X, Y))
                {
                    BadPlace++;
                    if (BadName == "") BadName = c + " at " + Fmt(X) + ", " + Fmt(Y);
                }
            }
            if (BadPlace > 0)
            {
                JobCheckAdd(Row, JobCheckLevel.Error, BadPlace.ToString(CultureInfo.InvariantCulture) + " placement position(s) out of reach of the nozzle, first "
                    + BadName + (Nominal ? " (nominal coordinates, fiducials not measured)" : ""));
                ok = false;
            }
            if (ok)
            {
                JobCheckAdd(Row, JobCheckLevel.OK, "feeder " + TapeID + ", " + Needed.ToString(CultureInfo.InvariantCulture) + " part(s), parts "
                    + Next.ToString(CultureInfo.InvariantCulture) + "-" + (Next + Needed - 1).ToString(CultureInfo.InvariantCulture)
                    + ((Earlier > 0) ? " (after " + Earlier.ToString(CultureInfo.InvariantCulture) + " for earlier rows)" : "") + ", reach OK");
            }
            return ok;
        }

        // Machine coordinates of a component: measured (after fiducials) or nominal + jig/job offset.
        private bool JobCheckPlacementPosition(string Component, bool Nominal, out double X, out double Y)
        {
            X = 0.0;
            Y = 0.0;
            foreach (DataGridViewRow R in CadData_GridView.Rows)
            {
                if ((R.Cells["CADdataComponentColumn"].Value == null) || (R.Cells["CADdataComponentColumn"].Value.ToString() != Component)) continue;
                string xs, ys;
                if (Nominal)
                {
                    xs = (R.Cells["CADdataXnominalColumn"].Value ?? "").ToString();
                    ys = (R.Cells["CADdataYnominalColumn"].Value ?? "").ToString();
                }
                else
                {
                    xs = (R.Cells["CADdataXmachineColumn"].Value ?? "").ToString();
                    ys = (R.Cells["CADdataYmachineColumn"].Value ?? "").ToString();
                }
                if (!double.TryParse(xs.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out X)) return false;
                if (!double.TryParse(ys.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out Y)) return false;
                if (Nominal)
                {
                    X += Setting.General_JigOffsetX + Setting.Job_Xoffset;
                    Y += Setting.General_JigOffsetY + Setting.Job_Yoffset;
                }
                return true;
            }
            return false;
        }

        // ==========================================================================================
        // Measured checks for one row: fiducials are assumed done; measures the tape's first and last hole.

        private void JobCheckRowMeasure(int Row)
        {
            if (JobRowSkipped(Row)) return;
            string Method = JobRowMethod(Row);
            if ((Method != "Place") && (Method != "Place Fast") && (Method != "Place Assisted")) return;
            string TapeID = JobRowTapeId(Row);
            int TapeNum;
            if (!Tapes.IdValidates_m(TapeID, out TapeNum)) return;
            if (UseCoordinatesDirectly(TapeNum)) return;

            int Needed = 0;
            object cv = JobData_GridView.Rows[Row].Cells["JobdataComponentsColumn"].Value;
            if ((cv != null) && (cv.ToString() != "--") && (cv.ToString() != ""))
            {
                foreach (string c in cv.ToString().Split(','))
                {
                    if ((CurrentPanel != null) && CurrentPanel.IsSkipped(c.Trim())) continue;
                    bool placed = false;
                    if (!AlreadyPlaced_m(c.Trim(), ref placed)) return;
                    if (!placed) Needed++;
                }
            }
            if (Needed == 0) return;

            double Pitch;
            if (!double.TryParse(TapeCellString(TapeNum, "Pitch_Column").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out Pitch)) return;

            // PrepareForFastPlacement_m does exactly the measurement we want, including the cut-leader and
            // short-tape handling, and only touches the Fast* properties. Earlier rows on the same feeder
            // take their parts first.
            int Earlier;
            JobCheckMeasured.TryGetValue(TapeID, out Earlier);
            JobCheckMeasured[TapeID] = Earlier + Needed;
            // Only holes the camera can reach are measured; the nominal position of a far part on a long tape
            // can lie outside the machine, and the camera move would stop the check with a limits error.
            // The geometry pass has already reported the parts that are out of reach.
            int Reachable = ReachableHoles(TapeNum, Earlier, Needed);
            if (Reachable < Needed)
            {
                if (Reachable <= 0)
                {
                    JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": no holes within camera reach, not measured");
                    return;
                }
                JobCheckAdd(Row, JobCheckLevel.Warning, "feeder " + TapeID + ": only the first " + Reachable.ToString(CultureInfo.InvariantCulture)
                    + " of " + Needed.ToString(CultureInfo.InvariantCulture) + " parts are within camera reach; measuring those");
                Needed = Reachable;
            }
            if (!Tapes.PrepareForFastPlacement_m(TapeID, Needed, Earlier))
            {
                JobCheckAdd(Row, JobCheckLevel.Error, "feeder " + TapeID + ": tape holes not found (see the log)");
                return;
            }
            Tapes.FastParametersOk = false;
            if (Tapes.FastAvailable < Needed)
            {
                JobCheckAdd(Row, JobCheckLevel.Warning, "feeder " + TapeID + ": tape ends after part " + Tapes.FastLastPartNo.ToString(CultureInfo.InvariantCulture)
                    + "; " + Tapes.FastAvailable.ToString(CultureInfo.InvariantCulture) + " of " + Needed.ToString(CultureInfo.InvariantCulture) + " parts available");
            }
            if (Tapes.FastAvailable > 1)
            {
                double HolePitch = Math.Sqrt(Tapes.FastXstep * Tapes.FastXstep + Tapes.FastYstep * Tapes.FastYstep);
                double Nominal = (Math.Abs(Pitch - 2.0) < 0.01) ? 4.0 : Pitch;
                double Angle = Math.Atan2(Tapes.FastYstep, Tapes.FastXstep) * 180.0 / Math.PI;
                string Orient = TapeCellString(TapeNum, "Orientation_Column");
                double Expected = (Orient == "+X") ? 0.0 : (Orient == "+Y") ? 90.0 : (Orient == "-X") ? 180.0 : -90.0;
                double Skew = Angle - Expected;
                while (Skew > 180.0) Skew -= 360.0;
                while (Skew < -180.0) Skew += 360.0;
                JobCheckLevel lv = ((Math.Abs(HolePitch - Nominal) > 0.1) || (Math.Abs(Skew) > 2.0)) ? JobCheckLevel.Warning : JobCheckLevel.OK;
                JobCheckAdd(Row, lv, "feeder " + TapeID + ": measured hole pitch " + HolePitch.ToString("0.000", CultureInfo.InvariantCulture)
                    + " (nominal " + Nominal.ToString("0.00", CultureInfo.InvariantCulture) + "), tape skew " + Skew.ToString("0.0", CultureInfo.InvariantCulture) + " deg");
            }
            else
            {
                JobCheckAdd(Row, JobCheckLevel.OK, "feeder " + TapeID + ": hole found");
            }
        }

        // ==========================================================================================
        // Whole job

        // Geometry only; returns false if any row has an error. Used by the button and as pre-flight.
        private bool JobCheckGeometry_m()
        {
            bool ok = true;
            JobCheckConsumed.Clear();
            for (int i = 0; i < JobData_GridView.RowCount; i++)
            {
                if (!JobCheckRowGeometry(i)) ok = false;
            }
            return ok;
        }

        private void CheckJob_button_Click(object sender, EventArgs e)
        {
            List<int> Rows = new List<int>();
            for (int i = 0; i < JobData_GridView.RowCount; i++) Rows.Add(i);
            CheckJobRows(Rows, "Job check");
        }

        private void CheckSelected_button_Click(object sender, EventArgs e)
        {
            List<int> Rows = new List<int>();
            for (int i = 0; i < JobData_GridView.RowCount; i++)
            {
                foreach (DataGridViewCell c in JobData_GridView.Rows[i].Cells)
                {
                    if (c.Selected)
                    {
                        Rows.Add(i);
                        break;
                    }
                }
            }
            if (Rows.Count == 0)
            {
                ShowMessageBox("No rows selected.", "Check selected", MessageBoxButtons.OK);
                return;
            }
            CheckJobRows(Rows, "Job check, " + Rows.Count.ToString(CultureInfo.InvariantCulture) + " selected row(s)");
        }

        private void CheckJobRows(List<int> Rows, string Title)
        {
            if (JobData_GridView.RowCount < 1)
            {
                ShowMessageBox("No Job loaded.", "No Job", MessageBoxButtons.OK);
                return;
            }
            JobCheckItems.Clear();
            bool Measure = CheckJobMeasure_checkBox.Checked;
            if (Measure)
            {
                if (!CheckPositionConfidence()) return;
                if (MovementIsBusy_m()) return;
                if (!ValidMeasurement_checkBox.Checked)
                {
                    DisplayText("Job check: measuring fiducials");
                    if (!BuildMachineCoordinateData_m())
                    {
                        JobCheckAdd(Rows[0], JobCheckLevel.Error, "fiducial measurement failed; placement positions checked with nominal coordinates");
                    }
                }
            }
            DisplayText("Job check: geometry");
            JobCheckConsumed.Clear();
            foreach (int i in Rows)
            {
                JobCheckRowGeometry(i);
            }
            if (Measure)
            {
                DisplayText("Job check: measuring tapes");
                JobCheckMeasured.Clear();
                foreach (int i in Rows)
                {
                    JobCheckRowMeasure(i);
                }
                CNC_Park();
            }
            ShowJobCheckReport(Title + (Measure ? " (measured)" : " (geometry only)"));
        }

        private void ShowJobCheckReport(string Title)
        {
            ShowJobCheckReport(Title, false);
        }

        // With AskToStart, the window has "Start anyway" and "Cancel"; returns true for Start anyway.
        private bool ShowJobCheckReport(string Title, bool AskToStart)
        {
            int Errors = 0, Warnings = 0;
            foreach (JobCheckItem it in JobCheckItems)
            {
                if (it.Level == JobCheckLevel.Error) Errors++;
                if (it.Level == JobCheckLevel.Warning) Warnings++;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(Errors.ToString(CultureInfo.InvariantCulture) + " error(s), " + Warnings.ToString(CultureInfo.InvariantCulture) + " warning(s)"
                + ((Errors == 0) ? " - the job can run." : " - rows with errors place what they can and skip the rest; Place All asks before starting."));
            sb.AppendLine("Machine " + Fmt(-Math.Abs(Setting.General_NegativeX)) + ".." + Fmt(Setting.General_MachineSizeX) + " x "
                + Fmt(-Math.Abs(Setting.General_NegativeY)) + ".." + Fmt(Setting.General_MachineSizeY)
                + ", nozzle offset " + Fmt(Setting.DownCam_NozzleOffsetX) + ", " + Fmt(Setting.DownCam_NozzleOffsetY)
                + ": the nozzle reaches X " + Fmt(-Math.Abs(Setting.General_NegativeX) - Setting.DownCam_NozzleOffsetX + JobCheckMargin) + ".."
                + Fmt(Setting.General_MachineSizeX - Setting.DownCam_NozzleOffsetX - JobCheckMargin)
                + ", Y " + Fmt(-Math.Abs(Setting.General_NegativeY) - Setting.DownCam_NozzleOffsetY + JobCheckMargin) + ".."
                + Fmt(Setting.General_MachineSizeY - Setting.DownCam_NozzleOffsetY - JobCheckMargin));
            sb.AppendLine();
            foreach (JobCheckItem it in JobCheckItems)
            {
                string tag = (it.Level == JobCheckLevel.Error) ? "ERROR  " : ((it.Level == JobCheckLevel.Warning) ? "warning" : "ok     ");
                sb.AppendLine(tag + "  row " + (it.Row + 1).ToString(CultureInfo.InvariantCulture).PadLeft(3) + "  " + JobRowName(it.Row) + ": " + it.Text);
            }
            using (Form f = new Form())
            {
                f.Text = Title;
                f.StartPosition = FormStartPosition.CenterParent;
                f.Size = new Size(900, 500);
                f.MinimizeBox = false;
                TextBox t = new TextBox();
                t.Multiline = true;
                t.ReadOnly = true;
                t.ScrollBars = ScrollBars.Both;
                t.WordWrap = false;
                t.Font = new Font("Consolas", 9.0F);
                t.Dock = DockStyle.Fill;
                t.Text = sb.ToString();
                Panel p = new Panel();
                p.Dock = DockStyle.Bottom;
                p.Height = 32;
                Button b = new Button();
                b.Text = AskToStart ? "Start anyway" : "Close";
                b.Size = new Size(110, 26);
                b.Location = new Point(f.ClientSize.Width - (AskToStart ? 240 : 120), 3);
                b.DialogResult = DialogResult.OK;
                p.Controls.Add(b);
                f.AcceptButton = b;
                if (AskToStart)
                {
                    Button c = new Button();
                    c.Text = "Cancel";
                    c.Size = new Size(110, 26);
                    c.Location = new Point(f.ClientSize.Width - 120, 3);
                    c.DialogResult = DialogResult.Cancel;
                    p.Controls.Add(c);
                    f.CancelButton = c;
                }
                else
                {
                    f.CancelButton = b;
                }
                f.Controls.Add(t);
                f.Controls.Add(p);
                t.Select(0, 0);
                return f.ShowDialog(this) == DialogResult.OK;
            }
        }

        // Called by Place All after the fiducials. With errors, shows the report and asks whether to start
        // anyway (rows then place what they can and skip the rest). Returns false if the operator cancels.
        private bool JobPreflight_m()
        {
            JobCheckItems.Clear();
            if (JobCheckGeometry_m())
            {
                return true;
            }
            if (ShowJobCheckReport("Job check: problems found - start anyway?", true))
            {
                DisplayText("Job check found errors, starting anyway: rows place what they can.", KnownColor.DarkRed, true);
                return true;
            }
            DisplayText("Job check found errors, not starting.", KnownColor.DarkRed, true);
            return false;
        }

        // ==========================================================================================
        // Used by the run loop: how many of the parts Next .. Next+Needed-1 of a feeder can be picked
        // (consecutive from Next; the first unreachable one ends it).

        // How many of the parts Next+Skip .. Next+Skip+Count-1 of a tape have their hole within camera reach
        // (consecutive; the first unreachable one ends it). Holes 1-3 are needed too when the count starts
        // at part 1 (cut-leader extrapolation).
        private int ReachableHoles(int TapeNum, int Skip, int Count)
        {
            int Next;
            if (!int.TryParse(TapeCellString(TapeNum, "NextPart_Column"), out Next)) return Count;
            int From = Next + Skip;
            if (From == 1)
            {
                for (int p = 1; p <= 3; p++)
                {
                    double HX, HY;
                    if (!Tapes.GetPartHoleNominal_m(TapeNum, p, out HX, out HY)) return Count;
                    if (!CameraCanReach(HX, HY)) return 0;
                }
            }
            for (int p = From; p < From + Count; p++)
            {
                double HX, HY;
                if (!Tapes.GetPartHoleNominal_m(TapeNum, p, out HX, out HY)) return Count;
                if (!CameraCanReach(HX, HY)) return p - From;
            }
            return Count;
        }

        private int ReachablePickups(int TapeNum, int Needed)
        {
            int Next;
            if (!int.TryParse(TapeCellString(TapeNum, "NextPart_Column"), out Next)) return Needed;
            for (int p = Next; p < Next + Needed; p++)
            {
                double X, Y, A;
                if (UseCoordinatesDirectly(TapeNum))
                {
                    bool inc;
                    if (!FindPartWithDirectCoordinates_m(TapeNum, p, out X, out Y, out A, out inc)) return Needed;
                    bool reach = UseNozzleCoordinates(TapeNum) ? CameraCanReach(X, Y) : NozzleCanReach(X, Y);
                    if (!reach) return p - Next;
                }
                else
                {
                    double HX, HY;
                    if (!Tapes.GetPartHoleNominal_m(TapeNum, p, out HX, out HY)) return Needed;
                    if (!CameraCanReach(HX, HY)) return p - Next;
                    if (!Tapes.GetPartLocationFromHolePosition_m(TapeNum, HX, HY, p, out X, out Y, out A)) return Needed;
                    if (!NozzleCanReach(X, Y)) return p - Next;
                }
            }
            return Needed;
        }
    }
}
