using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LitePlacer
{
    // A panel is a definition applied to single-board CAD data, not a transformation of it (issue #5).
    // The single-board rows and the definition are what we keep; the CAD grid shows the generated panel
    // because the run loop reads it. Both are saved in the job file (#PANEL lines after the version line),
    // so a reloaded job comes back panelized and can be re-generated or reverted at any time.
    public class PanelDefinition
    {
        public double XFirstOffset = 0.0;       // lower-left board's origin on the panel
        public double YFirstOffset = 0.0;
        public int XRepeats = 1;
        public int YRepeats = 1;
        public double XIncrement = 0.0;
        public double YIncrement = 0.0;
        public bool UseBoardFids = false;       // false: fiducials from PanelFids; true: the single board's fiducials, outermost copies
        public string Strategy = STRATEGY_PANEL;    // how fiducials are used at run time
        public double HybridTolerance = 0.1;    // mm: hybrid strategy re-measures a board whose check fiducial is further off than this
        public HashSet<string> Skipped = new HashSet<string>(StringComparer.Ordinal);   // boards "r_c" not to place
        public List<PanelFid> PanelFids = new List<PanelFid>();
        public string SingleBoardFiducials = "";   // the single board's fiducial list (job row), "" if none
        public List<string[]> SingleBoardRows = new List<string[]>();   // Component, Value, Footprint, X, Y, Rotation

        public const string STRATEGY_PANEL = "Panel";    // one transform from the panel fiducials
        public const string STRATEGY_BOARD = "Board";    // every board measured on its own fiducials
        public const string STRATEGY_HYBRID = "Hybrid";  // panel transform, each board checked on one fiducial, re-measured if off

        public class PanelFid
        {
            public string Name;
            public double X;
            public double Y;
        }

        public static string BoardKey(int Row, int Column)
        {
            return Row.ToString(CultureInfo.InvariantCulture) + "_" + Column.ToString(CultureInfo.InvariantCulture);
        }

        public string CopyName(string Designator, int Row, int Column)
        {
            return Designator + "_" + BoardKey(Row, Column);
        }

        // "<designator>_<row>_<col>" -> designator, row, col
        public static bool ParseCopyName(string Name, out string Designator, out int Row, out int Column)
        {
            Designator = Name;
            Row = 0;
            Column = 0;
            string[] Parts = Name.Trim().Split('_');
            if (Parts.Length < 3) return false;
            if (!int.TryParse(Parts[Parts.Length - 2], out Row)) return false;
            if (!int.TryParse(Parts[Parts.Length - 1], out Column)) return false;
            Designator = string.Join("_", Parts, 0, Parts.Length - 2);
            return true;
        }

        public static bool LooksPanelized(string Name)
        {
            string d;
            int r, c;
            return ParseCopyName(Name, out d, out r, out c);
        }

        public bool IsSkipped(string Component)
        {
            string d;
            int r, c;
            if (!ParseCopyName(Component, out d, out r, out c)) return false;
            return Skipped.Contains(BoardKey(r, c));
        }

        public bool IsSkipped(int Row, int Column)
        {
            return Skipped.Contains(BoardKey(Row, Column));
        }

        public int Boards
        {
            get { return XRepeats * YRepeats; }
        }

        public int ActiveBoards
        {
            get { return Boards - Skipped.Count(k => { int r, c; return ParseKey(k, out r, out c) && (r >= 1) && (r <= YRepeats) && (c >= 1) && (c <= XRepeats); }); }
        }

        public static bool ParseKey(string Key, out int Row, out int Column)
        {
            Row = 0;
            Column = 0;
            string[] p = Key.Split('_');
            return (p.Length == 2) && int.TryParse(p[0], out Row) && int.TryParse(p[1], out Column);
        }

        // Geometry decides whether generated rows keep their names and positions
        public bool SameGeometry(PanelDefinition Other)
        {
            if (Other == null) return false;
            return (Math.Abs(XFirstOffset - Other.XFirstOffset) < 1e-6) && (Math.Abs(YFirstOffset - Other.YFirstOffset) < 1e-6)
                && (XRepeats == Other.XRepeats) && (YRepeats == Other.YRepeats)
                && (Math.Abs(XIncrement - Other.XIncrement) < 1e-6) && (Math.Abs(YIncrement - Other.YIncrement) < 1e-6)
                && (UseBoardFids == Other.UseBoardFids)
                && (PanelFids.Count == Other.PanelFids.Count)
                && PanelFids.Zip(Other.PanelFids, (a, b) => (a.Name == b.Name) && (Math.Abs(a.X - b.X) < 1e-6) && (Math.Abs(a.Y - b.Y) < 1e-6)).All(x => x);
        }

        public PanelDefinition Clone()
        {
            PanelDefinition p = new PanelDefinition();
            p.XFirstOffset = XFirstOffset; p.YFirstOffset = YFirstOffset; p.XRepeats = XRepeats; p.YRepeats = YRepeats;
            p.XIncrement = XIncrement; p.YIncrement = YIncrement; p.UseBoardFids = UseBoardFids; p.Strategy = Strategy;
            p.HybridTolerance = HybridTolerance; p.Skipped = new HashSet<string>(Skipped, StringComparer.Ordinal);
            p.PanelFids = PanelFids.Select(f => new PanelFid() { Name = f.Name, X = f.X, Y = f.Y }).ToList();
            p.SingleBoardFiducials = SingleBoardFiducials;
            p.SingleBoardRows = SingleBoardRows.Select(r => (string[])r.Clone()).ToList();
            return p;
        }

        // ------------------------------------------------------------------------------------------
        // Job file lines. All start with '#' so an old reader would at least see them as odd lines.
        //   #PANEL,"xoff","yoff","xrep","yrep","xinc","yinc","useboardfids","strategy","tolerance","skipped","singlefids"
        //   #PANELFID,"name","x","y"
        //   #PANELROW,"component","value","footprint","x","y","rotation"

        private static string Q(string s)
        {
            return "\"" + s.Replace("\"", "'") + "\"";
        }

        private static string D(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public List<string> ToLines()
        {
            List<string> L = new List<string>();
            L.Add("#PANEL," + Q(D(XFirstOffset)) + "," + Q(D(YFirstOffset)) + "," + Q(XRepeats.ToString(CultureInfo.InvariantCulture)) + "," + Q(YRepeats.ToString(CultureInfo.InvariantCulture))
                + "," + Q(D(XIncrement)) + "," + Q(D(YIncrement)) + "," + Q(UseBoardFids ? "true" : "false") + "," + Q(Strategy) + "," + Q(D(HybridTolerance))
                + "," + Q(string.Join(";", Skipped.ToArray())) + "," + Q(SingleBoardFiducials));
            foreach (PanelFid f in PanelFids)
            {
                L.Add("#PANELFID," + Q(f.Name) + "," + Q(D(f.X)) + "," + Q(D(f.Y)));
            }
            foreach (string[] r in SingleBoardRows)
            {
                L.Add("#PANELROW," + string.Join(",", r.Select(Q).ToArray()));
            }
            return L;
        }

        // Feeds one job file line; returns true if it was a panel line (consumed).
        public static bool TryParseLine(string Line, Func<string, List<string>> Split, ref PanelDefinition Def, out bool Bad)
        {
            Bad = false;
            if (!Line.StartsWith("#PANEL", StringComparison.Ordinal)) return false;
            List<string> F = Split(Line);
            if (F == null || F.Count == 0) { Bad = true; return true; }
            try
            {
                if (F[0] == "#PANEL")
                {
                    if (F.Count < 12) { Bad = true; return true; }
                    Def = new PanelDefinition();
                    Def.XFirstOffset = double.Parse(F[1], CultureInfo.InvariantCulture);
                    Def.YFirstOffset = double.Parse(F[2], CultureInfo.InvariantCulture);
                    Def.XRepeats = int.Parse(F[3], CultureInfo.InvariantCulture);
                    Def.YRepeats = int.Parse(F[4], CultureInfo.InvariantCulture);
                    Def.XIncrement = double.Parse(F[5], CultureInfo.InvariantCulture);
                    Def.YIncrement = double.Parse(F[6], CultureInfo.InvariantCulture);
                    Def.UseBoardFids = (F[7].ToLowerInvariant() == "true");
                    Def.Strategy = F[8];
                    Def.HybridTolerance = double.Parse(F[9], CultureInfo.InvariantCulture);
                    Def.Skipped = new HashSet<string>(F[10].Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
                    Def.SingleBoardFiducials = F[11];
                }
                else if (F[0] == "#PANELFID")
                {
                    if ((Def == null) || (F.Count < 4)) { Bad = true; return true; }
                    Def.PanelFids.Add(new PanelFid() { Name = F[1], X = double.Parse(F[2], CultureInfo.InvariantCulture), Y = double.Parse(F[3], CultureInfo.InvariantCulture) });
                }
                else if (F[0] == "#PANELROW")
                {
                    if ((Def == null) || (F.Count < 7)) { Bad = true; return true; }
                    Def.SingleBoardRows.Add(new string[] { F[1], F[2], F[3], F[4], F[5], F[6] });
                }
                else
                {
                    Bad = true;
                }
            }
            catch (Exception)
            {
                Bad = true;
            }
            return true;
        }
    }

    // ==============================================================================================
    // MainForm side: the current panel, applying it to the CAD grid, reverting.

    public partial class FormMain : Form
    {
        public PanelDefinition CurrentPanel = null;     // null = single board

        // The single-board rows as string[6] {Component, Value, Footprint, X, Y, Rotation} from the CAD grid as it is now
        private List<string[]> GridRowsAsSingleBoard()
        {
            List<string[]> Rows = new List<string[]>();
            foreach (DataGridViewRow Row in CadData_GridView.Rows)
            {
                if (Row.IsNewRow) continue;
                Rows.Add(new string[] {
                    CellText(Row, "CADdataComponentColumn"), CellText(Row, "CADdataValueColumn"), CellText(Row, "CADdataFootprintColumn"),
                    CellText(Row, "CADdataXnominalColumn"), CellText(Row, "CADdataYnominalColumn"), CellText(Row, "CADdataRotationNominalColumn") });
            }
            return Rows;
        }

        public List<string[]> SingleBoardRowsFromGrid()
        {
            return (CurrentPanel != null) ? CurrentPanel.SingleBoardRows : GridRowsAsSingleBoard();
        }

        // The single board's fiducial list: from the panel if there is one, else the job's fiducials row
        public string SingleBoardFiducialsNow()
        {
            if (CurrentPanel != null) return CurrentPanel.SingleBoardFiducials;
            int FidRow;
            return FindFiducialsRowQuiet(out FidRow) ? CellText(JobData_GridView.Rows[FidRow], "JobdataComponentsColumn") : "";
        }

        // Skip grid shared by the Panelize and Boards dialogs: columns 1..XRepeats, rows YRepeats..1 (top = far side)
        public static void BuildSkipGrid(DataGridView g, int XRepeats, int YRepeats, HashSet<string> Skipped)
        {
            g.SuspendLayout();
            g.Columns.Clear();
            g.Rows.Clear();
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.RowHeadersVisible = true;
            g.RowHeadersWidth = 60;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            for (int c = 1; c <= XRepeats; c++)
            {
                DataGridViewCheckBoxColumn col = new DataGridViewCheckBoxColumn();
                col.HeaderText = "col " + c.ToString(CultureInfo.InvariantCulture);
                col.Name = "c" + c.ToString(CultureInfo.InvariantCulture);
                col.Width = 44;
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                g.Columns.Add(col);
            }
            for (int r = YRepeats; r >= 1; r--)
            {
                int idx = g.Rows.Add();
                g.Rows[idx].HeaderCell.Value = "row " + r.ToString(CultureInfo.InvariantCulture);
                g.Rows[idx].Tag = r;
                for (int c = 1; c <= XRepeats; c++)
                {
                    g.Rows[idx].Cells[c - 1].Value = Skipped.Contains(PanelDefinition.BoardKey(r, c));
                }
            }
            g.ResumeLayout();
        }

        public static HashSet<string> ReadSkipGrid(DataGridView g)
        {
            HashSet<string> Skipped = new HashSet<string>(StringComparer.Ordinal);
            foreach (DataGridViewRow Row in g.Rows)
            {
                if (Row.Tag == null) continue;
                int r = (int)Row.Tag;
                for (int c = 1; c <= g.Columns.Count; c++)
                {
                    object v = Row.Cells[c - 1].Value;
                    if ((v is bool) && (bool)v)
                    {
                        Skipped.Add(PanelDefinition.BoardKey(r, c));
                    }
                }
            }
            return Skipped;
        }

        // Boards... button: toggle skipped boards without re-panelizing
        private void PanelBoards_button_Click(object sender, EventArgs e)
        {
            if (CurrentPanel == null)
            {
                ShowMessageBox("The CAD data is not panelized. Use Panelize first.", "Single board", MessageBoxButtons.OK);
                return;
            }
            using (Form f = new Form())
            {
                f.Text = "Boards to skip";
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(Math.Max(260, 70 + 46 * CurrentPanel.XRepeats + 30), 80 + 22 * CurrentPanel.YRepeats + 50);
                f.MinimizeBox = false;
                f.MaximizeBox = false;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                Label l = new Label();
                l.Text = "Tick the boards NOT to place. Row 1 is the lower-left board.";
                l.Location = new Point(8, 8);
                l.AutoSize = true;
                DataGridView g = new DataGridView();
                g.Location = new Point(8, 30);
                g.Size = new Size(f.ClientSize.Width - 16, 25 + 22 * CurrentPanel.YRepeats + 4);
                g.ScrollBars = ScrollBars.None;
                BuildSkipGrid(g, CurrentPanel.XRepeats, CurrentPanel.YRepeats, CurrentPanel.Skipped);
                g.CurrentCellDirtyStateChanged += (o, a) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
                Button ok = new Button();
                ok.Text = "OK";
                ok.DialogResult = DialogResult.OK;
                ok.Size = new Size(75, 23);
                ok.Location = new Point(f.ClientSize.Width - 170, f.ClientSize.Height - 32);
                Button cancel = new Button();
                cancel.Text = "Cancel";
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Size = new Size(75, 23);
                cancel.Location = new Point(f.ClientSize.Width - 85, f.ClientSize.Height - 32);
                f.Controls.Add(l);
                f.Controls.Add(g);
                f.Controls.Add(ok);
                f.Controls.Add(cancel);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                if (f.ShowDialog(this) == DialogResult.OK)
                {
                    CurrentPanel.Skipped = ReadSkipGrid(g);
                    DisplayText("Panel: " + CurrentPanel.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " board(s) skipped"
                        + ((CurrentPanel.Skipped.Count > 0) ? " (" + string.Join(", ", CurrentPanel.Skipped.OrderBy(k => k).ToArray()) + ")" : ""));
                    MakeJobDataDirty();
                    SaveTempJobData();
                    Update_GridView(JobData_GridView);
                }
            }
        }

        private bool CadGridLooksPanelized()
        {
            foreach (DataGridViewRow Row in CadData_GridView.Rows)
            {
                if (Row.IsNewRow) continue;
                if (PanelDefinition.LooksPanelized(CellText(Row, "CADdataComponentColumn"))) return true;
            }
            return false;
        }

        // Preview numbers for the dialog
        public void PanelPreview(PanelDefinition Def, List<string[]> SingleRows, out int Boards, out int Components, out RectangleF BBox)
        {
            Boards = Def.ActiveBoards;
            Components = 0;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (string[] r in SingleRows)
            {
                double x, y;
                if (!double.TryParse(r[3].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) continue;
                if (!double.TryParse(r[4].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) continue;
                for (int pr = 1; pr <= Def.YRepeats; pr++)
                {
                    for (int pc = 1; pc <= Def.XRepeats; pc++)
                    {
                        if (Def.IsSkipped(pr, pc)) continue;
                        double X = Def.XFirstOffset + (pc - 1) * Def.XIncrement + x;
                        double Y = Def.YFirstOffset + (pr - 1) * Def.YIncrement + y;
                        Components++;
                        if (X < x0) x0 = X; if (X > x1) x1 = X; if (Y < y0) y0 = Y; if (Y > y1) y1 = Y;
                    }
                }
            }
            BBox = (Components > 0) ? new RectangleF((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0)) : RectangleF.Empty;
        }

        // ------------------------------------------------------------------------------------------
        // Apply a definition: regenerate the CAD grid from the single board, rebuild the job grid keeping
        // row settings, set up the fiducials row. Placed flags and machine coordinates of generated rows
        // are kept by name when the geometry is unchanged, so toggling skipped boards or re-opening the
        // dialog does not force a new fiducial measurement.
        public bool ApplyPanel_m(PanelDefinition Def)
        {
            // Source rows
            if (CurrentPanel == null)
            {
                // The grid holds the single board (unless it is a leftover panel from before this feature)
                if (CadGridLooksPanelized())
                {
                    if (Def.SingleBoardRows.Count == 0)
                    {
                        ShowMessageBox(
                            "The CAD data already looks panelized and the single-board data is not available.\n" +
                            "Load the single-board CAD file again before panelizing.",
                            "Already panelized", MessageBoxButtons.OK);
                        return false;
                    }
                }
                else
                {
                    Def.SingleBoardRows = GridRowsAsSingleBoard();
                    int FidRow;
                    Def.SingleBoardFiducials = FindFiducialsRowQuiet(out FidRow) ? CellText(JobData_GridView.Rows[FidRow], "JobdataComponentsColumn") : "";
                }
            }
            else if (Def.SingleBoardRows.Count == 0)
            {
                Def.SingleBoardRows = CurrentPanel.SingleBoardRows;
                Def.SingleBoardFiducials = CurrentPanel.SingleBoardFiducials;
            }
            if (Def.SingleBoardRows.Count == 0)
            {
                ShowMessageBox("No CAD data to panelize.", "No data", MessageBoxButtons.OK);
                return false;
            }

            bool KeepState = (CurrentPanel != null) && Def.SameGeometry(CurrentPanel);
            Dictionary<string, object[]> Kept = new Dictionary<string, object[]>(StringComparer.Ordinal);
            if (KeepState)
            {
                foreach (DataGridViewRow Row in CadData_GridView.Rows)
                {
                    if (Row.IsNewRow) continue;
                    Kept[CellText(Row, "CADdataComponentColumn")] = new object[] {
                        Row.Cells["CADdataPlacedColumn"].Value, Row.Cells["CADdataXmachineColumn"].Value,
                        Row.Cells["CADdataYmachineColumn"].Value, Row.Cells["CADdataRotationMachineColumn"].Value };
                }
            }
            Dictionary<string, object[]> JobSnap = SnapshotJobSettings();

            // Generate
            CadData_GridView.SuspendLayout();
            try
            {
                CadData_GridView.Rows.Clear();
                foreach (string[] r in Def.SingleBoardRows)
                {
                    double x, y;
                    if (!double.TryParse(r[3].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                        !double.TryParse(r[4].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                    {
                        ShowMessageBox("Error in " + r[0] + " coordinate data.", "CAD data error", MessageBoxButtons.OK);
                        return false;
                    }
                    for (int pr = 1; pr <= Def.YRepeats; pr++)
                    {
                        for (int pc = 1; pc <= Def.XRepeats; pc++)
                        {
                            string Name = Def.CopyName(r[0], pr, pc);
                            double X = Def.XFirstOffset + (pc - 1) * Def.XIncrement + x;
                            double Y = Def.YFirstOffset + (pr - 1) * Def.YIncrement + y;
                            AddCadRow(Name, r[1], r[2], X.ToString("0.000", CultureInfo.InvariantCulture), Y.ToString("0.000", CultureInfo.InvariantCulture), r[5], Kept);
                        }
                    }
                }
                // Fiducials
                if (!Def.UseBoardFids)
                {
                    // Panel fiducials: the board fiducial copies are not needed for the panel-only strategy,
                    // but per-board / hybrid need them, so they stay as ordinary CAD rows; the job row "Fiducial / On panel" is the panel set.
                    foreach (PanelDefinition.PanelFid f in Def.PanelFids)
                    {
                        AddCadRow(f.Name, "Fiducial", "On panel", f.X.ToString("0.000", CultureInfo.InvariantCulture), f.Y.ToString("0.000", CultureInfo.InvariantCulture), "0.0", Kept);
                    }
                }
            }
            finally
            {
                CadData_GridView.ResumeLayout();
            }

            CurrentPanel = Def;
            RebuildJobDataKeepingSettings(JobSnap);
            if (!SetupPanelFiducialsRow(Def))
            {
                return false;
            }
            if (!KeepState)
            {
                ValidMeasurement_checkBox.Checked = false;
            }
            Update_GridView(CadData_GridView);
            Update_GridView(JobData_GridView);
            DisplayText("Panel: " + Def.XRepeats.ToString(CultureInfo.InvariantCulture) + " x " + Def.YRepeats.ToString(CultureInfo.InvariantCulture)
                + " boards" + ((Def.Skipped.Count > 0) ? ", " + Def.Skipped.Count.ToString(CultureInfo.InvariantCulture) + " skipped" : "")
                + ", fiducials " + (Def.UseBoardFids ? "from the boards" : "on the panel") + ", strategy " + Def.Strategy
                + (KeepState ? " (placed flags and measured positions kept)" : ""));
            SaveTempCADdata();
            SaveTempJobData();
            return true;
        }

        private void AddCadRow(string Name, string Value, string Footprint, string X, string Y, string Rot, Dictionary<string, object[]> Kept)
        {
            CadData_GridView.Rows.Add();
            DataGridViewRow R = CadData_GridView.Rows[CadData_GridView.RowCount - 1];
            R.Cells["CADdataComponentColumn"].Value = Name;
            R.Cells["CADdataValueColumn"].Value = Value;
            R.Cells["CADdataFootprintColumn"].Value = Footprint;
            R.Cells["CADdataXnominalColumn"].Value = X;
            R.Cells["CADdataYnominalColumn"].Value = Y;
            R.Cells["CADdataRotationNominalColumn"].Value = Rot;
            object[] k;
            if (Kept.TryGetValue(Name, out k))
            {
                R.Cells["CADdataPlacedColumn"].Value = k[0];
                R.Cells["CADdataXmachineColumn"].Value = k[1];
                R.Cells["CADdataYmachineColumn"].Value = k[2];
                R.Cells["CADdataRotationMachineColumn"].Value = k[3];
            }
            else
            {
                R.Cells["CADdataPlacedColumn"].Value = false;
                R.Cells["CADdataXmachineColumn"].Value = "Nan";
                R.Cells["CADdataYmachineColumn"].Value = "Nan";
                R.Cells["CADdataRotationMachineColumn"].Value = "Nan";
            }
        }

        // The job's fiducials row for a panel: panel fids -> the "Fiducial / On panel" row; board fids -> the
        // four outermost copies of the single board's fiducials.
        private bool SetupPanelFiducialsRow(PanelDefinition Def)
        {
            string[] OriginalFids = string.IsNullOrWhiteSpace(Def.SingleBoardFiducials) ? new string[0] : Def.SingleBoardFiducials.Split(',').Select(s => s.Trim()).ToArray();
            if (!Def.UseBoardFids)
            {
                // Rows made of the single board's fiducial copies must not be the job's fiducials row
                for (int r = 0; r < JobData_GridView.RowCount; r++)
                {
                    if (JobRowIsFiducials(r))
                    {
                        string[] Members = CellText(JobData_GridView.Rows[r], "JobdataComponentsColumn").Split(',');
                        if (!Members.Any(m => Def.PanelFids.Any(f => f.Name == m.Trim())))
                        {
                            // The single board's fiducial copies: kept in the CAD data for the per-board
                            // strategies, never placed, and not the job's fiducials row (skipped rows don't count)
                            JobData_GridView.Rows[r].Cells["JobSkipColumn"].Value = true;
                        }
                    }
                }
                for (int r = 0; r < JobData_GridView.RowCount; r++)
                {
                    if ((CellText(JobData_GridView.Rows[r], "JobDataValueColumn") == "Fiducial") && (CellText(JobData_GridView.Rows[r], "JobDataFootprintColumn") == "On panel"))
                    {
                        AssignFiducialComponent(r);
                        return true;
                    }
                }
                ShowMessageBox("Panel fiducials row not found in the job data.", "Fiducials data error", MessageBoxButtons.OK);
                return false;
            }

            if (OriginalFids.Length == 0)
            {
                ShowMessageBox("Board fiducials not found or indicated. Mark the fiducials row (Step = Fiducials) on the single board first.", "No fiducials", MessageBoxButtons.OK);
                return false;
            }
            int FiducialIndex = -1;
            for (int r = 0; r < JobData_GridView.RowCount; r++)
            {
                string[] Members = CellText(JobData_GridView.Rows[r], "JobdataComponentsColumn").Split(',');
                if (Members.Any(m => { string d; int rr, cc; return PanelDefinition.ParseCopyName(m.Trim(), out d, out rr, out cc) && (d == OriginalFids[0]); }))
                {
                    FiducialIndex = r;
                    break;
                }
            }
            if (FiducialIndex < 0)
            {
                ShowMessageBox("Fiducial " + OriginalFids[0] + " not found in the panelized job data.", "Fiducials data error", MessageBoxButtons.OK);
                return false;
            }
            // Outermost copies: lowest-left, highest-left, lowest-right, highest-right among all fiducial copies
            string LL = null, HL = null, LR = null, HR = null;
            double dLL = double.MaxValue, dHL = double.MaxValue, dLR = double.MaxValue, dHR = double.MaxValue;
            double m = 10000.0;
            foreach (DataGridViewRow Row in CadData_GridView.Rows)
            {
                if (Row.IsNewRow) continue;
                string Name = CellText(Row, "CADdataComponentColumn");
                string d;
                int rr, cc;
                if (!PanelDefinition.ParseCopyName(Name, out d, out rr, out cc) || !OriginalFids.Contains(d)) continue;
                double X, Y;
                if (!double.TryParse(CellText(Row, "CADdataXnominalColumn").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out X)) continue;
                if (!double.TryParse(CellText(Row, "CADdataYnominalColumn").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out Y)) continue;
                double t;
                t = Dist(X, Y, -m, -m); if (t < dLL) { dLL = t; LL = Name; }
                t = Dist(X, Y, -m, m); if (t < dHL) { dHL = t; HL = Name; }
                t = Dist(X, Y, m, -m); if (t < dLR) { dLR = t; LR = Name; }
                t = Dist(X, Y, m, m); if (t < dHR) { dHR = t; HR = Name; }
            }
            if ((LL == null) || (new HashSet<string>(new[] { LL, HL, LR, HR }, StringComparer.Ordinal).Count < 4))
            {
                ShowMessageBox("Current fiducials don't spread out.", "Fiducials data error", MessageBoxButtons.OK);
                return false;
            }
            JobData_GridView.Rows[FiducialIndex].Cells["JobdataComponentsColumn"].Value = LL + "," + HL + "," + LR + "," + HR;
            JobData_GridView.Rows[FiducialIndex].Cells["JobdataCountColumn"].Value = "4";
            AssignFiducialComponent(FiducialIndex);
            return true;
        }

        private static double Dist(double X1, double Y1, double X2, double Y2)
        {
            return Math.Sqrt((X1 - X2) * (X1 - X2) + (Y1 - Y2) * (Y1 - Y2));
        }

        // FindFiducials_m without the dialogs: is there a fiducials row?
        private bool FindFiducialsRowQuiet(out int FidRow)
        {
            FidRow = -1;
            for (int i = 0; i < JobData_GridView.RowCount; i++)
            {
                if (JobRowIsFiducials(i))
                {
                    FidRow = i;
                    return true;
                }
            }
            return false;
        }

        // ------------------------------------------------------------------------------------------
        // Back to the single board

        public bool RevertPanel_m()
        {
            if (CurrentPanel == null)
            {
                ShowMessageBox("The CAD data is not panelized.", "Single board", MessageBoxButtons.OK);
                return false;
            }
            if (CurrentPanel.SingleBoardRows.Count == 0)
            {
                ShowMessageBox("The single-board data is not available. Load the single-board CAD file again.", "No single-board data", MessageBoxButtons.OK);
                return false;
            }
            Dictionary<string, object[]> JobSnap = SnapshotJobSettings();
            string Fids = CurrentPanel.SingleBoardFiducials;
            Dictionary<string, object[]> None = new Dictionary<string, object[]>(StringComparer.Ordinal);
            CadData_GridView.SuspendLayout();
            try
            {
                CadData_GridView.Rows.Clear();
                foreach (string[] r in CurrentPanel.SingleBoardRows)
                {
                    AddCadRow(r[0], r[1], r[2], r[3], r[4], r[5], None);
                }
            }
            finally
            {
                CadData_GridView.ResumeLayout();
            }
            CurrentPanel = null;
            RebuildJobDataKeepingSettings(JobSnap);
            if (!string.IsNullOrWhiteSpace(Fids))
            {
                for (int r = 0; r < JobData_GridView.RowCount; r++)
                {
                    string[] Members = CellText(JobData_GridView.Rows[r], "JobdataComponentsColumn").Split(',').Select(s => s.Trim()).ToArray();
                    if (Members.Contains(Fids.Split(',')[0].Trim()))
                    {
                        AssignFiducialComponent(r);
                        break;
                    }
                }
            }
            ValidMeasurement_checkBox.Checked = false;
            Update_GridView(CadData_GridView);
            Update_GridView(JobData_GridView);
            DisplayText("Panel reverted to the single board.");
            SaveTempCADdata();
            SaveTempJobData();
            return true;
        }

        // ------------------------------------------------------------------------------------------
        // Job grid rebuild keeping per-row settings (moved here from PanelizeForm so both paths share it)

        private static readonly string[] JobSettingColumns = {
            "JobdataTapeColumn", "JobdataMethodColumn", "JobSkipColumn", "JobFindFastColumn", "JobDataNozzleColumn",
            "JobUseUpCamColumn", "PlacementParameterColumn", "JobManualAssistColumn" };

        private static string JobRowKey(DataGridViewRow Row)
        {
            return CellText(Row, "JobDataValueColumn") + "|" + CellText(Row, "JobDataFootprintColumn");
        }

        public Dictionary<string, object[]> SnapshotJobSettings()
        {
            Dictionary<string, object[]> Snap = new Dictionary<string, object[]>(StringComparer.Ordinal);
            foreach (DataGridViewRow Row in JobData_GridView.Rows)
            {
                if (Row.IsNewRow) continue;
                object[] Values = new object[JobSettingColumns.Length];
                for (int i = 0; i < JobSettingColumns.Length; i++)
                {
                    Values[i] = Row.Cells[JobSettingColumns[i]].Value;
                }
                Snap[JobRowKey(Row)] = Values;
            }
            return Snap;
        }

        public void RebuildJobDataKeepingSettings(Dictionary<string, object[]> Snap)
        {
            FillJobData_GridView();
            FillDefaultJobValues();
            foreach (DataGridViewRow Row in JobData_GridView.Rows)
            {
                object[] Values;
                if (Snap.TryGetValue(JobRowKey(Row), out Values))
                {
                    for (int i = 0; i < JobSettingColumns.Length; i++)
                    {
                        Row.Cells[JobSettingColumns[i]].Value = Values[i];   // null = inherited, keep it
                    }
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // After a job file with a panel block has been read: make the CAD grid match.

        private void ApplyLoadedPanel()
        {
            if (CurrentPanel == null) return;
            PanelDefinition Def = CurrentPanel;
            if (CadGridLooksPanelized())
            {
                // Temp data at startup, or a job loaded on top of its own panel: the grid is the panel already.
                DisplayText("Panel definition loaded (" + Def.XRepeats.ToString(CultureInfo.InvariantCulture) + " x " + Def.YRepeats.ToString(CultureInfo.InvariantCulture) + "), CAD data is panelized.");
                return;
            }
            if (CadData_GridView.RowCount == 0) return;
            // Fresh single-board CAD data with a panelized job: generate, preferring the CAD file just loaded
            CurrentPanel = null;
            Def.SingleBoardRows.Clear();
            if (!ApplyPanel_m(Def))
            {
                CurrentPanel = null;
            }
        }
    }
}
