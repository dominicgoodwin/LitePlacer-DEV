using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace LitePlacer
{
    // Panelize dialog: collects a PanelDefinition and hands it to MainForm.ApplyPanel_m(). The generation
    // itself lives in Panel.cs so that a loaded job, the Boards dialog and Revert all share it.
    public partial class PanelizeForm : Form
    {
        public static FormMain MainForm { get; set; }

        public const string PANELFIDUCIALS_DATAFILE = "LitePlacer.PanelFids";

        public bool OK { get; set; } = false;

        private bool Loading = true;

        public PanelizeForm(FormMain MainF)
        {
            MainForm = MainF;
            InitializeComponent();
        }

        private void PanelizeForm_Load(object sender, EventArgs e)
        {
            string path = MainForm.GetPath();
            MainForm.LoadDataGrid(path + PANELFIDUCIALS_DATAFILE, PanelFiducials_dataGridView, FormMain.DataTableType.PanelFiducials);
            // Row headers are hidden and selection is per cell, so there was no way to delete a row.
            // Delete key (when not editing a cell) removes the current row.
            PanelFiducials_dataGridView.AllowUserToDeleteRows = true;
            PanelFiducials_dataGridView.KeyDown += PanelFiducials_dataGridView_KeyDown;

            Strategy_comboBox.Items.Clear();
            Strategy_comboBox.Items.Add(PanelDefinition.STRATEGY_PANEL);
            Strategy_comboBox.Items.Add(PanelDefinition.STRATEGY_BOARD);
            Strategy_comboBox.Items.Add(PanelDefinition.STRATEGY_HYBRID);

            PanelDefinition Def = MainForm.CurrentPanel;
            if (Def != null)
            {
                // Editing the current panel
                XFirstOffset_textBox.Text = Def.XFirstOffset.ToString("0.00", CultureInfo.InvariantCulture);
                YFirstOffset_textBox.Text = Def.YFirstOffset.ToString("0.00", CultureInfo.InvariantCulture);
                XRepeats_textBox.Text = Def.XRepeats.ToString(CultureInfo.InvariantCulture);
                YRepeats_textBox.Text = Def.YRepeats.ToString(CultureInfo.InvariantCulture);
                XIncrement_textBox.Text = Def.XIncrement.ToString("0.00", CultureInfo.InvariantCulture);
                YIncrement_textBox.Text = Def.YIncrement.ToString("0.00", CultureInfo.InvariantCulture);
                UseBoardFids_checkBox.Checked = Def.UseBoardFids;
                Strategy_comboBox.SelectedItem = Strategy_comboBox.Items.Contains(Def.Strategy) ? Def.Strategy : PanelDefinition.STRATEGY_PANEL;
                Tolerance_textBox.Text = Def.HybridTolerance.ToString("0.00", CultureInfo.InvariantCulture);
                if (Def.PanelFids.Count > 0)
                {
                    PanelFiducials_dataGridView.Rows.Clear();
                    foreach (PanelDefinition.PanelFid f in Def.PanelFids)
                    {
                        PanelFiducials_dataGridView.Rows.Add(f.Name, f.X.ToString("0.000", CultureInfo.InvariantCulture), f.Y.ToString("0.000", CultureInfo.InvariantCulture));
                    }
                }
            }
            else
            {
                // New panel: last used values
                XFirstOffset_textBox.Text = MainForm.Setting.Panel_XFirstOffset.ToString("0.00", CultureInfo.InvariantCulture);
                YFirstOffset_textBox.Text = MainForm.Setting.Panel_YFirstOffset.ToString("0.00", CultureInfo.InvariantCulture);
                XRepeats_textBox.Text = MainForm.Setting.Panel_XRepeats.ToString(CultureInfo.InvariantCulture);
                YRepeats_textBox.Text = MainForm.Setting.Panel_YRepeats.ToString(CultureInfo.InvariantCulture);
                XIncrement_textBox.Text = MainForm.Setting.Panel_XIncrement.ToString("0.00", CultureInfo.InvariantCulture);
                YIncrement_textBox.Text = MainForm.Setting.Panel_YIncrement.ToString("0.00", CultureInfo.InvariantCulture);
                UseBoardFids_checkBox.Checked = MainForm.Setting.Panel_UseBoardFids;
                Strategy_comboBox.SelectedItem = Strategy_comboBox.Items.Contains(MainForm.Setting.Panel_Strategy) ? MainForm.Setting.Panel_Strategy : PanelDefinition.STRATEGY_HYBRID;
                Tolerance_textBox.Text = MainForm.Setting.Panel_HybridTolerance.ToString("0.00", CultureInfo.InvariantCulture);
            }
            Revert_button.Enabled = (Def != null);
            Loading = false;
            RebuildSkipGrid((Def != null) ? Def.Skipped : new HashSet<string>(StringComparer.Ordinal));
            UpdatePreview();
        }

        private void PanelFiducials_dataGridView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Delete || PanelFiducials_dataGridView.IsCurrentCellInEditMode)
            {
                return;
            }
            DataGridViewRow Row = PanelFiducials_dataGridView.CurrentRow;
            if (Row == null || Row.IsNewRow)
            {
                return;
            }
            PanelFiducials_dataGridView.Rows.Remove(Row);
            e.Handled = true;
        }

        // ==========================================================================================
        // Reading the fields

        private bool ReadGeometry(PanelDefinition Def, bool Complain)
        {
            double val;
            int intval;
            string Bad = null;
            if (double.TryParse(XFirstOffset_textBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val)) Def.XFirstOffset = val; else Bad = "Offset to lower left board, X";
            if (Bad == null) { if (double.TryParse(YFirstOffset_textBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val)) Def.YFirstOffset = val; else Bad = "Offset to lower left board, Y"; }
            if (Bad == null) { if (int.TryParse(XRepeats_textBox.Text, out intval) && (intval >= 1)) Def.XRepeats = intval; else Bad = "X repeats"; }
            if (Bad == null) { if (int.TryParse(YRepeats_textBox.Text, out intval) && (intval >= 1)) Def.YRepeats = intval; else Bad = "Y repeats"; }
            if (Bad == null) { if (double.TryParse(XIncrement_textBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val)) Def.XIncrement = val; else Bad = "X increment"; }
            if (Bad == null) { if (double.TryParse(YIncrement_textBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val)) Def.YIncrement = val; else Bad = "Y increment"; }
            if (Bad == null) { if (double.TryParse(Tolerance_textBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out val) && (val > 0)) Def.HybridTolerance = val; else Bad = "Hybrid tolerance"; }
            if (Bad != null)
            {
                if (Complain)
                {
                    MainForm.ShowMessageBox("Invalid value in " + Bad, "Invalid value", MessageBoxButtons.OK);
                }
                return false;
            }
            Def.UseBoardFids = UseBoardFids_checkBox.Checked;
            Def.Strategy = (Strategy_comboBox.SelectedItem == null) ? PanelDefinition.STRATEGY_PANEL : Strategy_comboBox.SelectedItem.ToString();
            return true;
        }

        private bool ReadPanelFids(PanelDefinition Def)
        {
            Def.PanelFids.Clear();
            double x, y;
            for (int i = 0; i < PanelFiducials_dataGridView.RowCount; i++)
            {
                DataGridViewRow Row = PanelFiducials_dataGridView.Rows[i];
                if (Row.IsNewRow) continue;
                if (Row.Cells["Designator_Column"].Value == null)
                {
                    Row.Cells["Designator_Column"].Value = "Fid" + i.ToString(CultureInfo.InvariantCulture);
                }
                if ((Row.Cells["Xpanelize_Column"].Value == null) || (Row.Cells["Ypanelize_Column"].Value == null) ||
                    !double.TryParse(Row.Cells["Xpanelize_Column"].Value.ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                    !double.TryParse(Row.Cells["Ypanelize_Column"].Value.ToString().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out y))
                {
                    MainForm.ShowMessageBox("Bad data in fiducials table, line " + (i + 1).ToString(CultureInfo.InvariantCulture), "Fiducials", MessageBoxButtons.OK);
                    return false;
                }
                Def.PanelFids.Add(new PanelDefinition.PanelFid() { Name = Row.Cells["Designator_Column"].Value.ToString(), X = x, Y = y });
            }
            return true;
        }

        private bool ValidateFiducials(PanelDefinition Def)
        {
            bool BoardFidsAvailable = !string.IsNullOrWhiteSpace(SingleBoardFiducials());
            if (Def.UseBoardFids)
            {
                if (!BoardFidsAvailable)
                {
                    MainForm.ShowMessageBox("Board fiducials not found or indicated. Mark the fiducials row (Step = Fiducials) on the single board first.",
                        "No fiducials", MessageBoxButtons.OK);
                    return false;
                }
            }
            else
            {
                if (Def.PanelFids.Count < 3)
                {
                    MainForm.ShowMessageBox("Need data for at least three (four preferred) panel fiducials", "Not enough fiducials", MessageBoxButtons.OK);
                    return false;
                }
                if (Def.PanelFids.Count == 3)
                {
                    if (MainForm.ShowMessageBox("Only three fiducials, results might be inaccurate. Continue?", "Three fiducials", MessageBoxButtons.OKCancel) == DialogResult.Cancel)
                    {
                        return false;
                    }
                }
            }
            if ((Def.Strategy != PanelDefinition.STRATEGY_PANEL) && !BoardFidsAvailable)
            {
                MainForm.ShowMessageBox("The " + Def.Strategy + " strategy measures each board on its own fiducials, but the single board has no fiducials row.\n"
                    + "Mark the fiducials on the single board first, or use the Panel strategy.", "No board fiducials", MessageBoxButtons.OK);
                return false;
            }
            return true;
        }

        private string SingleBoardFiducials()
        {
            return MainForm.SingleBoardFiducialsNow();
        }

        // ==========================================================================================
        // Skip grid: one checkbox per board, rows top-down = high Y first

        private void RebuildSkipGrid(HashSet<string> Skipped)
        {
            int xr, yr;
            if (!int.TryParse(XRepeats_textBox.Text, out xr) || !int.TryParse(YRepeats_textBox.Text, out yr) || (xr < 1) || (yr < 1) || (xr > 50) || (yr > 50))
            {
                return;
            }
            FormMain.BuildSkipGrid(Skip_dataGridView, xr, yr, Skipped);
        }

        private HashSet<string> ReadSkipGrid()
        {
            return FormMain.ReadSkipGrid(Skip_dataGridView);
        }

        private void Repeats_TextChanged(object sender, EventArgs e)
        {
            if (Loading) return;
            RebuildSkipGrid(ReadSkipGrid());
            UpdatePreview();
        }

        private void Geometry_TextChanged(object sender, EventArgs e)
        {
            if (Loading) return;
            UpdatePreview();
        }

        private void Skip_dataGridView_CellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (Loading) return;
            UpdatePreview();
        }

        private void Skip_dataGridView_CurrentCellDirtyStateChanged(object sender, EventArgs e)
        {
            if (Skip_dataGridView.IsCurrentCellDirty)
            {
                Skip_dataGridView.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void Strategy_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            bool hybrid = (Strategy_comboBox.SelectedItem != null) && (Strategy_comboBox.SelectedItem.ToString() == PanelDefinition.STRATEGY_HYBRID);
            Tolerance_textBox.Enabled = hybrid;
            Tolerance_label.Enabled = hybrid;
        }

        // ==========================================================================================
        // Preview

        private void UpdatePreview()
        {
            PanelDefinition Def = new PanelDefinition();
            if (!ReadGeometry(Def, false))
            {
                Preview_label.Text = "(check the values)";
                return;
            }
            Def.Skipped = ReadSkipGrid();
            List<string[]> Rows = (MainForm.CurrentPanel != null) ? MainForm.CurrentPanel.SingleBoardRows : MainForm.SingleBoardRowsFromGrid();
            int Boards, Components;
            RectangleF BBox;
            MainForm.PanelPreview(Def, Rows, out Boards, out Components, out BBox);
            Preview_label.Text = Boards.ToString(CultureInfo.InvariantCulture) + " board(s) of " + Def.Boards.ToString(CultureInfo.InvariantCulture)
                + ", " + Components.ToString(CultureInfo.InvariantCulture) + " components" + Environment.NewLine
                + ((Components > 0) ? "Components span X " + BBox.Left.ToString("0.0", CultureInfo.InvariantCulture) + " .. " + BBox.Right.ToString("0.0", CultureInfo.InvariantCulture)
                    + ", Y " + BBox.Top.ToString("0.0", CultureInfo.InvariantCulture) + " .. " + BBox.Bottom.ToString("0.0", CultureInfo.InvariantCulture) + " (board coordinates)" : "");
        }

        // ==========================================================================================

        private void Cancel_button_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void Revert_button_Click(object sender, EventArgs e)
        {
            if (MainForm.CurrentPanel == null)
            {
                MainForm.ShowMessageBox("The CAD data is not panelized.", "Single board", MessageBoxButtons.OK);
                return;
            }
            if (MainForm.ShowMessageBox("Replace the panel with the single board? Placed flags of the panel are lost.", "Revert to single board", MessageBoxButtons.OKCancel) != DialogResult.OK)
            {
                return;
            }
            if (MainForm.RevertPanel_m())
            {
                OK = true;
                this.Close();
            }
        }

        private void OK_button_Click(object sender, EventArgs e)
        {
            PanelDefinition Def = new PanelDefinition();
            if (!ReadGeometry(Def, true)) return;
            if (!ReadPanelFids(Def)) return;
            Def.Skipped = ReadSkipGrid();
            if (!ValidateFiducials(Def)) return;

            if (!MainForm.ApplyPanel_m(Def))
            {
                return;
            }
            OK = true;
            string path = MainForm.GetPath();
            MainForm.SaveDataGrid(path + PANELFIDUCIALS_DATAFILE, PanelFiducials_dataGridView);
            MainForm.Setting.Panel_XFirstOffset = Def.XFirstOffset;
            MainForm.Setting.Panel_YFirstOffset = Def.YFirstOffset;
            MainForm.Setting.Panel_XRepeats = Def.XRepeats;
            MainForm.Setting.Panel_YRepeats = Def.YRepeats;
            MainForm.Setting.Panel_XIncrement = Def.XIncrement;
            MainForm.Setting.Panel_YIncrement = Def.YIncrement;
            MainForm.Setting.Panel_UseBoardFids = Def.UseBoardFids;
            MainForm.Setting.Panel_Strategy = Def.Strategy;
            MainForm.Setting.Panel_HybridTolerance = Def.HybridTolerance;
            this.Close();
        }
    }
}
