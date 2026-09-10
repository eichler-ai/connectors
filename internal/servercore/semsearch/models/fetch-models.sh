#!/usr/bin/env sh
# Fetches the pinned search_functions models into assets/ for go:embed.
# Pins (revision + sha256) must match models.go; the build verifies them again at load.
set -eu
cd "$(dirname "$0")/assets"
POTION_REV=bf8b056651a2c21b8d2565580b8569da283cab23
MSMARCO_REV=a09144355adeed5f58c8ed011d209bf8ee5a1fec
fetch() { # url dest sha256
  if [ -f "$2" ] && [ "$(shasum -a 256 "$2" | cut -d' ' -f1)" = "$3" ]; then echo "ok      $2"; return; fi
  mkdir -p "$(dirname "$2")"
  curl -sSL --fail -o "$2.tmp" "$1"
  got="$(shasum -a 256 "$2.tmp" | cut -d' ' -f1)"
  if [ "$got" != "$3" ]; then rm -f "$2.tmp"; echo "sha256 mismatch for $2: got $got want $3" >&2; exit 1; fi
  mv "$2.tmp" "$2"; echo "fetched $2"
}
fetch "https://huggingface.co/minishlab/potion-base-8M/resolve/$POTION_REV/config.json"         potion-base-8M/config.json         2a6ac0e9aaa356a68a5688070db78fc3a464fefe85d2f06a1905ce3718687553
fetch "https://huggingface.co/minishlab/potion-base-8M/resolve/$POTION_REV/tokenizer.json"      potion-base-8M/tokenizer.json      e67e803f624fb4d67dea1c730d06e1067e1b14d830e2c2202569e3ef0f70bb50
fetch "https://huggingface.co/minishlab/potion-base-8M/resolve/$POTION_REV/model.safetensors"   potion-base-8M/model.safetensors   f65d0f325faadc1e121c319e2faa41170d3fa07d8c89abd48ca5358d9a223de2
fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MSMARCO_REV/onnx/model_quantized.onnx" ms-marco-MiniLM-L-6-v2/model.onnx e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe
fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MSMARCO_REV/tokenizer.json"           ms-marco-MiniLM-L-6-v2/tokenizer.json d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66
fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MSMARCO_REV/config.json"              ms-marco-MiniLM-L-6-v2/config.json d827779a72d27ae68cf878a6fc2e954542663fe21ca515d9f4783fc96be2d37e
fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MSMARCO_REV/special_tokens_map.json"  ms-marco-MiniLM-L-6-v2/special_tokens_map.json b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3
