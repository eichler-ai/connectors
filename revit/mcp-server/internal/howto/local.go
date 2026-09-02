package howto

import (
	"fmt"
	"os"
	"path/filepath"
	"sort"
)

// LoadLocalDir reads a local corpus directory: one document per <id>.json
// (pretty-printed is fine), skipping the outbox and sidecar. Invalid files
// are counted and reported, never fatal; two files carrying one id is
// reported and the first (by name) wins.
func LoadLocalDir(dir string) (*Corpus, error) {
	c := &Corpus{Source: SourceLocal, docs: map[string]*Document{}, absorbed: map[string]string{}}
	entries, err := os.ReadDir(dir)
	if err != nil {
		if os.IsNotExist(err) {
			return c, nil
		}
		return nil, fmt.Errorf("howto: reading local corpus %s: %w", dir, err)
	}
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".json" {
			continue
		}
		names = append(names, e.Name())
	}
	sort.Strings(names)
	for _, name := range names {
		if len(c.docs) >= MaxLocalDocuments {
			c.Truncated = true
			c.problem(fmt.Sprintf("local corpus exceeds %d documents; rest ignored", MaxLocalDocuments))
			break
		}
		raw, err := os.ReadFile(filepath.Join(dir, name))
		if err != nil {
			c.Skipped++
			c.problem(fmt.Sprintf("%s: %v", name, err))
			continue
		}
		d, err := ValidateDocument(raw)
		if err != nil {
			c.Skipped++
			c.problem(fmt.Sprintf("%s: %v", name, err))
			continue
		}
		if _, dup := c.docs[d.ID]; dup {
			c.Skipped++
			c.problem(fmt.Sprintf("%s: id %s is already defined by an earlier file; this one is ignored", name, d.ID))
			continue
		}
		if name != d.ID+".json" {
			c.problem(fmt.Sprintf("%s: file name does not match its id %s (expected %s.json)", name, d.ID, d.ID))
		}
		c.docs[d.ID] = d
		c.order = append(c.order, d.ID)
	}
	c.indexAbsorbs()
	return c, nil
}
