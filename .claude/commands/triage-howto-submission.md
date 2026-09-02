Takes issue numbers as its argument (`/triage-howto-submission 171 172`, or a range). If the argument
is missing or unparseable, stop and ask — do not choose issues yourself. Only issues labelled
`howto-submission` are in the queue; an unlabelled issue is not, even if it looks like one.

Load the `revit-connector-development` skill and follow it for anything that touches live Revit.
Design and guidelines: `revit/docs/howto-corpus-design.md`, `revit/docs/howto-seed-plan.md` (§3e
level-of-detail guidelines, §4c this flow). Document model and validator:
`revit/mcp-server/internal/howto`.

Per issue, in this order:

1. **Read and parse.** Pull the fenced ```json block out of the issue body and validate it
   (`go run ./cmd/howto-validate <file>` once that exists; until then, the package's
   `ValidateDocument` through a small test or script). A parse or schema failure is a **comment on
   the issue** listing every problem and asking the submitter to re-run `submit_howto` — never a
   guess at what they meant, never a hand-fix of a document you cannot validate.

2. **Scrub again, by eye.** The tool's scrubber is a filter, not a guarantee. Read every field —
   `task`, the script's comments and string literals, `pitfalls[].symptom`, `queries[].text` — for
   project names, paths, document titles, people or machines. Anything found: **close the issue**
   with a comment saying what class of thing was present (not the value), and file a bug against
   the scrubber. The point is that nothing private reaches the tracker, so a leak is a scrubber
   defect, not something to edit into shape.

3. **Decide the outcome before running anything.** Three outcomes, and the second is the one to
   reach for first:
   - **Fold into an existing how-to.** Search the corpus (`search_howto` once it exists; until
     then, a `members`-set and `tags` match over `revit/howto/corpus.jsonl`) for a document that
     teaches the same concept. The default is to fold: a good pitfall, a better step, or a version
     note joins that document as its next revision. Each document teaches one Revit feature or
     connector mechanism at moderate depth — a broader usage concept with numbered steps, not a
     one-line script — and the corpus will hold hundreds of them, one per niche feature. So the
     question is "which feature is this about?": if a document already teaches that feature, fold
     into it; only a feature no document covers starts a new lineage.
   - **Accept as a new lineage** when no document covers the feature. Assign the id (a kebab slug
     of the feature, not the specific task), `rev` 1.
   - **Reject** with a comment when it duplicates without adding, cannot be made to pass, or is
     out of scope.
   For a `howto-edit` submission the target is already named; diff the submitted revision against
   the current line and judge the *change*, not the whole document.

4. **Apply the `howto-reviewed` label, then run it, on a disposable fixture.** Only after the
   read-through: create a blank fixture document on the connected Revit (the harness's
   `createBlankFixtureDocument` shape), route the script at it by `document_id`, run it with
   `execute_script`, and record status, diagnostic and return value in a comment. A failure is not a
   rejection by itself — the fix is often one line — but **nothing enters the corpus without a
   passing run on at least one version**, and the run happens after a human read the script, never
   before. Close the fixture afterwards.

5. **Edit the prose to the §3e guidelines.** This is a human edit, not the submitter's: `task`
   as one or two sentences naming element type, operation and key member nouns; the script's
   comments carry the explanation (setup labelled, steps numbered, the *why* at the step where it
   matters), aiming at a moderate-depth concept document of roughly 3–8 KB rather than a one-liner;
   pitfalls one line each, symptom → cause → fix; `members` = what the script calls; `tags` as
   concept facets. `queries.miss` is evidence — keep it verbatim. If folding, merge into the
   existing document the same way (append pitfalls and queries, replace the script only if the
   new one is better and passed).

6. **Write and stamp.** Put the document into `revit/howto/corpus.jsonl` — a new line, or the
   lineage's existing line replaced by `rev + 1` (one line per lineage; git history is the audit
   trail) — with `provenance.kind: "submission"`, `ref` = the issue URL, `reviewed_by` = your
   GitHub login; keep the submitter's `contributors` entry, renumber its `rev` to the accepted one,
   and add yourself as `reviewer` only if you want the credit. Append the run's stamp to
   `revit/howto/verified.jsonl` (`by: harness`) and drop any stamp whose script hash no longer
   matches. For a fold, record the merged-away submission id in the survivor's `absorbs` only if it
   had already been a lineage; a never-accepted submission has nothing to absorb.

7. **One PR per triage run.** Title names the issues; body lists each outcome (folded into `<id>`
   rev N / new `<id>` / rejected); `Closes #…` for accepted and rejected alike. CI validates the
   corpus and the sidecar. Merge per the series' merge grant.

8. **Report.** Issues closed, documents added / revised / merged, rejections with their reason in
   one line each, and the queue size before and after — the same net-count discipline
   `/triage-issues` uses.

Never: run a submitted script before reading it; edit a document you could not validate; leave a
private value in a comment; accept a script without a passing run.
