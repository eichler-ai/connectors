package howto

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"sort"
)

// Bounds (CONVENTIONS.md: every retained buffer states its bound). Beyond
// any of them loading stops or skips and the truncation is reported, never
// silent.
const (
	// MaxDocuments per corpus; the seed is tens, a mature corpus thousands.
	MaxDocuments = 20_000
	// MaxLineBytes bounds one JSONL line: a document is ~4 KB with a 16 KB
	// script bound, so 64 KB leaves room for long pitfall lists. A longer
	// line is skipped and counted; the lines after it still load.
	MaxLineBytes = 64 * 1024
	// MaxStamps per sidecar: documents × versions × a few reruns.
	MaxStamps = 200_000
	// MaxProblems bounds the per-load problem list kept for notices[].
	MaxProblems = 50
)

// Source of a document, reported on every hit.
const (
	SourceSeed   = "seed"
	SourceShared = "shared"
	SourceLocal  = "local"
)

// Corpus is one loaded corpus file: one document per lineage id. It is not
// safe for concurrent use; a reader that serves searches builds one and then
// treats it as immutable, and rebuilds on change.
type Corpus struct {
	Source string
	docs   map[string]*Document
	order  []string // ids in load order
	// absorbed maps a merged-away id to the surviving document's id.
	absorbed map[string]string
	// Skipped counts lines that failed validation or exceeded MaxLineBytes;
	// Problems holds their messages (bounded to MaxProblems) for a notices[]
	// record.
	Skipped  int
	Problems []string
	// NewerThanBroker is the highest schema_version seen above SchemaVersion,
	// or 0. Such documents are still loaded (unknown fields are allowed).
	NewerThanBroker int
	// Truncated is set when MaxDocuments stopped the load.
	Truncated bool
}

// forEachLine calls fn for every newline-terminated line, reporting a line
// longer than maxLen as tooLong (with its content discarded) instead of
// aborting the whole read, which bufio.Scanner would.
func forEachLine(r io.Reader, maxLen int, fn func(lineNo int, line []byte, tooLong bool) bool) error {
	br := bufio.NewReaderSize(r, 64*1024)
	lineNo := 0
	for {
		lineNo++
		var buf []byte
		tooLong := false
		for {
			chunk, isPrefix, err := br.ReadLine()
			if err == io.EOF {
				if len(buf) == 0 && len(chunk) == 0 {
					return nil
				}
				buf = append(buf, chunk...)
				if !fn(lineNo, bytes.TrimSpace(buf), tooLong || len(buf) > maxLen) {
					return nil
				}
				return nil
			}
			if err != nil {
				return err
			}
			if !tooLong {
				if len(buf)+len(chunk) > maxLen {
					tooLong = true
					buf = nil
				} else {
					buf = append(buf, chunk...)
				}
			}
			if !isPrefix {
				break
			}
		}
		if !fn(lineNo, bytes.TrimSpace(buf), tooLong) {
			return nil
		}
	}
}

