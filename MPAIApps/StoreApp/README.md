# StoreApp

The Implementer's front door to the MPAI Store: a window where an L3 is submitted to
the Store Service and the Store's verdict is read - what it found, and whether it
published or refused the L3. StoreApp holds no Store logic of its own.

- **Store address:** the environment variable `MPAI_STORE`, default `https://localhost:5020/`.
- **Build:** run `StoreApp.bat`, which builds `StoreApp.exe` in this folder; or
  `dotnet run --project MPAIApps\StoreApp\StoreApp.csproj`.
- **Use:** *Submit an L3...*, pick the file, read the log. The list on the left shows
  what the Store holds, with each L3's latest version and the number of findings.

See `MPAIApps\StoreService\README.md` for what the Store checks.
