package howto

import (
	"bytes"
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"fmt"
	"io/fs"
	"path"
	"sort"
	"strings"
	"sync"
)

// The shared corpus ships INSIDE the broker: one JSON file per lineage under
// corpus/ (the latest revision only; git history is the audit trail) and the
// sweep's sidecar of verification stamps. A file per document, rather than
// the one-line-per-lineage JSONL the loader also reads, because these are
// edited by hand at triage and reviewed as diffs -- a 6 KB script on one
// JSONL line is unreviewable. LoadCorpus still gets JSONL: the files are
// joined into it at load, so the one loader and its checks serve both.
//
//go:embed corpus/*.json corpus/verified.jsonl
var corpusFS embed.FS

// CorpusDir is where the files live in the repository, for messages.
const CorpusDir = "revit/mcp-server/internal/howto/corpus"

// Version identifies a corpus build: how many documents, a content hash
// (documents only, in id order -- stamps do not change it), and the Revit
// versions at least one document is verified on.
type Version struct {
	Documents  int      `json:"documents"`
	Hash       string   `json:"hash"`
	VerifiedOn []string `json:"verified_on,omitempty"`
}

// String is the -version / get_skills rendering.
func (v Version) String() string {
	s := fmt.Sprintf("how-to corpus: %d documents, hash %s", v.Documents, v.Hash)
	if len(v.VerifiedOn) > 0 {
		s += ", verified on Revit " + strings.Join(v.VerifiedOn, ", ")
	}
	return s
}

var (
	embeddedOnce   sync.Once
	embeddedCorpus *Corpus
	embeddedStamps []Stamp
	embeddedVer    Version
	embeddedErr    error
)

// Embedded returns the corpus compiled into this binary, its stamps and its
// version. Loaded once; a malformed file is an error here (it is a build
// artefact the repo's tests validate), not a skipped line.
func Embedded() (*Corpus, []Stamp, Version, error) {
	embeddedOnce.Do(func() {
		embeddedCorpus, embeddedStamps, embeddedVer, embeddedErr = loadEmbedded(corpusFS)
	})
	return embeddedCorpus, embeddedStamps, embeddedVer, embeddedErr
}

func loadEmbedded(fsys fs.FS) (*Corpus, []Stamp, Version, error) {
	names, err := fs.Glob(fsys, "corpus/*.json")
	if err != nil {
		return nil, nil, Version{}, err
	}
	sort.Strings(names)
	var jsonl bytes.Buffer
	h := sha256.New()
	for _, name := range names {
		raw, err := fs.ReadFile(fsys, name)
		if err != nil {
			return nil, nil, Version{}, err
		}
		d, err := ValidateDocument(raw)
		if err != nil {
			return nil, nil, Version{}, fmt.Errorf("%s/%s: %w", CorpusDir, path.Base(name), err)
		}
		if want := d.ID + ".json"; path.Base(name) != want {
			return nil, nil, Version{}, fmt.Errorf("%s/%s: file is named for id %q; rename it to %s", CorpusDir, path.Base(name), d.ID, want)
		}
		line, err := MarshalDocument(d)
		if err != nil {
			return nil, nil, Version{}, err
		}
		h.Write(line)
		h.Write([]byte{'\n'})
		jsonl.Write(line)
		jsonl.WriteByte('\n')
	}
	c, err := LoadCorpus(&jsonl, "embedded")
	if err != nil {
		return nil, nil, Version{}, err
	}
	if len(c.Problems) > 0 {
		return nil, nil, Version{}, fmt.Errorf("embedded corpus: %s", strings.Join(c.Problems, "; "))
	}
	var stamps []Stamp
	if raw, err := fs.ReadFile(fsys, "corpus/verified.jsonl"); err == nil && len(bytes.TrimSpace(raw)) > 0 {
		sc, err := LoadSidecar(bytes.NewReader(raw))
		if err != nil {
			return nil, nil, Version{}, fmt.Errorf("%s/verified.jsonl: %w", CorpusDir, err)
		}
		if len(sc.Problems) > 0 {
			return nil, nil, Version{}, fmt.Errorf("%s/verified.jsonl: %s", CorpusDir, strings.Join(sc.Problems, "; "))
		}
		stamps, _ = sc.Prune(c)
	}
	ver := Version{Documents: c.Len(), Hash: hex.EncodeToString(h.Sum(nil))[:12]}
	seen := map[string]bool{}
	for _, st := range stamps {
		if st.Status == StampPassed && !seen[st.RevitVersion] {
			seen[st.RevitVersion] = true
			ver.VerifiedOn = append(ver.VerifiedOn, st.RevitVersion)
		}
	}
	sort.Strings(ver.VerifiedOn)
	return c, stamps, ver, nil
}
