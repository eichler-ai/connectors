---
description: Triage a set of GitHub issues, verify each filed diagnosis, then fix them in parallel worktrees with independent review
---

Takes a set of issue numbers as its argument (`/triage-issues 113-118`, `/triage-issues 42 51 77`).
If none is given, stop and ask which issues — do not pick them yourself.

Load the `revit-connector-development` skill first and follow it; this command is the *intake and
sequencing* process that runs before its per-stage workflow, not a replacement for it.

## Why this exists

Of six issues filed from one live agent session, **four had a diagnosis that did not survive contact
with the running code**, and in three of those, implementing the suggested fix would have shipped the
wrong thing. The issues were all worth filing — each pointed at something real — but the reported
*mechanism* was inferred mid-task by whoever hit the symptom, and inference is what fails. So the
expensive part of this process is deliberately at the front.

## 1. Read every issue before touching any of them

Read all of them first. Issues filed in one session share root causes and share files, and both facts
change the plan: two issues whose fixes land in the same file must be one PR, not two racing worktrees.

## 2. Verify each filed diagnosis — this is the step that pays

For each issue, separate three things: the **symptom** (usually reliable, it was observed), the
**cause** (a hypothesis), and the **suggested fix** (inherits the hypothesis, and may be unreachable).

Check the cause against reality, cheapest evidence first:

- **Read the code that would have to be true.** Several diagnoses die here in minutes. Do not stop at
  the PRD — a specification describes intent, and the implementation can differ. Cite the code.
- **Run it against live Revit** where the claim is behavioural. `list_instances` first: the instance
  may belong to another session (see `dev-environment.md`), and a read-only probe is fine where a
  dialog-popping one is not.
- **Check the suggested fix is reachable at all** before designing around it. A guard needs an
  interception point; a drift check needs drift that can actually occur.

Watch for the specific shapes seen so far: an API misuse reported as an unsupported *operation*;
documentation that is itself the defect, believed and obeyed; a proposed guard with no hook to hang
on; a proposed check that cannot fail by construction.

## 3. Comment the corrected triage on every issue, before writing code

One comment per issue, even where the filed diagnosis was right (say so briefly — that is information
too). This is the step most likely to be skipped and the one with the most leverage: other agents and
future sessions read the issue, not this conversation, and an uncorrected diagnosis gets re-derived
and re-implemented.

Each comment states plainly what was **verified**, what was **inferred**, and what was **tried and
not reproduced** — those are three different confidence levels and collapsing them is how a wrong
claim ships. Where the filed diagnosis was wrong, say so directly and say why; the person who filed it
most needs the reasoning corrected, not just the conclusion.

## 4. Group and sequence

Group by **shared files**, not by theme — that is what determines whether two fixes can run in
parallel. Then order by: issues whose fix is documentation (fast, and they may be *causing* other
issues); then real defects; then anything needing design.

Prefer combining doc-only fixes that touch the same file into one PR. If a token/size budget applies
to a file, check the headroom before promising two changes will fit.

## 5. Fix in parallel worktree subagents

One subagent per group, `isolation: "worktree"`. In each brief:

- State the **corrected** diagnosis and tell it not to implement the issue's version.
- Name the files another agent owns exclusively, so it does not conflict.
- Require mutation testing for new tests (the house rule), both TFMs before PR for C#, and tier 2
  where the skill requires it.
- Tell it to get its own independent review, fix findings, and **stop before merging** — report back.
- Pass on live-environment cautions: the shared Revit, and anything that needs a human click.

## 6. Review before merge, including docs-only PRs

Read every PR yourself, then have a fresh reviewer with no shared context read it. Docs- and
comment-only diffs need this most — there is no test to contradict an unsupported claim.

Expect the highest-value finding to be an **unsupported claim behind a correct conclusion**. Four of
those appeared in one day, in four separate PRs, every one caught by review and none by its author.
If a reviewer's finding is wrong, say so and show the evidence — but check yourself before rejecting,
since the reviewer is usually right about this class.

## 7. Merge only with authorization

Per the skill: merging needs human authorization, which may be pre-granted for the session or scope.
Without a grant, take each PR to reviewed-and-green and stop. Never infer a grant from earlier PRs in
the same series.

## 8. Close the loop

Report: issues closed, PRs merged, issues filed, and the **net count** — the goal is that it goes
down. Then run the skill's milestone retrospective and put each lesson where it belongs (`SKILL.md`
for a rule, `caveats.md` for what a symptom meant, `dev-environment.md` for a machine property or a
dead end). A wrong hypothesis leaves no commit and no issue, so if it is not written down, nothing
anywhere records it.
