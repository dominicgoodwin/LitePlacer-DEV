using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace LitePlacer
{
    static class Program
    {
        /// <summary>
        /// The main entry point for the application.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // Single instance: a second LitePlacer can't read the data files the first one holds, starts
            // with empty grids and saves them on exit, wiping the data (issue #23). Refuse to start.
            bool createdNew;
            using (System.Threading.Mutex SingleInstance = new System.Threading.Mutex(true, @"Local\LitePlacer-DEV-running", out createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show("LitePlacer is already running. Use the running instance; a second one would overwrite its data files.",
                        "LitePlacer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                Application.Run(new FormMain());
            }
        }
    }
}