// LoadCorpus reads JSONL. Every line is validated; an invalid or oversized
// line is skipped and counted, never fatal. A duplicate id is an error: the
// file format is one line per lineage, so two lines for one id means a
// broken write, not two revisions.
func LoadCorpus(r io.Reader, source string) (*Corpus, error) {
	c := &Corpus{Source: source, docs: map[string]*Document{}, absorbed: map[string]string{}}
	var dupErr error
	err := forEachLine(r, MaxLineBytes, func(lineNo int, line []byte, tooLong bool) bool {
		if tooLong {
			c.Skipped++
			c.problem(fmt.Sprintf("line %d exceeds %d bytes; skipped", lineNo, MaxLineBytes))
			return true
		}
		if len(line) == 0 {
			return true
		}
		if len(c.docs) >= MaxDocuments {
			c.Truncated = true
			c.problem(fmt.Sprintf("line %d: corpus exceeds %d documents; rest ignored", lineNo, MaxDocuments))
			return false
		}
		d, err := ValidateDocument(line)
		if err != nil {
			c.Skipped++
			c.problem(fmt.Sprintf("line %d: %v", lineNo, err))
			return true
		}
		if d.SchemaVersion > SchemaVersion && d.SchemaVersion > c.NewerThanBroker {
			c.NewerThanBroker = d.SchemaVersion
		}
		if _, dup := c.docs[d.ID]; dup {
			dupErr = fmt.Errorf("howto: line %d: duplicate id %q (the corpus holds one line per lineage)", lineNo, d.ID)
			return false
		}
		c.docs[d.ID] = d
		c.order = append(c.order, d.ID)
		return true
	})
	if err != nil {
		return nil, fmt.Errorf("howto: reading corpus: %w", err)
	}
	if dupErr != nil {
		return nil, dupErr
	}
	// Consistency of absorbs across the file: a merged-away id must not still
	// have a line, and must not be claimed by two survivors.
	for _, id := range c.order {
		d := c.docs[id]
		for _, old := range d.Absorbs {
			if _, live := c.docs[old]; live {
				c.problem(fmt.Sprintf("document %s absorbs %s, but %s still has its own line", d.ID, old, old))
				continue
			}
			if prev, taken := c.absorbed[old]; taken && prev != d.ID {
				c.problem(fmt.Sprintf("id %s is absorbed by both %s and %s; %s wins", old, prev, d.ID, prev))
				continue
			}
			c.absorbed[old] = d.ID
		}
	}
	return c, nil
}

func (c *Corpus) problem(msg string) {
	if len(c.Problems) < MaxProblems {
		c.Problems = append(c.Problems, msg)
	}
}

// Len is the number of documents.
func (c *Corpus) Len() int { return len(c.docs) }

// IDs returns ids in load order.
func (c *Corpus) IDs() []string { return append([]string(nil), c.order...) }

// Get returns the document for id, following an absorbs pointer when id was
// merged into another lineage. redirected is the surviving id in that case.
func (c *Corpus) Get(id string) (d *Document, redirected string, ok bool) {
	if d, ok := c.docs[id]; ok {
		return d, "", true
	}
	if to, ok := c.absorbed[id]; ok {
		if d, ok := c.docs[to]; ok {
			return d, to, true
		}
	}
	return nil, "", false
}

// Put adds a new lineage (rev 1) or replaces the lineage's line with the next
// revision (exactly rev+1, the seed plan's edit shape). The document is
// validated first, through the same path a loaded line takes, so a Go-built
// document cannot enter the corpus in a shape the file would reject.
func (c *Corpus) Put(d *Document) error {
	raw, err := MarshalDocument(d)
	if err != nil {
		return err
	}
	if _, err := ValidateDocument(raw); err != nil {
		return err
	}
	if cur, ok := c.docs[d.ID]; ok {
		if d.Rev != cur.Rev+1 {
			return fmt.Errorf("howto: %s is at rev %d; an edit must be rev %d, got %d", d.ID, cur.Rev, cur.Rev+1, d.Rev)
		}
		c.docs[d.ID] = d
		return nil
	}
	if _, wasAbsorbed := c.absorbed[d.ID]; wasAbsorbed {
		return fmt.Errorf("howto: %s was merged into %s; edit that lineage instead", d.ID, c.absorbed[d.ID])
	}
	if d.Rev != 1 {
		return fmt.Errorf("howto: new lineage %s must start at rev 1, got %d", d.ID, d.Rev)
	}
	c.docs[d.ID] = d
	c.order = append(c.order, d.ID)
	return nil
}

