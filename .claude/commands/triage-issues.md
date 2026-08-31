---
description: Triage a set of GitHub issues, verifying each filed diagnosis before fixing them
---

Takes issue numbers as its argument (`/triage-issues 113-118`, `/triage-issues 42 51 77`), accepting
either a range or a list. If the argument is missing or unparseable, stop and ask — do not choose the
issues yourself.

Load the `revit-connector-development` skill and follow it. This command covers **intake and
sequencing only**; from the point where work is delegated, the skill's per-stage workflow and PR
review checklist govern, and are not restated here.

1. **Read every issue before touching any of them.** Issues filed in one session share root causes and
   share files, and both change the plan: two fixes landing in the same file are one PR, not two racing
   worktrees.

2. **Verify each filed diagnosis.** Separate the **symptom** (observed, usually reliable), the **cause**
   (a hypothesis), and the **suggested fix** (inherits the hypothesis, and may be unreachable). Then,
   cheapest evidence first:
   - Read the code that would have to be true. Cite the code, never the PRD — see "a specification is
     not behaviour".
   - Run it against live Revit where the claim is behavioural. `list_instances` first; the instance may
     belong to another session (`dev-environment.md`).
   - Confirm the suggested fix is reachable before designing around it. A guard needs an interception
     point; a drift check needs drift that can occur.

3. **Comment the corrected triage on every issue, before writing code.** One per issue, including those
   that were right — that is information too. State separately what was **verified**, what was
   **inferred**, and what was **tried and not reproduced**. Where the filed diagnosis was wrong, say so
   and say why: whoever filed it needs the reasoning, not just the verdict. Later readers see the issue,
   not this conversation, so an uncorrected diagnosis gets re-implemented.

4. **Group by shared files, not by theme** — that is what decides what can run in parallel. Order:
   documentation fixes first (fast, and they may be *causing* other issues), then defects, then anything
   needing design. Combine doc-only fixes touching one file into a single PR, and check any size or
   token budget on that file has headroom for all of them before promising both.

5. **Delegate each group to a worktree subagent**, per the skill's per-stage workflow. Each brief must
   additionally: state the **corrected** diagnosis and forbid implementing the issue's version; name the
   files another agent owns exclusively; and pass on live-environment cautions.

6. **Review, then merge only if authorized.** Both per the skill. Expect the most valuable finding to be
   an unsupported claim behind a correct conclusion; if you think a reviewer's finding is wrong, verify
   before rejecting it.

7. **Report and retrospect.** Give issues closed, PRs merged, issues filed, and the **net count** — it
   should go down. Then run the skill's milestone retrospective and route each lesson to `SKILL.md`,
   `caveats.md`, or `dev-environment.md`. A wrong hypothesis leaves no commit and no issue, so if it is
   not written down, nothing records it.
