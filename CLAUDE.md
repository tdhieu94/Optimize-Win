# Optimize Win — notes for contributors

Personal Windows 11 optimizer for one non-technical user (Hieu). UI text is Vietnamese. Safety beats features.

## Hard rules
- Every write to the user's machine goes through `OW.Core.Execution.SafeExecutor`: PathGuard check → journal Pending → act → journal outcome → undoable. Modules only build `ActionPlan`s.
- PathGuard protects system roots (Windows, Program Files, ProgramData\Microsoft...). The only exceptions are the rule-only junk folders in `PathGuard.CreateForCurrentMachine` (Windows\Temp, SoftwareDistribution\Download, WER report folders): files strictly inside them, cleanup rules only, never user picks. Do not add to that list without tests.
- Never permanently delete user files directly; quarantine them (7-day retention) so undo works. A file is always quarantined on its own drive (`X:\OptimizeWin.Quarantine` on drives other than C:), never copied across drives.
- The admin service exposes only typed methods in `OW.Contracts.IOwService`. Never add a method that runs arbitrary commands or takes arbitrary paths without going through SafeExecutor.
- The service only talks to `OptimizeWin.exe` installed one folder above it (ClientVerifier). Do not weaken this.
- No telemetry, nothing about the machine leaves it. The only network call is the update check/download from this repo's GitHub releases (`UpdateRules`), HTTPS only, SHA-256 verified before install.
- No registry cleaner, no partition resizing, no defrag on SSDs.
- Windows' own files (WinSxS, Windows.old, restore points) are never deleted by OW. The only exception is Windows' own component cleanup: `dism.exe` with the fixed arguments in `ComponentStore` (never `/ResetBase`), run through SafeExecutor only when the user pressed the button and confirmed (`ActionOrigin.UserSelected`). It is journaled but cannot be undone, and the UI says so.
- Any new rule or write operation needs tests in `tests/OW.Core.Tests` and must be tried in Windows Sandbox before Hieu runs it.

## Build
- `dotnet test tests/OW.Core.Tests` works on Linux and Windows.
- `src/OW.App` (WinUI 3) and the service publish only on Windows; CI (`.github/workflows/build.yml`) builds and uploads the installable zip.
