# Client, project and model names

What was removed, what is enforced, and **what the history still contains**.

Last updated: 2026-07-30.

---

## 1. Verdict

| | |
| --- | --- |
| Working tree | **Clean.** 110 tracked files scanned; 0 findings against 9 terms plus 3 structural rules |
| Git history | **NOT clean.** Names remain in old commits and in commit *messages* — see §4 |
| Repository visibility | **Private** (`HorizunGroup/horizun-mcp`, confirmed via the GitHub API on 2026-07-30) |
| History rewritten | **No.** Not attempted, and not to be attempted without an explicit instruction — see §5 |

## 2. What was found and changed now

The previous passes edited the files somebody remembered. This one read every
tracked file, which is the difference between a scrub and a sweep. Four
references survived the earlier passes, all of them the *operator's user name*
rather than a client:

| File | Was | Now |
| --- | --- | --- |
| `docs/migration-plan.md` | `C:\Users\<name>\aec-model-bridge\` | `%USERPROFILE%\aec-model-bridge\` |
| `docs/migration-plan.md` | `C:\Users\<name>\horizun-revit-mcp\` | `%USERPROFILE%\horizun-revit-mcp\` |
| `docs/mcp-parity-matrix.md` | *"\<name\>'s method does not depend on…"* | *"the operator's method does not depend on…"* |
| `docs/production-readiness.md` | *"any machine that is not \<name\>'s"* | *"any machine that is not the build machine"* |

Client and project names were already absent from the working tree — removed by
`43729e0` and `ef861f6`. This pass confirms that by measurement rather than by
recollection.

Model names in the test suite were checked and **kept**: `TOWER.rvt`,
`MOD_ARCH_A.rvt`, `Autodesk Docs://Sample Project/…`, `A Model.rvt`. They are
placeholders, and replacing placeholders with other placeholders is motion.

## 3. What stops it coming back

`scripts/scan-sensitive.ps1`, run over `git ls-files` — every tracked file, every
time, in CI. Two kinds of check:

**Structural rules need no wordlist** and catch the shapes that leak whoever the
client turns out to be:

| Rule | Catches | Allowed |
| --- | --- | --- |
| `user-home-path` | `C:\Users\<a real name>` | `%USERPROFILE%`, `<user>`, `someone` |
| `email-address` | any address | `noreply@`, `example.com` |
| `cloud-project-path` | `Autodesk Docs://…`, `BIM 360://…` | `Sample Project`, `Example`, `Test` |

**The wordlist is deliberately NOT in this repository.** A scanner that greps for
a client name must contain that client name — so the file meant to prove the
names are gone becomes the file that publishes them, and it is the file nobody
thinks to check. It lives at `%USERPROFILE%\.horizun\sensitive-terms.txt`, one
term per line.

Two consequences, both deliberate:

- **A missing wordlist reports NOT RUN, never "clean".** "The name check did not
  run" and "the name check passed" are different answers and must never print the
  same way. `-RequireTerms` turns the first into a failure, which is how the
  release gate calls it.
- **A matched term is never echoed.** Findings say `file:line` and
  `<redacted>`. A CI log is a published artifact; a scanner that prints the
  secret it found has moved the leak, not closed it.

Verified by planting a leak: a file containing a real-looking home path, an
e-mail address and a cloud model path produced exactly three findings and exit
code 1. A scanner only ever observed saying "clean" has not been observed working.

**The list is a starting point.** It carries the terms found in this
repository's own history. It cannot check what it is not told, and completing it
for every client, site and person is the operator's to do.

## 4. The history retains the data — this is the part that is not fixed

The repository is **private**, so today the exposure is limited to whoever has
access to the GitHub organisation. That is the only thing making this tolerable,
and it is a permission setting, not a property of the data.

Names remain in:

- **File contents of old commits.** `git log -S` finds them across several
  commits; `git show <sha>:<path>` returns them in full. The working tree being
  clean does not remove a single byte of that.
- **Commit messages**, which no file edit can reach. At least seven subject lines
  name a project directly.
- **Any clone, fork or CI cache** taken before today, on any machine.

So:

> **If this repository is ever made public, the client and project names become
> public with it — regardless of the working tree being clean.** Changing the
> visibility flag is sufficient to disclose them. There is no state in which the
> current history and public visibility are both acceptable.

## 5. What was deliberately NOT done

**No history rewrite. No force-push.** `git filter-repo`, `filter-branch` and an
interactive rebase would each remove the names, and each rewrites every commit
id: every clone diverges, every open branch has to be re-based, and every
reference to a sha in the acceptance report, the changelog and the commit
messages themselves stops resolving. That is a decision for the person who owns
the repository, not a side effect of a cleanup task.

If it is wanted, the options in ascending order of cost:

1. **Leave it, keep the repository private.** Zero cost, and the exposure is a
   single settings toggle away. Adequate only while "private forever" is a
   decision somebody has actually made.
2. **Rewrite history with `git filter-repo`,** replacing terms in blobs *and*
   commit messages, then force-push. Every clone must be re-cloned. Requires
   explicit authorisation.
3. **Start a fresh repository** from the current tree with a single initial
   commit, archiving the old one privately. Keeps the code, loses the history
   and everything that cites it.

None of these is started. Say which, and it can be.
