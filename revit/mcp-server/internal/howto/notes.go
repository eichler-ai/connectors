package howto

import (
	"fmt"
	"sort"
	"strings"
)

// CorpusChanges is what changed between two corpus snapshots, by lineage id
// (seed plan §1: "release notes name what changed").
type CorpusChanges struct {
	Added   []string          // new lineages
	Revised map[string][2]int // id -> {old rev, new rev}
	Merged  map[string]string // absorbed id -> surviving id
	Removed []string          // gone without an absorbs pointer
	// ScriptOnly lists revised lineages whose rev did not change but whose
	// script text did -- a maintainer edit that forgot to bump rev, which
	// would leave every stamp for the old script looking current.
	ScriptOnly []string
}

// IsEmpty reports no change at all.
func (c CorpusChanges) IsEmpty() bool {
	return len(c.Added) == 0 && len(c.Revised) == 0 && len(c.Merged) == 0 && len(c.Removed) == 0 && len(c.ScriptOnly) == 0
}

// DiffCorpus compares two snapshots of the shared corpus. old may be nil
// (a first release), in which case every document is Added.
func DiffCorpus(old, cur *Corpus) CorpusChanges {
	ch := CorpusChanges{Revised: map[string][2]int{}, Merged: map[string]string{}}
	for _, id := range cur.IDs() {
		nd := cur.docs[id]
		if old == nil {
			ch.Added = append(ch.Added, id)
			continue
		}
		od, ok := old.docs[id]
		switch {
		case !ok:
			ch.Added = append(ch.Added, id)
		case nd.Rev != od.Rev:
			ch.Revised[id] = [2]int{od.Rev, nd.Rev}
		case nd.Script != od.Script:
			ch.ScriptOnly = append(ch.ScriptOnly, id)
		}
	}
	if old != nil {
		for _, id := range old.IDs() {
			if _, still := cur.docs[id]; still {
				continue
			}
			if to, ok := cur.absorbed[id]; ok {
				ch.Merged[id] = to
			} else {
				ch.Removed = append(ch.Removed, id)
			}
		}
	}
	sort.Strings(ch.Added)
	sort.Strings(ch.Removed)
	sort.Strings(ch.ScriptOnly)
	return ch
}

// ReleaseNotes renders the changes as the "How-tos" section of a release's
// notes: one line per lineage with its title, so a reader can tell what a
// corpus-only release brought without opening the diff. An empty change set
// renders a one-line "unchanged" note so the section is never silently
// absent.
func ReleaseNotes(ch CorpusChanges, cur *Corpus, ver Version) string {
	var b strings.Builder
	b.WriteString("## How-tos\n\n")
	fmt.Fprintf(&b, "Corpus: %d documents, hash %s", ver.Documents, ver.Hash)
	if len(ver.VerifiedOn) > 0 {
		fmt.Fprintf(&b, ", verified on Revit %s", strings.Join(ver.VerifiedOn, ", "))
	}
	b.WriteString(".\n")
	if ch.IsEmpty() {
		b.WriteString("\nNo how-to changed in this release.\n")
		return b.String()
	}
	title := func(id string) string {
		if d, _, ok := cur.Get(id); ok {
			return d.Title
		}
		return ""
	}
	section := func(heading string, ids []string, line func(id string) string) {
		if len(ids) == 0 {
			return
		}
		fmt.Fprintf(&b, "\n**%s**\n", heading)
		for _, id := range ids {
			fmt.Fprintf(&b, "- %s\n", line(id))
		}
	}
	section("Added", ch.Added, func(id string) string { return fmt.Sprintf("`%s` — %s", id, title(id)) })
	revised := make([]string, 0, len(ch.Revised))
	for id := range ch.Revised {
		revised = append(revised, id)
	}
	sort.Strings(revised)
	section("Revised", revised, func(id string) string {
		r := ch.Revised[id]
		return fmt.Sprintf("`%s` (rev %d → %d) — %s", id, r[0], r[1], title(id))
	})
	merged := make([]string, 0, len(ch.Merged))
	for id := range ch.Merged {
		merged = append(merged, id)
	}
	sort.Strings(merged)
	section("Merged", merged, func(id string) string {
		return fmt.Sprintf("`%s` → `%s` — %s", id, ch.Merged[id], title(ch.Merged[id]))
	})
	section("Removed", ch.Removed, func(id string) string { return fmt.Sprintf("`%s`", id) })
	section("Script changed without a revision bump (stamps for these are stale; fix before shipping)", ch.ScriptOnly,
		func(id string) string { return fmt.Sprintf("`%s` — %s", id, title(id)) })
	return b.String()
}
