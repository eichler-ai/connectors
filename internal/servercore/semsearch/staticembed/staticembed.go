// Package staticembed is a pure-Go implementation of a model2vec static
// sentence embedder (https://github.com/MinishLab/model2vec): WordPiece
// tokenization, one embedding row per token, mean-pooled and L2-normalised.
//
// Why static embeddings: the design note's POC used a transformer
// bi-encoder (BGE-small), but embedding a 76k-member corpus with a
// transformer in pure Go runs at ~14 docs/s (hours per index build; see
// revit/docs/search-ranking-redesign.md §8.3 measurements). A static model
// embeds the same corpus in seconds, has no runtime dependency, and -- once
// the cross-encoder reranks the fused pool -- matched BGE-small on the
// labelled query set (recall@1 23/43, MRR 0.624 vs 0.629; potion-base-8M).
//
// The model is two files from the HuggingFace repo: tokenizer.json (the
// WordPiece vocabulary and BERT normaliser settings) and model.safetensors
// (one F32 tensor "embeddings" of shape [vocab, dim]).
package staticembed

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"fmt"
	"math"
	"strings"
	"unicode"
	"unicode/utf8"

	"golang.org/x/text/unicode/norm"
)

// Model is a loaded static embedder. Safe for concurrent use.
type Model struct {
	tok       *wordPiece
	table     []float32 // vocab*dim, row-major
	dim       int
	normalize bool
}

// Load parses tokenizer.json and model.safetensors contents. normalize
// mirrors the model's config.json "normalize" flag (true for potion models).
func Load(tokenizerJSON, safetensors []byte, normalize bool) (*Model, error) {
	tok, err := newWordPiece(tokenizerJSON)
	if err != nil {
		return nil, fmt.Errorf("tokenizer.json: %w", err)
	}
	table, shape, err := readEmbeddings(safetensors)
	if err != nil {
		return nil, fmt.Errorf("model.safetensors: %w", err)
	}
	if shape[0] != len(tok.idToToken) {
		return nil, fmt.Errorf("model.safetensors has %d rows but tokenizer.json has %d tokens", shape[0], len(tok.idToToken))
	}
	return &Model{tok: tok, table: table, dim: shape[1], normalize: normalize}, nil
}

// Dim is the embedding width.
func (m *Model) Dim() int { return m.dim }

// Tokenize returns the token ids model2vec would feed the embedding table:
// no special tokens, unknown-token ids dropped.
func (m *Model) Tokenize(text string) []int {
	ids := m.tok.encode(text)
	out := ids[:0]
	for _, id := range ids {
		if id != m.tok.unkID {
			out = append(out, id)
		}
	}
	return out
}

// Embed implements semsearch.Embedder. A text with no known tokens embeds
// to the zero vector.
func (m *Model) Embed(_ context.Context, texts []string) ([][]float32, error) {
	out := make([][]float32, len(texts))
	for i, t := range texts {
		out[i] = m.embedOne(t)
	}
	return out, nil
}

func (m *Model) embedOne(text string) []float32 {
	v := make([]float32, m.dim)
	ids := m.Tokenize(text)
	if len(ids) == 0 {
		return v
	}
	acc := make([]float64, m.dim)
	for _, id := range ids {
		row := m.table[id*m.dim : (id+1)*m.dim]
		for k, x := range row {
			acc[k] += float64(x)
		}
	}
	inv := 1 / float64(len(ids))
	var norm2 float64
	for k := range acc {
		acc[k] *= inv
		norm2 += acc[k] * acc[k]
	}
	scale := 1.0
	if m.normalize && norm2 > 0 {
		scale = 1 / math.Sqrt(norm2)
	}
	for k := range acc {
		v[k] = float32(acc[k] * scale)
	}
	return v
}

// --- safetensors -------------------------------------------------------------

