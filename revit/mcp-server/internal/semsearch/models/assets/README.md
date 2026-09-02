# Bundled search_functions models (fetched, not committed)

`go:embed` picks up everything in this directory. The two model directories are
gitignored and produced by `../fetch-models.sh` (macOS/Linux) or `../fetch-models.ps1`
(Windows), which download pinned revisions from Hugging Face and verify them against the
SHA-256 pins in `../models.go`:

- `potion-base-8M/` — `config.json`, `tokenizer.json`, `model.safetensors` (minishlab/potion-base-8M, MIT)
- `ms-marco-MiniLM-L-6-v2/` — `model.onnx` (int8), `tokenizer.json`, `config.json`,
  `special_tokens_map.json` (Xenova/ms-marco-MiniLM-L-6-v2, Apache-2.0)

A build without them still compiles; `models.Available()` reports false and
`search_functions` runs lexical-only with a notice. The release workflow refuses to ship
such a build. This file is the one committed entry so the embed directory always exists.
