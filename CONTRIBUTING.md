# Contributing

All work reaches `master` through a feature branch and a normal pull request. `master` is protected: you cannot
push it directly, and the required `verify` check must be green before a maintainer merges.

## Prerequisites

- **`git`** and the **GitHub CLI (`gh`)** installed and on `PATH`.
- **`gh` authenticated** ahead of time: run `gh auth login` once (`gh auth status` should succeed).
- A working build environment for the local verification (see `README.md` → *Setup*), and the pre-push hook
  installed once per clone with `.\scripts\setup-dev.ps1`.

## Branching

Branch from the latest `master`. Fetch first and branch directly off `origin/master`, so you do not need a local
`master` checkout and cannot branch from a stale one:

```
git fetch origin
git switch -c feat/short-description origin/master
```

Branch-name prefixes follow the usual convention: `feat/`, `fix/`, `docs/`, `chore/`. All work happens on the
branch — never commit or push directly to `master`. Commit messages and PR text are in English.

## Committing

- **Commit at each logically complete, independently revertible step** — a checkpoint you would be comfortable
  rolling back to. Do not commit half-finished work.
- **Before committing, run the fast checks directly relevant to your change.** You do **not** need to run the
  full Debug + Release verification on every commit; that runs once at push time (below).

## Pushing

- **Do not push every intermediate commit.** Push only when:
  - the task has reached a deliverable state; or
  - you have completed a related group of fixes after PR review or CI feedback; or
  - a push is explicitly requested.
- Once a PR exists, **batch related changes and push them together** rather than pushing every small commit —
  each push re-runs the full local verification and CI.

## Preparing and submitting the PR

When you first reach a deliverable state, write the PR body to a file with the three template sections from
[`.github/pull_request_template.md`](.github/pull_request_template.md) — **Summary**, **Verification**,
**Scope / limitations** — filled in from the real diff and real verification results (state honestly anything
you did not run). Then submit:

```
.\scripts\submit-pr.ps1 `
    -Title "Short English title" `
    -BodyFile "$env:TEMP\pr-body.md"
```

The script:

- runs preflight checks (right repo, `git`/`gh` present and authenticated, a real feature branch, a clean
  working tree, the pre-push hook installed, and a body carrying the three headings);
- pushes the branch — the pre-push hook runs the full Debug + Release verification; the script never uses
  `--no-verify`;
- opens a normal PR against `master`, or updates the existing open PR for the branch (never a duplicate);
- prints the real PR URL and waits for the required `verify` check.

If the check fails or times out, the script exits non-zero and leaves the PR open for you to fix. It also
accepts optional parameters `-BaseBranch` (default `master`), `-Remote` (default `origin`), `-TimeoutSeconds`,
and `-PollSeconds`.

## Merging

The **final merge is a maintainer's manual click on GitHub**, only after the required check is green. The
submission script does not merge, does not auto-merge, and never bypasses branch protection.
