package howto

import (
	"bufio"
	"bytes"
	"encoding/json"
	"fmt"
	"io"
	"sort"
	"strings"
)

// Bounds (CONVENTIONS.md: every retained buffer states its bound). Beyond
// any of them loading stops and the truncation is reported, never silent.
const (
	// MaxDocuments per corpus; the seed is tens, a mature corpus thousands.
	MaxDocuments = 20_000
	// MaxLineBytes bounds one JSONL line: a document is ~4 KB with a 16 KB
	// script bound, so 64 KB leaves room for long pitfall lists.
	MaxLineBytes = 64 * 1024
	// MaxStamps per sidecar: documents × versions × a few reruns.
	MaxStamps = 200_000
)

// Source of a document, reported on every hit.
const (
	SourceSeed   = "seed"
	SourceShared = "shared"
	SourceLocal  = "local"
)

// Corpus is one loaded corpus file: one document per lineage id.
type Corpus struct {
	Source string
	docs   map[string]*Document
	order  []string // ids in load order
	// absorbed maps a merged-away id to the surviving document's id.
	absorbed map[string]string
	// Skipped counts lines that failed validation; Problems holds their
	// messages (bounded to the first 50) for a notices[] record.
	Skipped  int
	Problems []string
	// NewerThanBroker is the highest schema_version seen above SchemaVersion,
	// or 0. Such documents are still loaded (unknown fields are allowed).
	NewerThanBroker int
	// Truncated is set when MaxDocuments or MaxLineBytes stopped the load.
	Truncated bool
}

// LoadCorpus reads JSONL. Every line is validated; an invalid line is skipped
// and counted, never fatal. A duplicate id is an error: the file format is one
// line per lineage, so two lines for one id means a broken write, not two
// revisions.
func LoadCorpus(r io.Reader, source string) (*Corpus, error) {
	c := &Corpus{Source: source, docs: map[string]*Document{}, absorbed: map[string]string{}}
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 0, 64*1024), MaxLineBytes)
	lineNo := 0
	for sc.Scan() {
		lineNo++
		line := bytes.TrimSpace(sc.Bytes())
		if len(line) == 0 {
			continue
		}
		if len(c.docs) >= MaxDocuments {
			c.Truncated = true
			c.problem(fmt.Sprintf("line %d: corpus exceeds %d documents; rest ignored", lineNo, MaxDocuments))
			break
		}
		d, err := ValidateDocument(line)
		if err != nil {
			c.Skipped++
			c.problem(fmt.Sprintf("line %d: %v", lineNo, err))
			continue
		}
		if d.SchemaVersion > SchemaVersion && d.SchemaVersion > c.NewerThanBroker {
			c.NewerThanBroker = d.SchemaVersion
		}
		if _, dup := c.docs[d.ID]; dup {
			return nil, fmt.Errorf("howto: line %d: duplicate id %q (the corpus holds one line per lineage)", lineNo, d.ID)
		}
		c.docs[d.ID] = d
		c.order = append(c.order, d.ID)
	}
	if err := sc.Err(); err != nil {
		if err == bufio.ErrTooLong {
			c.Truncated = true
			c.problem(fmt.Sprintf("line %d exceeds %d bytes; load stopped", lineNo+1, MaxLineBytes))
		} else {
			return nil, fmt.Errorf("howto: reading corpus: %w", err)
		}
	}
	for _, d := range c.docs {
		for _, old := range d.Absorbs {
			if _, live := c.docs[old]; live {
				c.problem(fmt.Sprintf("document %s absorbs %s, but %s still has its own line", d.ID, old, old))
				continue
			}
			c.absorbed[old] = d.ID
		}
	}
	return c, nil
}

func (c *Corpus) problem(msg string) {
	if len(c.Problems) < 50 {
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
		return c.docs[to], to, true
	}
	return nil, "", false
}

