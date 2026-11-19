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
    //   from the down camera (the nozzle offset), so a part the camera can see near the low-X / low-Y edge
    //   is out of the nozzle's reach, and vice versa at the far edges. Pickup positions of every part the
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

        // The nozzle is at gantry + offset, so to put the nozzle at (X, Y) the gantry goes to (X - offset).
        private bool NozzleCanReach(double X, double Y)
        {
            return CameraCanReach(X - Setting.DownCam_NozzleOffsetX, Y - Setting.DownCam_NozzleOffsetY);
        }

        private static string Fmt(double v)
        {
            return v.ToString("0.0", CultureInfo.InvariantCulture);
        }

        // ==========================================================================================
        // Geometry checks for one row. Returns false on an error (warnings don't count).

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
            int Capacity;
            if (int.TryParse(TapeCellString(TapeNum, "Capacity_Column"), out Capacity) && (Capacity > 0))
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
                    + (CameraBad ? "" : " (gantry would be at " + Fmt(BadX - Setting.DownCam_NozzleOffsetX) + ", " + Fmt(BadY - Setting.DownCam_NozzleOffsetY) + ")"));
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
                    + Next.ToString(CultureInfo.InvariantCulture) + "-" + (Next + Needed - 1).ToString(CultureInfo.InvariantCulture) + ", reach OK");
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
                    bool placed = false;
                    if (!AlreadyPlaced_m(c.Trim(), ref placed)) return;
                    if (!placed) Needed++;
                }
            }
            if (Needed == 0) return;

            double Pitch;
            if (!double.TryParse(TapeCellString(TapeNum, "Pitch_Column").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out Pitch)) return;

            // PrepareForFastPlacement_m does exactly the measurement we want, including the cut-leader and
            // short-tape handling, and only touches the Fast* properties.
            if (!Tapes.PrepareForFastPlacement_m(TapeID, Needed))
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
            for (int i = 0; i < JobData_GridView.RowCount; i++)
            {
                if (!JobCheckRowGeometry(i)) ok = false;
            }
            return ok;
        }

        private void CheckJob_button_Click(object sender, EventArgs e)
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
                        JobCheckAdd(0, JobCheckLevel.Error, "fiducial measurement failed; placement positions checked with nominal coordinates");
                    }
                }
            }
            DisplayText("Job check: geometry");
            JobCheckGeometry_m();
            if (Measure)
            {
                DisplayText("Job check: measuring tapes");
                for (int i = 0; i < JobData_GridView.RowCount; i++)
                {
                    JobCheckRowMeasure(i);
                }
                CNC_Park();
            }
            ShowJobCheckReport(Measure ? "Job check (measured)" : "Job check (geometry only)");
        }

        private void ShowJobCheckReport(string Title)
        {
            int Errors = 0, Warnings = 0;
            foreach (JobCheckItem it in JobCheckItems)
            {
                if (it.Level == JobCheckLevel.Error) Errors++;
                if (it.Level == JobCheckLevel.Warning) Warnings++;
            }
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.AppendLine(Errors.ToString(CultureInfo.InvariantCulture) + " error(s), " + Warnings.ToString(CultureInfo.InvariantCulture) + " warning(s)"
                + ((Errors == 0) ? " - the job can run." : " - Place All will refuse to start until the errors are fixed."));
            sb.AppendLine("Machine " + Fmt(-Math.Abs(Setting.General_NegativeX)) + ".." + Fmt(Setting.General_MachineSizeX) + " x "
                + Fmt(-Math.Abs(Setting.General_NegativeY)) + ".." + Fmt(Setting.General_MachineSizeY)
                + ", nozzle offset " + Fmt(Setting.DownCam_NozzleOffsetX) + ", " + Fmt(Setting.DownCam_NozzleOffsetY)
                + ": the nozzle reaches X " + Fmt(-Math.Abs(Setting.General_NegativeX) + Setting.DownCam_NozzleOffsetX + JobCheckMargin) + ".."
                + Fmt(Setting.General_MachineSizeX + Setting.DownCam_NozzleOffsetX - JobCheckMargin)
                + ", Y " + Fmt(-Math.Abs(Setting.General_NegativeY) + Setting.DownCam_NozzleOffsetY + JobCheckMargin) + ".."
                + Fmt(Setting.General_MachineSizeY + Setting.DownCam_NozzleOffsetY - JobCheckMargin));
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
                Button b = new Button();
                b.Text = "Close";
                b.Dock = DockStyle.Bottom;
                b.DialogResult = DialogResult.OK;
                f.Controls.Add(t);
                f.Controls.Add(b);
                f.AcceptButton = b;
                f.CancelButton = b;
                t.Select(0, 0);
                f.ShowDialog(this);
            }
        }

        // Called by Place All before anything moves. Returns false (after showing the report) on an error.
        private bool JobPreflight_m()
        {
            JobCheckItems.Clear();
            if (JobCheckGeometry_m())
            {
                return true;
            }
            DisplayText("Job check found errors, not starting.", KnownColor.DarkRed, true);
            ShowJobCheckReport("Job check: not starting");
            return false;
        }
    }
}
