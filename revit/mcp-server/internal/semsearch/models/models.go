// Package models bundles the two search_functions models into the broker
// binary so the connector stays a single self-contained executable (PRD §04:
// no runtime DLL or download dependency).
//
// The model files are NOT committed: they are fetched at build time by
// fetch-models.sh / fetch-models.ps1 into assets/, verified against the
// SHA-256 pins below, and picked up by go:embed. A build without them still
// compiles -- Available reports false and search_functions degrades to
// lexical-only with a notice -- so `go test ./...` never depends on a
// network fetch, while the release workflow asserts the models are present.
//
// Models (both permissively licensed):
//   - minishlab/potion-base-8M (MIT), revision bf8b056 -- static embedder
//     read by internal/semsearch/staticembed.
//   - Xenova/ms-marco-MiniLM-L-6-v2 (Apache-2.0) int8 ONNX export of
//     cross-encoder/ms-marco-MiniLM-L-6-v2 -- reranker read by
//     internal/semsearch/crossenc through hugot, which needs the files on
//     disk (Materialize).
package models

import (
	"crypto/sha256"
	"embed"
	"encoding/hex"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
)

//go:embed assets
var assets embed.FS

// Pinned file set. Path is relative to assets/; the fetch scripts and this
// table must agree, and Verify enforces the pins at load time so a tampered
// or half-downloaded file fails loudly rather than ranking silently wrong.
var pins = map[string]string{
	"potion-base-8M/tokenizer.json":                  "e67e803f624fb4d67dea1c730d06e1067e1b14d830e2c2202569e3ef0f70bb50",
	"potion-base-8M/model.safetensors":               "f65d0f325faadc1e121c319e2faa41170d3fa07d8c89abd48ca5358d9a223de2",
	"ms-marco-MiniLM-L-6-v2/model.onnx":              "e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe",
	"ms-marco-MiniLM-L-6-v2/tokenizer.json":          "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
	"ms-marco-MiniLM-L-6-v2/config.json":             "d827779a72d27ae68cf878a6fc2e954542663fe21ca515d9f4783fc96be2d37e",
	"ms-marco-MiniLM-L-6-v2/special_tokens_map.json": "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3",
}

const (
	embedderDir = "potion-base-8M"
	rerankerDir = "ms-marco-MiniLM-L-6-v2"
)

// Available reports whether every pinned file was embedded. It does not
// verify checksums (Verify does); it answers "was the fetch step run".
func Available() bool {
	for p := range pins {
		if _, err := fs.Stat(assets, "assets/"+p); err != nil {
			return false
		}
	}
	return true
}

// Missing lists the pinned files absent from the build, for diagnostics.
func Missing() []string {
	var out []string
	for p := range pins {
		if _, err := fs.Stat(assets, "assets/"+p); err != nil {
			out = append(out, p)
		}
	}
	return out
}

// Verify checks every embedded file against its pin.
func Verify() error {
	for p, want := range pins {
		b, err := assets.ReadFile("assets/" + p)
		if err != nil {
			return fmt.Errorf("models: %s not embedded (run fetch-models before building): %w", p, err)
		}
		sum := sha256.Sum256(b)
		if got := hex.EncodeToString(sum[:]); got != want {
			return fmt.Errorf("models: %s sha256 %s does not match pin %s", p, got, want)
		}
	}
	return nil
}

// Embedder returns the static embedder's tokenizer.json and model.safetensors.
func Embedder() (tokenizerJSON, safetensors []byte, err error) {
	tokenizerJSON, err = read(embedderDir + "/tokenizer.json")
	if err != nil {
		return nil, nil, err
	}
	safetensors, err = read(embedderDir + "/model.safetensors")
	if err != nil {
		return nil, nil, err
	}
	return tokenizerJSON, safetensors, nil
}

// Materialize writes the reranker's files under dir (creating
// dir/ms-marco-MiniLM-L-6-v2) and returns that model directory, for a loader
// that needs real paths. Files already present with the right size are left
// alone, so a broker restart does not rewrite 23MB.
func Materialize(dir string) (string, error) {
	out := filepath.Join(dir, rerankerDir)
	if err := os.MkdirAll(out, 0o755); err != nil {
		return "", err
	}
	for p := range pins {
		if filepath.Dir(p) != rerankerDir {
			continue
		}
		b, err := read(p)
		if err != nil {
			return "", err
		}
		dst := filepath.Join(out, filepath.Base(p))
		if st, err := os.Stat(dst); err == nil && st.Size() == int64(len(b)) {
			continue
		}
		tmp := dst + ".tmp"
		if err := os.WriteFile(tmp, b, 0o644); err != nil {
			return "", err
		}
		if err := os.Rename(tmp, dst); err != nil {
			return "", err
		}
	}
	return out, nil
}

func read(p string) ([]byte, error) {
	b, err := assets.ReadFile("assets/" + p)
	if err != nil {
		return nil, fmt.Errorf("models: %s not embedded (run fetch-models before building): %w", p, err)
	}
	want, ok := pins[p]
	if !ok {
		return nil, fmt.Errorf("models: %s has no pin", p)
	}
	sum := sha256.Sum256(b)
	if got := hex.EncodeToString(sum[:]); got != want {
		return nil, fmt.Errorf("models: %s sha256 %s does not match pin %s", p, got, want)
	}
	return b, nil
}
