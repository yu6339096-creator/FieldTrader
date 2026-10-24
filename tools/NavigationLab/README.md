# Windows local navigation lab

This remains a separate WPF launcher; its shared implementation now lives in `src/FieldTrader/Market/Windows/NavigationLab` and is also used by the existing market window debug tab. It references the existing FieldTrader project and uses its `NavigationExecutor`, planners, resolver, verifier, shared input gate and `WindowsInputController`. It does not start audio devices, access the game, perform trades, or change ledger semantics. `WindowsNavigationMouse` remains disabled.

## Run

On a Windows interactive desktop, with .NET 8 SDK and Desktop runtime:

```powershell
dotnet run --project tools/NavigationLab -- "C:/path/to/evidence/new-run"
# Optional one repetition per scenario during development:
dotnet run --project tools/NavigationLab -- "C:/path/to/evidence/new-smoke-run" --smoke
# Targeted 20-run verification, including stronger native-guard assertions:
dotnet run --project tools/NavigationLab -- "C:/path/to/evidence/new-minimize-run" --scenario minimize
# Manual UI-stop probe: A clicks, then waits; click EMERGENCY STOP while Running:
dotnet run --project tools/NavigationLab -- "C:/path/to/evidence/new-stop-probe" --ui-stop-probe
# Offline pure target-fusion benchmark (20 warm resolves per saved fixture observation):
dotnet run --project tools/NavigationLab -- --benchmark-evidence "C:/path/to/evidence/completed-run" "C:/path/to/fusion-timings.json"
```

Click **Run real GUI suite ×20** to expressly start native mouse tests. Use a **new empty evidence directory** for every run. Keep the window visible and do not move the mouse or cover it while the suite runs. Use Pause, Cancel, EMERGENCY STOP or Escape to stop the suite. There is no automatic resume after a stop; a new Run captures and plans again. A synthetic test interruption stops only its own case, so the independently authorized next case can start with fresh evidence. The UI stop controls prevent subsequent cases too.

Restricted sandboxes may display/capture the window while rejecting `SetCursorPos`. This is an environment failure, not a click pass. Run normally in the permitted interactive desktop; do not disable security software or change security settings. The application never automatically restores focus after a task is rejected. Only the explicit next independent test case sets up its local window again.

## What is real, and what is a fixture

- The WPF controls, window handle, visible desktop screenshot, screen coordinates, native `SetCursorPos`/`SendInput`, WPF click/wheel events, state changes, and screenshots after input are real.
- `MarkerVision` is an intentionally deterministic **screenshot pixel fixture detector**. It reads PNG pixels only, not the WPF visual tree, UI Automation, or a precomputed desired click result. Known colored regions identify fixture controls and visible counter/page evidence. Its confidence 1.0 is a fixture convention, not a model probability.
- This lab does **not** claim that the game-trained YOLO detects arbitrary WPF controls, or that marker labels are OCR. Local-case YOLO/OCR timing is explicitly null. Real market YOLO/OCR evidence and overlays are separate from this input acceptance test.
- Screenshot geometry is recorded in physical client pixels under PerMonitorV2. WPF DIP is never passed to the native adapter. No extra DPI multiplier is applied.

## Coverage and evidence

There are 23 scenarios: single/two/six targets, configured interval, dynamic relocation, unexpected stable-layout change, page change, in-client dialog, scroll-to-target, scroll boundary, cancellation, fresh resume after pause, emergency latch, injected adapter exception, fresh/stale window movement and resizing, foreground loss, minimization, closed HWND, exclusive competing tasks, and cancellation while queued.

Each scenario defaults to 20 repetitions. Negative cases pass only when they stop safely with the expected physical event count. The closed-window case verifies that execution actually reached the native adapter guard. At the scroll boundary the verifier must report no new visual change; an unrelated input failure does not qualify as a pass. WPF can bring a list item into view when it gets focus, so list-item clicks are dynamic boundaries and never share stale coordinates.

`environment.json` records actual DPI/display configuration. Other DPI settings, negative coordinates and multiple monitors are **not covered** unless physically present and actually run. The focus-loss fixture uses another same-process decoy window; switching to an external application is not silently claimed. The dialog is a modal-looking in-client overlay using the same expressly authorized HWND, not a separate native modal HWND.

Per case:

- `screens/*.png`: new visible desktop client captures, including post-action evidence.
- `navigation.jsonl`: original executor states and frame IDs.
- `*-vision.json`: fixture boxes, labels, confidence, physical screen provenance.
- `*-overlay.png`: cyan fixture regions, green fixture labels, gold selected target, red planned point, frame ID/state. The original WPF status region shows the last actual received click/counter; `result.json` links the complete final event sequence.
- `native-calls.json`: actual native-call duration, requested physical point, subsequent received-event count.
- `result.json` and root `cases.jsonl`: verdict, physical click count, wrong/background clicks, wheel events, button-held observation and timings.
- `summary.json`: success rate, mean and nearest-rank P95, first cold sample and per-scenario warm mean.

The capture stage includes a deliberate 45 ms render-settle wait, encoding and disk persistence. The input wrapper includes a deliberate 65 ms event-drain wait; **native API time** is recorded separately. PageVerification includes its independent screenshot and fixture recognition; AfterVerification is only the pure verifier. TotalMs is the complete scenario including setup/settling/assertions. For task-only duration, group navigation events by TaskId and subtract the first IDLE timestamp from its terminal state timestamp. P95 is descriptive for this small sample, not a production SLA.

Partial OS `SendInput` delivery is not forcibly reproduced: input-exception is an explicitly labeled adapter fault before the second native action. `GetAsyncKeyState(VK_LBUTTON)` is checked after every case. No game input, audio hardware performance, or independent model accuracy claim is made.

The minimized-window scenario waits for OS restore animation to settle **before** capturing its source evidence, and asserts that the adapter was actually called once and rejected the minimized HWND. An earlier pre-input layout rejection is not accepted as coverage of that native guard. The manual UI-stop probe requires a UI control event and a cancellation/emergency reason; simply pausing for stale evidence is not a pass.
