# NuGet publishing plan

Status: not yet implemented. This is the plan for when we're ready to ship a
package, most likely alongside or after Phase 6 (release packaging) in the
roadmap.

## Package shape

- **Package ID:** `DoesTheDogDie` (matches the assembly/namespace). Worth a
  quick nuget.org search before locking this in.
- **What ships:** only `src/DoesTheDogDie` (the `Api`, root, `Cache`, and
  `Statistics` namespaces already live in one assembly, so it's a single
  package). `Microsoft.Data.Sqlite` stays a normal `PackageReference` — it's
  only pulled in transitively for consumers who use `SqliteDtddCache`.
- **TFMs:** `net9.0;net10.0`, same as `Directory.Build.props`. No
  `netstandard2.0` — this library targets current .NET only, which is fine
  for its one real consumer (the Jellyfin plugin) but worth stating
  explicitly since it limits who else can take a dependency on it.
- **Versioning:** SemVer 2.0, starting in `0.x` (pre-1.0) until the public
  surface (Phases 0-4 today, plus 5-6) has shipped and had a chance to
  settle. Tag releases `v0.1.0`, `v0.2.0`, etc.; cut `1.0.0` once Phase 6
  lands and the API is one we're willing to commit to.
- **Release notes:** generate from the tag's annotated message or a
  `CHANGELOG.md` entry — decide which once we're closer to cutting a
  release; not urgent now.

## Required `.csproj` metadata

Add to `src/DoesTheDogDie/DoesTheDogDie.csproj` (or hoist the
package-agnostic bits into `Directory.Build.props` so they don't drift):

```xml
<PropertyGroup>
  <PackageId>DoesTheDogDie</PackageId>
  <Description>Community-led .NET client for the Does the Dog Die API (doesthedogdie.com), built around its free-tier rate limits.</Description>
  <Authors>Connor Flanigan</Authors>
  <PackageLicenseExpression>MIT</PackageLicenseExpression>
  <PackageProjectUrl>https://github.com/theflanman/dtddclient</PackageProjectUrl>
  <RepositoryUrl>https://github.com/theflanman/dtddclient</RepositoryUrl>
  <PackageReadmeFile>README.md</PackageReadmeFile>
  <PackageTags>doesthedogdie;dtdd;content-warnings;trigger-warnings</PackageTags>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
  <EmbedUntrackedSources>true</EmbedUntrackedSources>
  <IncludeSymbols>true</IncludeSymbols>
  <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  <ContinuousIntegrationBuild Condition="'$(GITHUB_ACTIONS)' == 'true'">true</ContinuousIntegrationBuild>
</PropertyGroup>

<ItemGroup>
  <None Include="../../README.md" Pack="true" PackagePath="\" />
</ItemGroup>
```

Also add SourceLink so consumers can step into the published source:

```xml
<PackageReference Include="Microsoft.SourceLink.GitHub" Version="8.*" PrivateAssets="All" />
```

`PackageLicenseExpression` needs `LICENSE` to actually be MIT (it already
is) and matches the SPDX identifier exactly.

## Versioning mechanics

Two reasonable options; pick one rather than hand-editing `<Version>`:

1. **Tag-driven via `Directory.Build.props` + CI**: CI reads the pushed git
   tag (`v1.2.3`) and passes `-p:Version=1.2.3` to `dotnet pack`. Simple, no
   new dependency, but nothing enforces version bumps between commits.
2. **Nerdbank.GitVersioning**: derives version from git height + a
   `version.json`. More machinery, but gives every CI build (not just
   tagged ones) a meaningful, monotonic version — useful if we ever want
   CI to publish preview packages on every merge to `main`.

Recommendation: start with (1) — tag-driven, manual — since release
cadence will be low early on. Revisit (2) if preview/nightly packages
become useful for the Jellyfin plugin to consume ahead of a tagged release.

## CI: publish workflow

New workflow, separate from `.github/workflows/build.yml`, triggered only
on version tags so a stray push can't publish:

```yaml
name: publish
on:
  push:
    tags: ["v*.*.*"]

jobs:
  publish:
    runs-on: ubuntu-latest
    environment: nuget-publish   # manual approval gate, see below
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: |
            9.0.x
            10.0.x
      - run: dotnet restore
      - run: dotnet test --configuration Release
      - run: |
          VERSION=${GITHUB_REF_NAME#v}
          dotnet pack src/DoesTheDogDie -c Release -p:Version=$VERSION -o ./artifacts
      - run: dotnet nuget push ./artifacts/*.nupkg --api-key ${{ secrets.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate
      - uses: softprops/action-gh-release@v2
        with:
          files: ./artifacts/*.nupkg
```

Things this depends on, to set up before first use:

- **`NUGET_API_KEY` secret**: generate a nuget.org API key scoped to just
  this package ID (nuget.org lets you scope a key to specific packages once
  the package ID has been reserved by an initial manual push, or you can
  scope it to a glob before that — check current nuget.org UI).
- **`nuget-publish` GitHub Environment**: configure it with a required
  reviewer, so pushing a `v*` tag stages a publish but a human has to
  approve the job before it actually runs. Cheap insurance against a
  mistaken or malicious tag push.
- **Re-running `dotnet test` in the publish job**: redundant with the
  `build` workflow that already ran on the commit, but cheap, and it's the
  last checkpoint before something goes out the door.

## Order of operations for the first release

1. Add the `.csproj` metadata above.
2. Reserve the package ID with a manual `dotnet nuget push` from a local
   machine (first push to a new ID can't come from an environment-gated CI
   job with a pre-scoped key, since the key can't be scoped to a package
   that doesn't exist yet).
3. Add the `NUGET_API_KEY` secret and `nuget-publish` environment.
4. Add the `publish.yml` workflow.
5. Tag `v0.1.0`, push the tag, approve the environment gate, confirm the
   package shows up on nuget.org.

## Open questions to settle before v0.1.0

- Is `DoesTheDogDie` the package ID we want, or does it read oddly divorced
  from the site's branding in a NuGet listing? Worth a second opinion.
- Do we want a single package, or split `Cache`/`Statistics` into optional
  packages later so a consumer who only wants the thin API client doesn't
  pull in `Microsoft.Data.Sqlite`? Not worth the complexity at 0.x; revisit
  if the dependency list grows.
