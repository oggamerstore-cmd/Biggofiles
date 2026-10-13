# Publishing Biggofiles

The `github` export is ready to become the repository root. Upload its **contents**, including `.github`, `.gitattributes`, and `.gitignore`, while preserving the subfolders. Do not upload the surrounding development workspace or the enclosing `github` folder as an extra level.

The export includes:

- C# and WPF source, build script, icon, and independent tests.
- MIT license, README, privacy information, changelog, and contributor notes.
- Dark/light interface screenshots made with generated demonstration files.
- `downloads/Biggofiles-v1.0-win-x64.zip` and its SHA-256 checksum for users who do not want to compile the app.
- A Windows GitHub Actions workflow that builds, tests, and saves artifacts. It does not create a public release or require a personal access token.

After uploading, check the first Actions run. Local Windows tests have passed; the hosted workflow has not been run from this workspace. For a GitHub Release, use tag `v1.0` and attach the ZIP from `downloads`. The README's relative download link works without a predetermined account or repository URL.

To make another clean export, run `build.ps1 -Test -GitHub` from the development source. The command refuses to overwrite an existing `github` directory. Preserve or move that directory aside first. Generated `dist`, `release`, and test workspaces are not source files to commit.
