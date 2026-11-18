# Markdig 0.37.0

Pinned managed Markdown dependency. The source tree contains references and this
license notice; package binaries are restored locally before compiling and the
`packages/Markdig.0.37.0/` directory is intentionally ignored by Git.

From the repository root, with NuGet CLI 4.4 or newer:

```powershell
nuget install Markdig -Version 0.37.0 -OutputDirectory packages -DependencyVersion Ignore -NonInteractive -Source https://api.nuget.org/v3/index.json
```

If the NuGet CLI is unavailable, download the official
`https://api.nuget.org/v3-flatcontainer/markdig/0.37.0/markdig.0.37.0.nupkg`
and extract its contents into `packages/Markdig.0.37.0/`. Expected SHA-256:
`9cfa31d3a2eb694d3164f1a3fab41a040c698a22e784cd4d76e50489d5698335`.

Keep the official package contents under `packages/Markdig.0.37.0`, including
package metadata. Do not add `-ExcludeVersion`; the project references are exact:

- `lib/net462/Markdig.dll`: production .NET Framework 4.8 Core and deployment.
- `lib/net8.0/Markdig.dll`: Core.Net8, Harness and MockDemo.

The net462 asset requires System.Memory >= 4.5.5. RNAssistant already pins
System.Memory 4.6.3 and its dependency closure; the command above avoids changing
those pins. The net8.0 asset has no additional package dependencies. No runtime
download or network service is involved. Offline builds can extract the verified
official package to the same directory before invoking MSBuild.

Package: https://www.nuget.org/packages/Markdig/0.37.0

License: BSD-2-Clause; `Markdig-LICENSE.txt` reproduces the upstream notice from
https://github.com/xoofx/markdig/blob/0.37.0/license.txt . Production output includes
it under `licenses/Markdig-LICENSE.txt`. Any package update must also update the
analyzer profile, exact references and focused `markdown analysis:` checks.
