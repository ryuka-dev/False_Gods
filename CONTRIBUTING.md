# Contributing

Work reaches `master` by pushing to it directly. The local **pre-push hook** runs the full verification
(`scripts/verify.ps1`, Debug and Release) and blocks the push if anything fails, so a broken build or a
`FG-ARCH-*` violation never leaves your machine. There is no required server-side check and no mandatory pull
request; CI still runs on every push as a visible re-check, but it does not gate anything.

> This is a deliberate trade for a solo repository. The blocking gate is now the pre-push hook, which is
> client-side: a `git push --no-verify`, or a clone that never ran `setup-dev.ps1`, can bypass it. That is
> weaker than a server-side required check, in exchange for a lighter flow. Pull requests remain available
> (below) for anything you want reviewed, or recorded by CI, before it lands.

## Prerequisites

- **`git`** installed and on `PATH`. **`gh`** (GitHub CLI, authenticated) only if you choose to open a pull
  request.
- A working build environment for local verification (see `README.md` -> *Setup*), and the pre-push hook
  installed once per clone with `.\scripts\setup-dev.ps1`. **The hook is the gate — do not skip installing it.**

## The normal flow: commit and push to `master`

```
git switch master
git pull --ff-only origin master
# ... edit ...
git commit -m "Short English message"
git push                # pre-push hook runs the full verify (Debug + Release); blocks on failure
```

- **Commit at each logically complete, independently revertible step** — a checkpoint you would be comfortable
  rolling back to. Do not commit half-finished work.
- **Before you push, exercise the change the way it is actually verified.** For a build or architecture change,
  the pre-push hook is enough. For anything with runtime behaviour — a probe, a plugin, anything you load into
  the game — build it locally, load it, and look, *before* you push. With no pull request in the way, that
  in-game check is the only gate for behaviour the hook cannot see.
- **Push at deliverable checkpoints, not on every commit.** Each push re-runs the full local verify (~1 min)
  and fires CI. Batch a related group of commits and push them together.
- Commit messages are in English.

## When you want review: open a pull request instead

Pull requests are optional now, but still the right tool when you want a change looked at, or a CI record,
before it reaches `master`. From a feature branch, write the body with the three template sections from
[`.github/pull_request_template.md`](.github/pull_request_template.md) — **Summary**, **Verification**,
**Scope / limitations** — filled in from the real diff and real verification results (state honestly anything
you did not run). Then:

```
.\scripts\submit-pr.ps1 `
    -Title "Short English title" `
    -BodyFile "$env:TEMP\pr-body.md"
```

The script runs preflight checks (right repo, `git`/`gh` present and authenticated, a real feature branch, a
clean working tree, the pre-push hook installed, and a body carrying the three headings); pushes the branch —
the pre-push hook runs the full Debug + Release verification, and the script never uses `--no-verify`; opens a
normal PR against `master`, or updates the branch's existing open PR (never a duplicate); prints the real PR
URL; and waits for the CI `verify` check to report. It never merges. Merge the PR yourself on GitHub when you
are satisfied. It also accepts optional `-BaseBranch` (default `master`), `-Remote` (default `origin`),
`-TimeoutSeconds`, and `-PollSeconds`.