// readEmbeddings parses the safetensors container (8-byte little-endian
// header length, JSON header, raw tensor bytes) and returns the F32 tensor
// named "embeddings" and its 2-D shape.
func readEmbeddings(b []byte) ([]float32, [2]int, error) {
	var shape [2]int
	if len(b) < 8 {
		return nil, shape, fmt.Errorf("too short (%d bytes)", len(b))
	}
	hlen := binary.LittleEndian.Uint64(b[:8])
	if hlen > uint64(len(b)-8) {
		return nil, shape, fmt.Errorf("header length %d exceeds file", hlen)
	}
	var header map[string]struct {
		Dtype       string `json:"dtype"`
		Shape       []int  `json:"shape"`
		DataOffsets []int  `json:"data_offsets"`
	}
	if err := json.Unmarshal(b[8:8+hlen], &header); err != nil {
		return nil, shape, err
	}
	t, ok := header["embeddings"]
	if !ok {
		return nil, shape, fmt.Errorf("no \"embeddings\" tensor in header")
	}
	if t.Dtype != "F32" || len(t.Shape) != 2 || len(t.DataOffsets) != 2 {
		return nil, shape, fmt.Errorf("embeddings tensor must be F32 [vocab, dim], got %s %v", t.Dtype, t.Shape)
	}
	data := b[8+hlen:]
	start, end := t.DataOffsets[0], t.DataOffsets[1]
	if start < 0 || end > len(data) || end < start {
		return nil, shape, fmt.Errorf("data_offsets %v outside %d data bytes", t.DataOffsets, len(data))
	}
	n := t.Shape[0] * t.Shape[1]
	if (end-start)/4 != n {
		return nil, shape, fmt.Errorf("tensor bytes %d do not match shape %v", end-start, t.Shape)
	}
	out := make([]float32, n)
	for i := range out {
		out[i] = math.Float32frombits(binary.LittleEndian.Uint32(data[start+4*i:]))
	}
	shape = [2]int{t.Shape[0], t.Shape[1]}
	return out, shape, nil
}

// --- WordPiece tokenizer (HF tokenizers "BertNormalizer + BertPreTokenizer
// + WordPiece" subset, which is what model2vec's BERT-derived vocabularies use)

type wordPiece struct {
	vocab        map[string]int
	idToToken    []string
	unkID        int
	prefix       string
	maxWordChars int
	lowercase    bool
	stripAccents bool
}

func newWordPiece(tokenizerJSON []byte) (*wordPiece, error) {
	var tj struct {
		Normalizer *struct {
			Type         string `json:"type"`
			Lowercase    *bool  `json:"lowercase"`
			StripAccents *bool  `json:"strip_accents"`
		} `json:"normalizer"`
		PreTokenizer *struct {
			Type string `json:"type"`
		} `json:"pre_tokenizer"`
		Model struct {
			Type                    string         `json:"type"`
			UnkToken                string         `json:"unk_token"`
			ContinuingSubwordPrefix string         `json:"continuing_subword_prefix"`
			MaxInputCharsPerWord    int            `json:"max_input_chars_per_word"`
			Vocab                   map[string]int `json:"vocab"`
		} `json:"model"`
	}
	if err := json.Unmarshal(tokenizerJSON, &tj); err != nil {
		return nil, err
	}
	if tj.Model.Type != "WordPiece" {
		return nil, fmt.Errorf("unsupported tokenizer model %q (only WordPiece)", tj.Model.Type)
	}
	if tj.Normalizer == nil || tj.Normalizer.Type != "BertNormalizer" {
		return nil, fmt.Errorf("unsupported normalizer (only BertNormalizer)")
	}
	if tj.PreTokenizer == nil || tj.PreTokenizer.Type != "BertPreTokenizer" {
		return nil, fmt.Errorf("unsupported pre_tokenizer (only BertPreTokenizer)")
	}
	wp := &wordPiece{
		vocab:        tj.Model.Vocab,
		prefix:       tj.Model.ContinuingSubwordPrefix,
		maxWordChars: tj.Model.MaxInputCharsPerWord,
		lowercase:    tj.Normalizer.Lowercase == nil || *tj.Normalizer.Lowercase,
	}
	// HF semantics: strip_accents defaults to the lowercase setting.
	if tj.Normalizer.StripAccents != nil {
		wp.stripAccents = *tj.Normalizer.StripAccents
	} else {
		wp.stripAccents = wp.lowercase
	}
	if wp.prefix == "" {
		wp.prefix = "##"
	}
	if wp.maxWordChars == 0 {
		wp.maxWordChars = 100
	}
	maxID := -1
	for _, id := range wp.vocab {
		if id > maxID {
			maxID = id
		}
	}
	wp.idToToken = make([]string, maxID+1)
	for t, id := range wp.vocab {
		wp.idToToken[id] = t
	}
	unk, ok := wp.vocab[tj.Model.UnkToken]
	if !ok {
		return nil, fmt.Errorf("unk_token %q not in vocab", tj.Model.UnkToken)
	}
	wp.unkID = unk
	return wp, nil
}

