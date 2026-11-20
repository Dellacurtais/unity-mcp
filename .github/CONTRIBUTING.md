# Contributing to Unity MCP Transport

Thanks for helping. Issues and pull requests are welcome in English or Portuguese.

## Before you start

- **Bugs:** open an issue with the Unity version, render pipeline, transport mode (Embedded or Broker), the tool call and its full answer, and the relevant lines from the Unity Console or `%LOCALAPPDATA%\UnityMcp\Broker\logs`. Remove tokens and private paths first.
- **Features:** open an issue first for anything larger than a small fix, so the design can be agreed before you write code. New tools need a clear safety story: capability, risk, idempotence, and what happens after a Domain Reload.
- **Security problems** never go in public issues; see [SECURITY.md](SECURITY.md).

## Development setup

1. Clone the repository into the `Packages/` folder of a throwaway Unity project (6000.6 recommended; 6000.1–6000.5 also supported), so it loads as an embedded package. Do not develop against a production game project: the tests create and delete scenes and assets.
2. The broker and the mesh sidecar are plain Python projects inside the package:
   ```bash
   python -m venv .venv
   .venv/Scripts/python -m pip install -r Broker~/requirements-dev.txt
   .venv/Scripts/python -m pytest Broker~/tests -q
   ```
3. The checks CI runs without Unity:
   ```bash
   python Tools~/validate_release.py
   dotnet run --project Tools~/CSharpSyntaxCheck/CSharpSyntaxCheck.csproj --configuration Release -- .
   ```
4. Unity tests: **Window → General → Test Runner → EditMode**. Run the mutation-gate tests from the Test Runner window, not through `run_tests` over MCP (there the test job owns the gate and those tests report *Ignored*).

## Code guidelines

- C# targets **C# 9** (Unity's compiler for these versions): no file-scoped namespaces, raw strings or `required` members.
- Editor APIs, `EditorPrefs` included, are main-thread only. Background work captures what it needs on the main thread first, or goes through `MainThreadDispatcher`.
- Every mutation is Undo-aware, dirties the right scene or asset, and never saves implicitly.
- Tool descriptors must be honest: `IsWrite`, idempotence and risk decide how the broker retries after a disconnect. A non-idempotent write is never resent.
- Keep long work out of the main thread and out of a single tool call: start a job and let the client follow it.
- Optional ecosystem integrations live in their own assembly gated by `versionDefines`; the core package must compile without them.
- Python is pinned (`==`) in every `requirements*.txt`; `Tools~/validate_release.py` enforces it.
- Add or update tests with every behaviour change: pytest for `Broker~`, EditMode tests for the Editor side.

## Pull requests

- One topic per pull request, with a description of the behaviour change and how you verified it (test names, Unity version).
- Follow [Conventional Commits](https://www.conventionalcommits.org/) in commit titles (`fix(broker): ...`, `feat(editor): ...`), like the existing history.
- Update `CHANGELOG.md` under **Unreleased**.
- Do not bump versions; releases are cut by the maintainer (see *Release verification* in the README).
- Make sure CI is green.

By contributing you agree that your contribution is licensed under the repository's license, and that you take part under the [Code of Conduct](CODE_OF_CONDUCT.md).
