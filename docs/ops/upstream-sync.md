# Folding upstream into the fork

How to integrate `dmarc-analyzer-net/DmarcAnalyzerApp` (`upstream`) into this
fork (`MTG-Thomas/DmarcAnalyzerApp`, `origin`). Adapted from the Bifrost fork
method: the repo policy in `MTG-Thomas/bifrost-ops` (`repo-policy/bifrost.md`),
the merge rules in `Midtown-Technology-Group/bifrost` `AGENTS.md`, and that
fork's upstream-integration plan procedure (pinned inputs, per-commit
disposition, rehearsal, ancestry-preserving merge).

The reverse direction (proposing fork work upstream) is already covered in
`AGENTS.md`: land and verify in the fork first, then send small concrete
upstream pull requests. Nothing here changes that.

## Remotes and roles

- `origin` is the fork and the integration lane. `upstream` is a read-only
  source of commits, never a merge target.
- Upstream-bound fixes (anything intended for `dmarc-analyzer-net`) start from
  current `upstream/main`, in an isolated worktree, with no fork-local work on
  the branch. Do not open upstream pull requests for fork operations unless a
  human explicitly asks.
- Recent precedent: #32, #35, and #39 (`4c9bd31` "Merge upstream/main through
  `bdfc105`"). Read the latest one before starting.

## Fork boundaries a merge must preserve

Resolve conflicts in favor of these. Do not blanket-select upstream, and do
not mark unreviewed code integrated with an ours-only merge.

- **CI ownership.** The fork owns the extended workflows (tested-image dispatch
  to dogfood, CodeQL, Sonar, workflow lint, Dependabot). An upstream `ci.yml`
  must not overwrite them.
- **Releases.** Tag-driven only; merging to `main` publishes nothing, and no
  tag, release, or upstream proposal happens without explicit operator
  approval. See [release.md](release.md).
- **Deployment.** Images are published by CI from a tested fork commit and
  selected by digest in `bifrost-infra`; never a node-local or mutable-tag
  build. The `APP_MODE` contract holds: any value outside
  `api`/`worker`/`all`/`migrate` fails startup rather than defaulting.
- **Ingestion.** One bounded raw-payload orchestrator; mailbox and upload
  callers stay behind it. A machine-ingest caller selects a source, never a
  client; report routing, dedup, and the audit trail survive the merge.
- **Worker.** Exactly one ingestion worker per database (advisory lock);
  retention and mailbox-checkpoint behavior are unchanged by a sync.
- **Configuration.** `ConfigurationContractTests` fails the build if a setting
  exists in code and is missing from [configuration.md](configuration.md). Any
  setting an upstream commit adds must be documented there in the same PR.

## Procedure

1. **Pin the inputs** and record them in the PR: current fork `main` SHA, last
   imported upstream revision (the merge-base), selected upstream head, and the
   last integration PR. If `main` advances mid-work, refresh the base and
   repeat the rehearsal; keep the upstream target fixed.
2. **Write a commit-by-commit disposition** for the upstream delta: import,
   adapt to fork contracts, already satisfied (account for it without
   downgrading packages or manufacturing edits), or explicitly excluded with a
   reason.
3. **Rehearse before touching source.** In a scratch object store with
   read-only access, run `git merge-tree --write-tree
   --merge-base=<imported-base> <fork-head> <upstream-head>` and record the
   conflict inventory. It is evidence, not an implementation candidate. Expect
   an ordinary merge against the true common ancestor to show far more
   conflicts, most of them already-integrated changes resurfacing; that is the
   warning, not the work list.
4. **Implement in an isolated worktree** on refreshed fork `main` (a fresh
   worktree needs `.env` copied, never regenerated). Prepare the reconciled
   content in separately reviewable units. Resolve old-overlap conflicts
   against the already-landed fork behavior and the reviewed delta. Preserve
   fork content outside the selected paths except explicitly justified tests,
   generated artifacts, and shared adapters. Audit the final diff against
   current fork `main`, not merely conflict-marker removal. Do not invent a
   merge migration; verify the EF migration head is still coherent.
5. **Publish one PR and merge it as a merge commit.** Squash or rebase would
   collapse the two-parent ancestry the next sync depends on (a prior
   squash-merged integration landed one parent where the reviewed merge had
   two, and the trees differed). If branch rules ever prohibit merge commits,
   do not silently substitute squash; resolve it explicitly first.
6. **Verify parentage:** both the prior fork head and the selected upstream
   head are ancestors of the resulting `main`. Never rewrite published `main`
   history. A source merge does not deploy.

## Validation

Run the gates on the exact candidate, not on an earlier rehearsal tree.
Upstream green is not proof of the fork integration; known failures need a
durable disposition, not a retry-until-green.

- `dotnet build DmarcAnalyzerApp.slnx` and `dotnet test src/api.tests`.
- `dotnet test src/api.integrationtests` (needs `DMARC_TEST_POSTGRES`) for
  anything touching persistence, dedup, transactions, or migrations. InMemory
  cannot execute those paths.
- From `src/web`: `npm run build`, `npm test`, `npm run lint`.
- For user-facing changes: `docker compose up -d --build` and check the real
  app.
- Confirm no new setting is missing from [configuration.md](configuration.md)
  (the contract test enforces this, but check before pushing).

Rollout is a separate operation: CI publishes from the tested fork commit,
`bifrost-infra` pins the digest, and prior image refs are retained for
rollback.
