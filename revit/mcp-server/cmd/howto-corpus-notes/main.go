// howto-corpus-notes prints the "How-tos" section of a release's notes: what
// changed in the shared corpus between the previous release and this one
// (seed plan §1). The release workflow runs it with -old pointing at the
// previous tag's corpus directory (extracted with git archive) and -new at
// the checkout's; a missing -old means a first release, where every document
// is new. Exit status 2 when a script changed without a revision bump, which
// would ship stale verification stamps.
package main

import (
	"flag"
	"fmt"
	"os"

	"github.com/eichler-ai/connectors/revit/mcp-server/internal/howto"
)

func main() {
	oldDir := flag.String("old", "", "previous release's corpus directory (one <id>.json per lineage); empty for a first release")
	newDir := flag.String("new", "", "this release's corpus directory (default: the corpus embedded in this binary)")
	outPath := flag.String("o", "", "write the notes to this file (UTF-8, no BOM) instead of stdout -- Windows PowerShell re-encodes piped stdout")
	flag.Parse()

	var cur *howto.Corpus
	var ver howto.Version
	var err error
	if *newDir == "" {
		cur, _, ver, err = howto.Embedded()
	} else {
		cur, err = howto.LoadLocalDir(*newDir)
		if err == nil {
			ver = howto.Version{Documents: cur.Len()}
		}
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "howto-corpus-notes:", err)
		os.Exit(1)
	}
	var old *howto.Corpus
	if *oldDir != "" {
		old, err = howto.LoadLocalDir(*oldDir)
		if err != nil {
			fmt.Fprintln(os.Stderr, "howto-corpus-notes:", err)
			os.Exit(1)
		}
		if old.Len() == 0 {
			old = nil
		}
	}
	ch := howto.DiffCorpus(old, cur)
	notes := howto.ReleaseNotes(ch, cur, ver)
	if *outPath != "" {
		if err := os.WriteFile(*outPath, []byte(notes), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "howto-corpus-notes:", err)
			os.Exit(1)
		}
	}
	fmt.Print(notes)
	if len(ch.ScriptOnly) > 0 {
		fmt.Fprintf(os.Stderr, "howto-corpus-notes: %d document(s) changed their script without bumping rev: %v\n", len(ch.ScriptOnly), ch.ScriptOnly)
		os.Exit(2)
	}
}
