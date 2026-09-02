# Fetches the pinned search_functions models into assets/ for go:embed (Windows PowerShell 5.1).
# Pins (revision + sha256) must match models.go; the build verifies them again at load.
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"  # Invoke-WebRequest is very slow with the progress bar on under Windows PowerShell 5.1
Set-Location (Join-Path $PSScriptRoot "assets")
$PotionRev = "bf8b056651a2c21b8d2565580b8569da283cab23"
$MsMarcoRev = "a09144355adeed5f58c8ed011d209bf8ee5a1fec"
function Fetch($url, $dest, $sha) {
  if ((Test-Path $dest) -and ((Get-FileHash $dest -Algorithm SHA256).Hash.ToLower() -eq $sha)) { Write-Host "ok      $dest"; return }
  New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
  Invoke-WebRequest -Uri $url -OutFile "$dest.tmp" -UseBasicParsing
  $got = (Get-FileHash "$dest.tmp" -Algorithm SHA256).Hash.ToLower()
  if ($got -ne $sha) { Remove-Item "$dest.tmp"; throw "sha256 mismatch for ${dest}: got $got want $sha" }
  Move-Item -Force "$dest.tmp" $dest; Write-Host "fetched $dest"
}
Fetch "https://huggingface.co/minishlab/potion-base-8M/resolve/$PotionRev/config.json"    "potion-base-8M\config.json"    "2a6ac0e9aaa356a68a5688070db78fc3a464fefe85d2f06a1905ce3718687553"
Fetch "https://huggingface.co/minishlab/potion-base-8M/resolve/$PotionRev/tokenizer.json"    "potion-base-8M\tokenizer.json"    "e67e803f624fb4d67dea1c730d06e1067e1b14d830e2c2202569e3ef0f70bb50"
Fetch "https://huggingface.co/minishlab/potion-base-8M/resolve/$PotionRev/model.safetensors" "potion-base-8M\model.safetensors" "f65d0f325faadc1e121c319e2faa41170d3fa07d8c89abd48ca5358d9a223de2"
Fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MsMarcoRev/onnx/model_quantized.onnx" "ms-marco-MiniLM-L-6-v2\model.onnx" "e9d8ebf845c413e981c175bfe49a3bfa9b3dcce2a3ba54875ee5df5a58639fbe"
Fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MsMarcoRev/tokenizer.json"           "ms-marco-MiniLM-L-6-v2\tokenizer.json" "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66"
Fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MsMarcoRev/config.json"              "ms-marco-MiniLM-L-6-v2\config.json" "d827779a72d27ae68cf878a6fc2e954542663fe21ca515d9f4783fc96be2d37e"
Fetch "https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/$MsMarcoRev/special_tokens_map.json"  "ms-marco-MiniLM-L-6-v2\special_tokens_map.json" "b6d346be366a7d1d48332dbc9fdf3bf8960b5d879522b7799ddba59e76237ee3"
