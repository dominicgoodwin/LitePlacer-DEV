# LitePlacer-DEV (Dominic Goodwin's fork)

Fork of jkuusama/LitePlacer-DEV: WinForms control software for the LitePlacer pick-and-place
machine. Branch `Main`. Upstream has no OSS licence (EULA only); keep machine settings, jobs
and customer CAD data out of the repo.

## Working model
- One work item per branch, PR against `Main`, merged with a merge commit (keep individual
  commits for bisecting). Open PRs with `gh api repos/dominicgoodwin/LitePlacer-DEV/pulls
  --input body.json` — `gh pr create` fails here (GraphQL is blocked).
- **This code cannot be compiled in the cloud session** (.NET Framework 4.8 WinForms needs
  Visual Studio on Windows). Dominic builds and tests on the machine. Before pushing, run a
  syntax-only check with `tree-sitter-c-sharp` (pip) over every changed .cs file.
- Every PR body carries a concrete test plan for the machine.
- Other people's forks are not reachable from the session (no anonymous GitHub clone). To
  cherry-pick from one, Dominic pastes the diff.
- Work items live as GitHub issues on this repo; the overall plan is in the Claude project doc
  `liteplacer-fork-plan.md`.

## Code facts worth knowing
- `MainForm.cs` is ~14k lines; almost all logic is in event handlers. `Setting` (MySettings,
  `AppSettings.cs`) is JSON in `LitePlacer.Appsettings` next to the exe
  (`Application.StartupPath`); a Debug build reads/writes `bin\Debug\`.
- `ShowMessageBox` returns `DialogResult.Cancel` **without showing anything** while
  `MainForm.StartingUp` is true. To show a dialog during startup, save/clear/restore
  `StartingUp` around it (see nozzle-count check and `MySettings.Load`).
- Grids persist via `SaveDataGrid`/`LoadDataGrid` (binary, v2 format carries column names and
  maps by name), so **adding a grid column needs no migration**; old files leave it null.
  Job files (`.lpj`) are CSV written/read by hand in `SaveJobData`/`ParseJobData`.
- Tapes grid: Next X/Y are recalculated in `Tapes_dataGridView_CellEndEdit` only when the
  "Next" value changed; `Tapes.UpdateNextCoordinates` recomputes from first hole + pitch and
  must not run after "Set part no." (which stores a measured position). Tape buttons call
  `TapesGrid_CommitAndGetRow()` first.
- The Tapes tab is the **component** table. Columns: ID, `Source_Column` (Tape / Loose /
  Fiducial), Down cam alg. (`Type_Column`: holes for Tape, the part for Loose, fiducials for
  Fiducial), `FindFast_Column`, Nozzle, `UpCamAlg_Column` ("--" or algorithm), `UseUpCam_Column`,
  `ManualAssist_Column`, then tape geometry. Algorithm combos are rebuilt per row
  (`FillAlgorithmSelectionBox` for Type, `RefreshUpCamAlgCombos` for UpCamAlg) after load and
  after algorithm add/remove/rename. `EnsureLooseSourceRow()` migrates the old global pickup
  location into a Loose row named `LOOSE_SOURCE_ID`; `EnsureFiducialSourceRow()` creates a
  Fiducial component.
- Job grid columns: Value, Footprint, Count, **Component** (`JobdataTapeColumn`; blank = process
  step row), **Step** (`JobdataMethodColumn`; only meaningful on blank-component rows: "?",
  Pause, Change Nozzle, Recalibrate; derived and greyed on component rows), Skip, Find fast,
  Nozzle, Use up cam, Up cam alg. (`PlacementParameterColumn`), Manual assist, Components.
  Inherited cells hold `FormMain.FROM_TAPE` ("(comp)") for text or null for checkboxes and
  draw grey; left-click a checkbox overrides, right-click resets. The run loop reads rows ONLY
  through `JobRowMethod(row)` (vocabulary: Place / Place Fast / Place Assisted / Fiducials /
  ? / Pause / Change Nozzle / Recalibrate), `JobRowSkipped`, `JobRowNozzle`, `JobRowUseUpCam`,
  `JobRowUpCamAlgorithm`, `JobRowManualAssist`, `JobRowFindFast`, `JobRowIsFiducials`.
  `SetJobRowComponent` / `SetJobRowStep` are the only writers of a row's kind.
- `.lpj` format "2026": 12 quoted CSV fields per row in the column order above; inherited
  cells saved as "" (checkbox) or "(comp)" (text). Older formats are refused.
- Panelize (`PanelizeForm`) replaces CAD data; the single-board rows and fiducial list are kept
  on `MainForm.SingleBoardCadRows` / `SingleBoardFiducials` so it can be re-run. Job row
  settings survive the job-grid rebuild via a value+footprint keyed snapshot.
- TinyG accepts `{"zzb",1}` (comma) as well as `{"zzb":1}`; confirmed on the machine.
- Vision: AForge.NET 2.2.5 binaries are committed; `HomographyEstimation.dll` is closed source.

## Style
- Match the existing code: PascalCase locals are common, `_m` suffix on methods that can show
  a message box and return bool, `DisplayText(..., KnownColor.DarkRed, true)` for errors the
  operator must see in the log.
- Commit messages: short imperative summary line, bullet-point body.
