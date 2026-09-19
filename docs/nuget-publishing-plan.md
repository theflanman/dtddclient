# NuGet publishing plan

Status: package metadata, `IsAotCompatible`/SourceLink config, and the
tag-triggered `publish.yml` workflow are in place (see "Order of
operations" below for what's done vs. outstanding). Still need the
package ID reserved on nuget.org and the `NUGET_API_KEY` secret before a
tag can actually publish.

## Package shape

- **Package ID:** `DtDDNetClient`. Deliberately not `DoesTheDogDie` —
  we don't own that name, and a survey of other unofficial media-API
  clients on NuGet (`TvDbSharper`/`TVDBSharp`/`TvdbClient` for TheTVDB,
  `TMDbLib` for TMDb, `Anilist4Net`/`AniListNet` for AniList,
  `OmdbApiNet` for IMDb-via-OMDb) shows the norm is to abbreviate the
  service name rather than spell out the trademarked brand, often with a
  `Client`/`Sharp`/`Net` suffix. `DtDDNetClient` follows that pattern
  using the abbreviation this repo already uses throughout (`DtDD`).
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

## `.csproj` metadata — done

Added to `src/DoesTheDogDie/DoesTheDogDie.csproj`: `PackageId`
(`DtDDNetClient`), description, `PackageLicenseExpression` (`MIT`),
project/repo URLs, `PackageReadmeFile` + embedded `README.md`/`LICENSE`,
`IsAotCompatible` (builds with 0 warnings today), symbol packages
(`.snupkg`), and `Microsoft.SourceLink.GitHub` (pinned to `10.0.401` to
match the installed SDK — the `8.*` wildcard originally drafted here
pulled in a `Microsoft.Data.Sqlite`-transitive package with a known
vulnerability that failed under `TreatWarningsAsErrors`/NuGet audit).
Verified locally: `dotnet pack` produces `DtDDNetClient.<version>.nupkg`
with both TFM assemblies, README, and LICENSE embedded.

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

## CI: publish workflow — done

`.github/workflows/publish.yml` exists, triggered only on `v*.*.*` tags.
It restores, re-runs the full test suite, packs `src/DoesTheDogDie` with
`-p:Version` from the tag, pushes to nuget.org, and attaches the
`.nupkg`/`.snupkg` to a GitHub release via `softprops/action-gh-release`.

It runs under the `nuget-publish` GitHub Environment, which is also
already created (via the API, not the web UI) with `theflanman` as a
required reviewer — so pushing a `v*` tag stages the run but it won't
execute until manually approved. `can_admins_bypass` is `true` by
GitHub's default for environments, which is fine for a single-maintainer
repo.

## Order of operations for the first release

1. ✅ `.csproj` metadata (above).
2. ✅ `publish.yml` workflow.
3. ✅ `nuget-publish` GitHub Environment with required-reviewer gate.
4. ⬜ **Reserve the package ID** with a manual `dotnet nuget push` from a
   local machine, using a personal nuget.org API key — this has to be a
   human/local action; a scoped CI key can't push a package ID that
   doesn't exist yet, and this repo's automation shouldn't hold an
   unscoped nuget.org key.
5. ⬜ **Add the `NUGET_API_KEY` secret**, scoped to just `DtDDNetClient`
   once reserved:
   `gh secret set NUGET_API_KEY --repo theflanman/dtddclient --env nuget-publish`
6. ⬜ Tag `v0.1.0`, push the tag, approve the environment gate, confirm
   the package shows up on nuget.org.

## Open questions to settle before v0.1.0

- Do we want a single package, or split `Cache`/`Statistics` into optional
  packages later so a consumer who only wants the thin API client doesn't
  pull in `Microsoft.Data.Sqlite`? Not worth the complexity at 0.x; revisit
  if the dependency list grows.