// encode runs normalise -> pre-tokenise -> WordPiece, without special tokens.
func (wp *wordPiece) encode(text string) []int {
	var ids []int
	for _, word := range wp.preTokenize(wp.normalize(text)) {
		ids = wp.wordPieceIDs(word, ids)
	}
	return ids
}

// normalize is HF BertNormalizer: clean_text (drop control chars, fold
// whitespace to ' '), pad CJK ideographs with spaces, lowercase, and strip
// combining accents via NFD.
func (wp *wordPiece) normalize(text string) string {
	var b strings.Builder
	b.Grow(len(text))
	for _, r := range text {
		switch {
		case r == 0 || r == 0xFFFD || (unicode.IsControl(r) && !isBertWhitespace(r)):
			continue
		case isBertWhitespace(r):
			b.WriteByte(' ')
		case isCJK(r):
			b.WriteByte(' ')
			b.WriteRune(r)
			b.WriteByte(' ')
		default:
			b.WriteRune(r)
		}
	}
	s := b.String()
	if wp.stripAccents {
		s = norm.NFD.String(s)
		var sb strings.Builder
		sb.Grow(len(s))
		for _, r := range s {
			if !unicode.Is(unicode.Mn, r) {
				sb.WriteRune(r)
			}
		}
		s = sb.String()
	}
	if wp.lowercase {
		s = strings.ToLower(s)
	}
	return s
}

// preTokenize is HF BertPreTokenizer: split on whitespace, and every
// punctuation rune becomes its own word.
func (wp *wordPiece) preTokenize(s string) []string {
	var words []string
	var cur strings.Builder
	flush := func() {
		if cur.Len() > 0 {
			words = append(words, cur.String())
			cur.Reset()
		}
	}
	for _, r := range s {
		switch {
		case isBertWhitespace(r):
			flush()
		case isBertPunct(r):
			flush()
			words = append(words, string(r))
		default:
			cur.WriteRune(r)
		}
	}
	flush()
	return words
}

// wordPieceIDs appends the greedy longest-match-first pieces of word, or the
// unknown id if the word is too long or any remainder has no match.
func (wp *wordPiece) wordPieceIDs(word string, ids []int) []int {
	if utf8.RuneCountInString(word) > wp.maxWordChars {
		return append(ids, wp.unkID)
	}
	runes := []rune(word)
	var pieces []int
	start := 0
	for start < len(runes) {
		end := len(runes)
		found := -1
		for end > start {
			sub := string(runes[start:end])
			if start > 0 {
				sub = wp.prefix + sub
			}
			if id, ok := wp.vocab[sub]; ok {
				found = id
				break
			}
			end--
		}
		if found < 0 {
			return append(ids, wp.unkID)
		}
		pieces = append(pieces, found)
		start = end
	}
	return append(ids, pieces...)
}

func isBertWhitespace(r rune) bool {
	return r == ' ' || r == '\t' || r == '\n' || r == '\r' || unicode.Is(unicode.Zs, r)
}

// isBertPunct mirrors HF's _is_punctuation: the four ASCII symbol ranges plus
// any Unicode P* category.
func isBertPunct(r rune) bool {
	if (r >= 33 && r <= 47) || (r >= 58 && r <= 64) || (r >= 91 && r <= 96) || (r >= 123 && r <= 126) {
		return true
	}
	return unicode.IsPunct(r)
}

func isCJK(r rune) bool {
	return (r >= 0x4E00 && r <= 0x9FFF) || (r >= 0x3400 && r <= 0x4DBF) ||
		(r >= 0x20000 && r <= 0x2A6DF) || (r >= 0x2A700 && r <= 0x2B73F) ||
		(r >= 0x2B740 && r <= 0x2B81F) || (r >= 0x2B820 && r <= 0x2CEAF) ||
		(r >= 0xF900 && r <= 0xFAFF) || (r >= 0x2F800 && r <= 0x2FA1F)
}
