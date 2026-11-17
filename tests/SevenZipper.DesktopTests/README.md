# Windows Explorer drag test

Run on an unlocked Windows desktop as the signed-in user. Build the application and native DLLs first (`scripts/build.ps1`). Leave the mouse and keyboard untouched during the run: the test temporarily switches the primary button, moves the pointer, and opens its own Explorer windows. It restores the original button setting and cursor when it finishes or when a case times out.

```powershell
New-Item -ItemType Directory -Force artifacts | Out-Null
dotnet run --project tests/SevenZipper.DesktopTests -c Release -- --interactive artifacts
```

Specify `normal-copy`, `swapped-copy`, or `swapped-cancel` after `artifacts` to run one case. The output shows the physical button state, the corresponding WPF event, the copied file hashes, and the temporary folder cleanup. Fixtures and copied files are left in the unique directory printed at the start.