// Absorb merges lineage old into survivor: the survivor records old (and
// everything old had itself absorbed), the old line is removed, and Get on
// any id in the chain follows the pointer to the survivor.
func (c *Corpus) Absorb(survivor, old string) error {
	if survivor == old {
		return fmt.Errorf("howto: absorb: %q cannot absorb itself", old)
	}
	s, ok := c.docs[survivor]
	if !ok {
		return fmt.Errorf("howto: absorb: no document %q", survivor)
	}
	o, ok := c.docs[old]
	if !ok {
		return fmt.Errorf("howto: absorb: no document %q to absorb", old)
	}
	s.Absorbs = append(s.Absorbs, old)
	s.Absorbs = append(s.Absorbs, o.Absorbs...)
	delete(c.docs, old)
	for i, id := range c.order {
		if id == old {
			c.order = append(c.order[:i], c.order[i+1:]...)
			break
		}
	}
	for id, to := range c.absorbed {
		if to == old {
			c.absorbed[id] = survivor
		}
	}
	c.absorbed[old] = survivor
	return nil
}

// WriteCorpus writes JSONL sorted by id, one line per lineage, so the file
// diffs cleanly in git.
func WriteCorpus(w io.Writer, c *Corpus) error {
	ids := c.IDs()
	sort.Strings(ids)
	bw := bufio.NewWriter(w)
	for _, id := range ids {
		b, err := MarshalDocument(c.docs[id])
		if err != nil {
			return err
		}
		if _, err := bw.Write(b); err != nil {
			return err
		}
		if err := bw.WriteByte('\n'); err != nil {
			return err
		}
	}
	return bw.Flush()
}

// Sidecar is a loaded verification file.
type Sidecar struct {
	Stamps []Stamp
	// Skipped counts invalid or oversized lines; Problems is bounded to
	// MaxProblems.
	Skipped  int
	Problems []string
}

func (s *Sidecar) problem(msg string) {
	if len(s.Problems) < MaxProblems {
		s.Problems = append(s.Problems, msg)
	}
}

// LoadSidecar reads verification JSONL; invalid or oversized lines are
// skipped and counted, the same way LoadCorpus treats them.
func LoadSidecar(r io.Reader) (*Sidecar, error) {
	s := &Sidecar{}
	err := forEachLine(r, MaxLineBytes, func(lineNo int, line []byte, tooLong bool) bool {
		if tooLong {
			s.Skipped++
			s.problem(fmt.Sprintf("line %d exceeds %d bytes; skipped", lineNo, MaxLineBytes))
			return true
		}
		if len(line) == 0 {
			return true
		}
		if len(s.Stamps) >= MaxStamps {
			s.problem(fmt.Sprintf("line %d: sidecar exceeds %d stamps; rest ignored", lineNo, MaxStamps))
			return false
		}
		st, err := ValidateStamp(line)
		if err != nil {
			s.Skipped++
			s.problem(fmt.Sprintf("line %d: %v", lineNo, err))
			return true
		}
		s.Stamps = append(s.Stamps, *st)
		return true
	})
	if err != nil {
		return nil, fmt.Errorf("howto: reading sidecar: %w", err)
	}
	return s, nil
}

// WriteSidecar writes stamps as JSONL in a stable order (id, rev, version, at).
func WriteSidecar(w io.Writer, stamps []Stamp) error {
	sorted := append([]Stamp(nil), stamps...)
	sort.Slice(sorted, func(i, j int) bool {
		a, b := sorted[i], sorted[j]
		if a.ID != b.ID {
			return a.ID < b.ID
		}
		if a.Rev != b.Rev {
			return a.Rev < b.Rev
		}
		if a.RevitVersion != b.RevitVersion {
			return a.RevitVersion < b.RevitVersion
		}
		return a.At.Before(b.At)
	})
	bw := bufio.NewWriter(w)
	enc := json.NewEncoder(bw)
	for _, st := range sorted {
		if err := enc.Encode(st); err != nil {
			return err
		}
	}
	return bw.Flush()
}

// Prune drops stamps that no longer match any document in c: a stamp for an
// absent id, another revision, or a changed script is stale (design note §3).
// It returns the kept stamps and the number dropped.
func (s *Sidecar) Prune(c *Corpus) (kept []Stamp, dropped int) {
	for _, st := range s.Stamps {
		if d, redirected, ok := c.Get(st.ID); ok && redirected == "" && st.Matches(d) {
			kept = append(kept, st)
		} else {
			dropped++
		}
	}
	return kept, dropped
}

