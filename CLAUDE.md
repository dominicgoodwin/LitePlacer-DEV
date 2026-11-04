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
- Tapes grid `Source_Column`: "Tape" or "Loose". A Loose row is a one-part-at-a-time source
  at First X/Y found by its down cam algorithm; `PlacePart_m` branches on `TapeIsLoose()`.
  The job methods LoosePart / LoosePart Assisted / *Snapshot are legacy (buttons hidden);
  old `.lpj` rows with them are mapped to the Loose row named `LOOSE_SOURCE_ID`, which
  `EnsureLooseSourceRow()` creates from `Setting.General_PickupCenterX/Y` on tapes load.
- Tape "recipe": Method (Place Fast / Place), Type (= down cam algorithm; header "Down cam
  alg."), UpCamAlg ("--" or algorithm), UseUpCam, ManualAssist, Nozzle. Algorithm combos on
  the tapes grid are rebuilt per row (`FillAlgorithmSelectionBox` for Type,
  `RefreshUpCamAlgCombos` for UpCamAlg) after load and after algorithm add/remove/rename.
- Job grid columns: Value, Footprint, Count, **Tape** (`JobdataTapeColumn`, tape ID for
  Place-family rows), Method, Parameter (`JobdataMethodParametersColumn`: algorithm for
  Fiducials/LoosePart, stop distance for LoosePart Assisted), Nozzle, Placement method,
  Placement parameter, Components. `.lpj` files still have ONE parameter field: the saver
  writes Tape for tape rows and Parameter otherwise (`JobRowParameterField`), the loaders
  route it back by method (`SetJobRowParameterField`), so the file format is unchanged.
- Job grid: Method / Nozzle / Placement method / Placement parameter cells may hold
  `FormMain.FROM_TAPE` ("(tape)"), meaning derive from the tape in Method parameters. Read
  them through `ResolveJobCell(row, column)` (and `JobRowManualAssist(row)`), never
  directly. `CellFormatting` shows the resolved value greyed. Manual assist is independent
  of Place vs Place Fast: both put-down sites in `PlacePart_m` check it.
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