// Put adds a new lineage or replaces the lineage's line (an edit is the same
// id at rev+1). It refuses a rev that does not advance.
func (c *Corpus) Put(d *Document) error {
	if cur, ok := c.docs[d.ID]; ok {
		if d.Rev <= cur.Rev {
			return fmt.Errorf("howto: %s rev %d does not advance past the current rev %d", d.ID, d.Rev, cur.Rev)
		}
		c.docs[d.ID] = d
		return nil
	}
	if d.Rev != 1 {
		return fmt.Errorf("howto: new lineage %s must start at rev 1, got %d", d.ID, d.Rev)
	}
	c.docs[d.ID] = d
	c.order = append(c.order, d.ID)
	return nil
}

// Absorb merges lineage old into survivor: the survivor records the id, the
// old line is removed, and Get(old) follows the pointer from then on.
func (c *Corpus) Absorb(survivor, old string) error {
	s, ok := c.docs[survivor]
	if !ok {
		return fmt.Errorf("howto: absorb: no document %q", survivor)
	}
	if _, ok := c.docs[old]; !ok {
		return fmt.Errorf("howto: absorb: no document %q to absorb", old)
	}
	if survivor == old {
		return fmt.Errorf("howto: absorb: %q cannot absorb itself", old)
	}
	s.Absorbs = append(s.Absorbs, old)
	delete(c.docs, old)
	for i, id := range c.order {
		if id == old {
			c.order = append(c.order[:i], c.order[i+1:]...)
			break
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
	Stamps   []Stamp
	Skipped  int
	Problems []string
}

// LoadSidecar reads verification JSONL; invalid lines are skipped and counted.
func LoadSidecar(r io.Reader) (*Sidecar, error) {
	s := &Sidecar{}
	sc := bufio.NewScanner(r)
	sc.Buffer(make([]byte, 0, 4096), MaxLineBytes)
	lineNo := 0
	for sc.Scan() {
		lineNo++
		line := bytes.TrimSpace(sc.Bytes())
		if len(line) == 0 {
			continue
		}
		if len(s.Stamps) >= MaxStamps {
			if len(s.Problems) < 50 {
				s.Problems = append(s.Problems, fmt.Sprintf("line %d: sidecar exceeds %d stamps; rest ignored", lineNo, MaxStamps))
			}
			break
		}
		st, err := ValidateStamp(line)
		if err != nil {
			s.Skipped++
			if len(s.Problems) < 50 {
				s.Problems = append(s.Problems, fmt.Sprintf("line %d: %v", lineNo, err))
			}
			continue
		}
		s.Stamps = append(s.Stamps, *st)
	}
	if err := sc.Err(); err != nil {
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
		if d, _, ok := c.Get(st.ID); ok && st.Matches(d) {
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

// Override is the outcome of laying a local document over the shared corpus.
type Override struct {
	Doc *Document
	// Source is SourceLocal when the local document is served, else the
	// shared/seed source it fell back to.
	Source string
	// SharedRev is set when a local document shadows a shared lineage whose
	// revision differs, so the hit can say the shared corpus moved on.
	SharedRev int
	// Notice explains a local file that was NOT served, or a shadowing that
	// the user should know about; empty when nothing is noteworthy.
	Notice string
}

// Overlay applies the local-override rules (seed plan §4d) for one id:
//   - no local: the shared document, source shared/seed.
//   - local identical to shared (same id and script hash): the shared document
//     is served and the local copy is reported superseded-by-shared.
//   - local differs: the local document is served, marked local, and
//     SharedRev carries the shared revision so the hit can show it.
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
	case ScriptSHA256(ld.Script) == ScriptSHA256(sd.Script) && strings.TrimSpace(ld.Task) == strings.TrimSpace(sd.Task):
		return Override{Doc: sd, Source: shared.Source,
			Notice: fmt.Sprintf("local how-to %s is identical to the %s copy and is no longer indexed; delete the local file", id, shared.Source)}, true
	default:
		o := Override{Doc: ld, Source: SourceLocal, SharedRev: sd.Rev}
		if sd.Rev != ld.Rev {
			o.Notice = fmt.Sprintf("local how-to %s (rev %d) shadows the %s lineage, which is at rev %d", id, ld.Rev, shared.Source, sd.Rev)
		}
		return o, true
	}
}