// Verification is what a reader learns about one document from the sidecar.
type Verification struct {
	// Passed and Failed list Revit versions with a current stamp of each
	// status; a version is in at most one of them (harness beats session,
	// newer beats older).
	Passed []string
	Failed []string
	// ByVersion keeps the winning stamp per version for describe_howto.
	ByVersion map[string]Stamp
}

// VerifiedOn joins the sidecar with one document. Only stamps that match the
// document's current revision and script count.
func VerifiedOn(d *Document, stamps []Stamp) Verification {
	v := Verification{ByVersion: map[string]Stamp{}}
	for _, st := range stamps {
		if !st.Matches(d) {
			continue
		}
		cur, ok := v.ByVersion[st.RevitVersion]
		if !ok || stampBeats(st, cur) {
			v.ByVersion[st.RevitVersion] = st
		}
	}
	for ver, st := range v.ByVersion {
		if st.Status == StampPassed {
			v.Passed = append(v.Passed, ver)
		} else {
			v.Failed = append(v.Failed, ver)
		}
	}
	sort.Strings(v.Passed)
	sort.Strings(v.Failed)
	return v
}

// stampBeats: a harness stamp outranks a session stamp; otherwise the newer wins.
func stampBeats(a, b Stamp) bool {
	if (a.By == ByHarness) != (b.By == ByHarness) {
		return a.By == ByHarness
	}
	return a.At.After(b.At)
}

// §01 codes for Override.Notice.
const (
	CodeLocalSupersededByShared = "howto-local-superseded-by-shared"
	CodeLocalShadowsShared      = "howto-local-shadows-shared"
)

// Override is the outcome of laying a local document over the shared corpus.
type Override struct {
	Doc *Document
	// Source is SourceLocal when the local document is served, else the
	// shared/seed source it fell back to.
	Source string
	// SharedRev is set when a local document shadows a shared lineage, so the
	// hit can show how far the shared corpus has moved.
	SharedRev int
	// Code and Notice describe a local file that was NOT served, or a
	// shadowing the user should know about; empty when nothing is noteworthy.
	Code   string
	Notice string
}

// Overlay applies the local-override rules (seed plan §4d) for one id:
//   - no local: the shared document, source shared/seed.
//   - local identical to shared (same id and script hash): the shared
//     document is served and the local copy is reported superseded-by-shared.
//   - local differs: the local document is served, marked local, with
//     SharedRev; when the shared lineage has moved past the local revision the
//     hit also carries a shadowing notice.
func Overlay(shared *Corpus, local *Corpus, id string) (Override, bool) {
	var sd, ld *Document
	if shared != nil {
		sd, _, _ = shared.Get(id)
	}
	if local != nil {
		ld, _, _ = local.Get(id)
	}
	switch {
	case sd == nil && ld == nil:
		return Override{}, false
	case ld == nil:
		return Override{Doc: sd, Source: shared.Source}, true
	case sd == nil:
		return Override{Doc: ld, Source: SourceLocal}, true
	case ScriptSHA256(ld.Script) == ScriptSHA256(sd.Script):
		return Override{Doc: sd, Source: shared.Source, Code: CodeLocalSupersededByShared,
			Notice: fmt.Sprintf("local how-to %s has the same script as the %s copy and is no longer indexed; delete the local file", id, shared.Source)}, true
	default:
		o := Override{Doc: ld, Source: SourceLocal, SharedRev: sd.Rev}
		if sd.Rev > ld.Rev {
			o.Code = CodeLocalShadowsShared
			o.Notice = fmt.Sprintf("local how-to %s (rev %d) shadows the %s lineage, which has moved on to rev %d", id, ld.Rev, shared.Source, sd.Rev)
		}
		return o, true
	}
}
