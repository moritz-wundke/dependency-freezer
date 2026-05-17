# dependency-freezer

`dependency-freezer` is a greenfield Unity package and companion CLI for freezing registry-hosted UPM dependencies into embedded packages backed by auditable tarball provenance.

## What is implemented

- Freeze registry-hosted dependencies into `Packages/FrozenPackages/<package-name>`.
- Track frozen state in a project-level `frozen-lock.json`.
- Resolve and freeze transitive dependencies as part of the selected subtree.
- Preview subtree unfreeze operations and block unsafe unfreezes when a dependency is shared by another frozen root.
- Unfreeze selected subtrees or all frozen packages.
- Validate drift between `Packages/manifest.json`, embedded package contents, and `frozen-lock.json`.
- Provide a Unity Editor window for interactive workflows.
- Provide a CLI for CI and batch-mode automation.

## Current assumptions and limits

- The current implementation supports registry-hosted UPM packages only.
- Git, local path, and other non-registry package sources are detected and rejected during freeze.
- Frozen packages are stored as extracted embedded packages; tarball provenance and integrity are stored in `frozen-lock.json`.
- The editor window is designed as a thin UI over the core engine and CLI-oriented runtime logic.

## Repository layout

- `/Runtime` - core freeze/unfreeze/validation engine
- `/Editor` - Unity Editor window and editor assembly definition
- `/Tools/DependencyFreezer.Cli` - CLI entry point for CI and batch workflows
- `/Tests/DependencyFreezer.Tests` - automated tests for freeze/unfreeze/validation behavior

## CLI

```bash
dotnet run --project /home/runner/work/dependency-freezer/dependency-freezer/Tools/DependencyFreezer.Cli -- freeze --project /path/to/unity-project
dotnet run --project /home/runner/work/dependency-freezer/dependency-freezer/Tools/DependencyFreezer.Cli -- unfreeze-preview --project /path/to/unity-project --package com.example.package
dotnet run --project /home/runner/work/dependency-freezer/dependency-freezer/Tools/DependencyFreezer.Cli -- unfreeze --project /path/to/unity-project --all
dotnet run --project /home/runner/work/dependency-freezer/dependency-freezer/Tools/DependencyFreezer.Cli -- validate --project /path/to/unity-project
```

## Validation

Build and test the implementation with:

```bash
dotnet build /home/runner/work/dependency-freezer/dependency-freezer/DependencyFreezer.slnx
dotnet test /home/runner/work/dependency-freezer/dependency-freezer/DependencyFreezer.slnx --no-build
```
